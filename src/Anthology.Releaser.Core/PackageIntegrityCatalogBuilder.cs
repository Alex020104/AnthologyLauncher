using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthology.Contracts;
using Anthology.Update.Core;

namespace Anthology.Releaser.Core;

public sealed record PackageIntegrityBuildResult(PackageManifest Package, string ArtifactPath, string CatalogPath);

public static class PackageIntegrityCatalogBuilder
{
    public const string PackageId = "anthology-integrity";
    public const string CatalogRelativePath = "AnthologyLauncher/Update/Integrity/package-integrity.json";

    private const string QuickModpackPackageId = "anthology-files-modpack";
    private static readonly string[] ModpackUserOwnedRoots =
    [
        "profiles",
        "downloads",
        "overwrite",
        "logs",
        "crash_dumps",
        "webcache",
    ];

    public static async Task<PackageIntegrityBuildResult?> BuildAsync(
        IReadOnlyList<PackageManifest> currentPackages,
        string versionRoot,
        ReleaserWorkspace workspace,
        ECDsa privateKey,
        string keyId,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentPackages);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(privateKey);
        var root = Path.GetFullPath(versionRoot);
        var channel = string.IsNullOrWhiteSpace(workspace.Channel) ? "next" : workspace.Channel.Trim().ToLowerInvariant();
        var resolvedArtifacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fileHashCache = new Dictionary<string, IReadOnlyDictionary<string, PackageFileIntegrity>>(StringComparer.OrdinalIgnoreCase);
        var previousCatalog = await LoadLatestIntegrityCatalogAsync(
            root,
            channel,
            privateKey,
            cancellationToken);
        var managed = previousCatalog is null
            ? BuildManagedView(await LoadHistoricalArtifactsAsync(
                root,
                channel,
                privateKey,
                resolvedArtifacts,
                fileHashCache,
                progress,
                cancellationToken))
            : CreateManagedView(previousCatalog.Payload);
        var sources = new List<ArtifactSource>();
        foreach (var currentPackage in currentPackages.Where(IsProtectedPackage))
        {
            if (previousCatalog?.Payload.Artifacts.Any(artifact =>
                    string.Equals(artifact.PackageId, currentPackage.Id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        artifact.RequiredPackageVersion,
                        currentPackage.Version,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        artifact.ArchiveSha256,
                        currentPackage.Sha256,
                        StringComparison.OrdinalIgnoreCase)) == true)
            {
                continue;
            }
            var currentArtifactPath = await ResolveArtifactAsync(
                root,
                workspace.Version,
                currentPackage,
                resolvedArtifacts,
                cancellationToken);
            sources.Add(await LoadArtifactAsync(
                currentPackage,
                currentArtifactPath,
                DateTimeOffset.MaxValue,
                fileHashCache,
                progress,
                cancellationToken));
        }
        ApplyManagedView(managed, sources.OrderBy(source => source.PublishedAt));
        ApplyManagedPathPolicy(managed, currentPackages);
        var activePackageIds = currentPackages
            .Where(IsProtectedPackage)
            .Select(package => package.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in managed.Keys
                     .Where(key => !activePackageIds.Contains(managed[key].OwnerPackageId))
                     .ToArray())
        {
            managed.Remove(key);
        }
        if (managed.Count == 0)
        {
            return null;
        }

        var artifacts = managed.Values
            .GroupBy(item => new
            {
                ArchiveSha256 = item.Source.Package.Sha256.ToLowerInvariant(),
                InstallRoot = item.Source.Package.InstallRoot.ToLowerInvariant(),
                OriginPackageVersion = item.Source.Package.Version.ToLowerInvariant(),
                OwnerPackageId = item.OwnerPackageId.ToLowerInvariant(),
                RequiredPackageVersion = item.RequiredPackageVersion.ToLowerInvariant(),
            })
            .Select(group =>
            {
                var first = group.First();
                var package = first.Source.Package;
                var identity = string.Join('|',
                    package.Sha256.ToLowerInvariant(),
                    package.InstallRoot.ToLowerInvariant(),
                    first.OwnerPackageId.ToLowerInvariant(),
                    first.RequiredPackageVersion.ToLowerInvariant());
                var identityHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                return new PackageArtifactIntegrity(
                    $"artifact-{identityHash[..32]}",
                    first.OwnerPackageId,
                    package.Version,
                    first.RequiredPackageVersion,
                    package.Kind,
                    package.InstallRoot,
                    package.ArchiveFormat,
                    package.Size,
                    package.Sha256.ToLowerInvariant(),
                    package.Mirrors,
                    first.Source.Files.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
                    group.Select(item => item.File.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray());
            })
            .OrderBy(artifact => artifact.InstallRoot, StringComparer.OrdinalIgnoreCase)
            .ThenBy(artifact => artifact.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(artifact => artifact.ArtifactId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var payload = new PackageIntegrityCatalog(1, channel, workspace.Version.Trim(), DateTimeOffset.UtcNow, artifacts);
        var signed = ManifestSecurity.Sign(payload, privateKey, keyId);
        PackageIntegrityCatalogValidator.ValidateAndThrow(signed);

        var catalogPath = Path.Combine(root, "package-integrity.json");
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(catalogPath, signed, cancellationToken);
        var catalogHash = await ArtifactHash.ComputeSha256Async(catalogPath, cancellationToken);
        var artifactName = $"anthology-integrity-{catalogHash[..16]}.zip";
        var artifactPath = Path.Combine(root, artifactName);
        await CreateCatalogArchiveAsync(catalogPath, artifactPath, cancellationToken);
        var artifactSize = new FileInfo(artifactPath).Length;
        var mirrors = workspace.Mirrors
            .Where(mirror => !string.IsNullOrWhiteSpace(mirror.GameUrl))
            .Where(mirror => UnifiedReleaseBuilder.SupportsArtifact(mirror.Provider, artifactSize))
            .Select(mirror => new MirrorManifest(
                UnifiedReleaseBuilder.NormalizeProvider(mirror.Provider),
                UnifiedReleaseBuilder.ExpandUrl(mirror.GameUrl.Trim(), workspace.Version, PackageId, artifactName),
                mirror.Priority))
            .OrderBy(mirror => mirror.Priority)
            .ToArray();
        if (mirrors.Length == 0)
        {
            mirrors = [new MirrorManifest("local-file", new Uri(artifactPath).AbsoluteUri, 1000)];
        }

        var package = new PackageManifest(
            PackageId,
            "Каталог целостности Anthology",
            catalogHash,
            PackageKind.Launcher,
            "game",
            "zip",
            artifactSize,
            await ArtifactHash.ComputeSha256Async(artifactPath, cancellationToken),
            mirrors,
            [CatalogRelativePath],
            PackageUpdateMode.Merge);
        return new PackageIntegrityBuildResult(package, artifactPath, catalogPath);
    }

    private static async Task<SignedPackageIntegrityCatalog?> LoadLatestIntegrityCatalogAsync(
        string currentVersionRoot,
        string channel,
        ECDsa trustedKey,
        CancellationToken cancellationToken)
    {
        var releasesRoot = Directory.GetParent(currentVersionRoot)?.FullName;
        if (releasesRoot is null || !Directory.Exists(releasesRoot))
        {
            return null;
        }

        SignedPackageIntegrityCatalog? latest = null;
        var catalogPaths = Directory.EnumerateDirectories(releasesRoot)
            .Select(directory => Path.Combine(directory, "package-integrity.json"))
            .Where(File.Exists)
            .ToArray();
        foreach (var path in catalogPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var catalog = await JsonSerializer.DeserializeAsync<SignedPackageIntegrityCatalog>(
                    stream,
                    ManifestJson.Options,
                    cancellationToken);
                if (catalog is null
                    || !string.Equals(catalog.Payload.Channel, channel, StringComparison.OrdinalIgnoreCase)
                    || !ManifestSecurity.Verify(catalog, trustedKey))
                {
                    continue;
                }
                PackageIntegrityCatalogValidator.ValidateAndThrow(catalog);
                if (latest is null || catalog.Payload.PublishedAt > latest.Payload.PublishedAt)
                {
                    latest = catalog;
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException
                                               or InvalidDataException
                                               or CryptographicException)
            {
                // Ignore incomplete build output. It is never used as signed history.
            }
        }
        return latest;
    }

    private static async Task<List<ArtifactSource>> LoadHistoricalArtifactsAsync(
        string currentVersionRoot,
        string channel,
        ECDsa trustedKey,
        Dictionary<string, string> resolvedArtifacts,
        Dictionary<string, IReadOnlyDictionary<string, PackageFileIntegrity>> fileHashCache,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = new List<ArtifactSource>();
        var releasesRoot = Directory.GetParent(currentVersionRoot)?.FullName;
        if (releasesRoot is null || !Directory.Exists(releasesRoot))
        {
            return result;
        }

        var manifests = Directory.EnumerateDirectories(releasesRoot)
            .Where(path => !string.Equals(Path.GetFullPath(path), currentVersionRoot, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.Combine(path, "manifest.json"))
            .Where(File.Exists)
            .ToArray();
        var releases = new List<(string Root, SignedUpdateManifest Manifest)>();
        foreach (var manifestPath in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options, cancellationToken);
                if (manifest is null
                    || !string.Equals(manifest.Payload.Channel, channel, StringComparison.OrdinalIgnoreCase)
                    || !ManifestSecurity.Verify(manifest, trustedKey))
                {
                    continue;
                }
                ManifestValidator.ValidateAndThrow(manifest);
                releases.Add((Path.GetDirectoryName(manifestPath)!, manifest));
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                // Ignore incomplete unrelated build output; never sign it into the catalog.
            }
        }

        foreach (var release in releases.OrderBy(item => item.Manifest.Payload.PublishedAt))
        {
            foreach (var package in release.Manifest.Payload.Packages.Where(IsProtectedPackage))
            {
                var artifactPath = await ResolveArtifactAsync(
                    release.Root,
                    release.Manifest.Payload.Version,
                    package,
                    resolvedArtifacts,
                    cancellationToken);
                result.Add(await LoadArtifactAsync(
                    package,
                    artifactPath,
                    release.Manifest.Payload.PublishedAt,
                    fileHashCache,
                    progress,
                    cancellationToken));
            }
        }
        return result;
    }

    private static Dictionary<string, ManagedFile> BuildManagedView(IReadOnlyList<ArtifactSource> sources)
    {
        var managed = new Dictionary<string, ManagedFile>(StringComparer.OrdinalIgnoreCase);
        ApplyManagedView(managed, sources.OrderBy(source => source.PublishedAt));
        return managed;
    }

    private static Dictionary<string, ManagedFile> CreateManagedView(PackageIntegrityCatalog catalog)
    {
        var managed = new Dictionary<string, ManagedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in catalog.Artifacts)
        {
            var package = new PackageManifest(
                artifact.PackageId,
                artifact.PackageId,
                artifact.PackageVersion,
                artifact.Kind,
                artifact.InstallRoot,
                artifact.ArchiveFormat,
                artifact.ArchiveSize,
                artifact.ArchiveSha256,
                artifact.Mirrors,
                artifact.ArchiveFiles.Select(file => file.Path).ToArray(),
                PackageUpdateMode.Merge);
            var files = artifact.ArchiveFiles.ToDictionary(
                file => PathSafety.NormalizeRelativePath(file.Path),
                StringComparer.OrdinalIgnoreCase);
            var source = new ArtifactSource(package, catalog.PublishedAt, files);
            foreach (var path in artifact.ManagedFiles)
            {
                var normalized = PathSafety.NormalizeRelativePath(path);
                managed[artifact.InstallRoot + "|" + normalized] = new ManagedFile(
                    source,
                    files[normalized],
                    artifact.PackageId,
                    artifact.RequiredPackageVersion);
            }
        }
        return managed;
    }

    private static void ApplyManagedView(
        Dictionary<string, ManagedFile> managed,
        IEnumerable<ArtifactSource> sources)
    {
        foreach (var source in sources)
        {
            var package = source.Package;
            var rootPrefix = package.InstallRoot + "|";
            if (package.UpdateMode == PackageUpdateMode.ManagedExact || package.PruneInstallRoot)
            {
                foreach (var key in managed.Keys.Where(key => key.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    managed.Remove(key);
                }
            }
            foreach (var path in package.DeletedFiles ?? [])
            {
                managed.Remove(rootPrefix + PathSafety.NormalizeRelativePath(path));
            }
            foreach (var directory in package.DeletedDirectories ?? [])
            {
                var prefix = rootPrefix + PathSafety.NormalizeRelativePath(directory).TrimEnd('/') + "/";
                foreach (var key in managed.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    managed.Remove(key);
                }
            }
            foreach (var key in managed.Keys.ToArray())
            {
                var current = managed[key];
                if (string.Equals(current.OwnerPackageId, package.Id, StringComparison.OrdinalIgnoreCase))
                {
                    managed[key] = current with { RequiredPackageVersion = package.Version };
                }
            }
            foreach (var file in source.Files.Values)
            {
                managed[rootPrefix + file.Path] = new ManagedFile(source, file, package.Id, package.Version);
            }
        }
    }

    private static void ApplyManagedPathPolicy(
        Dictionary<string, ManagedFile> managed,
        IReadOnlyList<PackageManifest> currentPackages)
    {
        // ManagedFiles are automatic repair targets. MO2 user state is allowed to
        // exist in an origin archive, but must never be restored over player data.
        var correctlyMappedQuickModpackPackages = currentPackages
            .Where(UsesCanonicalQuickModpackLayout)
            .Select(package => package.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in managed.ToArray())
        {
            var item = pair.Value;
            var installRoot = item.Source.Package.InstallRoot;
            var path = PathSafety.NormalizeRelativePath(item.File.Path);
            if (IsModpackUserOwnedPath(installRoot, path)
                || correctlyMappedQuickModpackPackages.Contains(item.OwnerPackageId)
                   && installRoot.Equals("modpack", StringComparison.OrdinalIgnoreCase)
                   && !path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
            {
                managed.Remove(pair.Key);
            }
        }
    }

    private static bool UsesCanonicalQuickModpackLayout(PackageManifest package)
    {
        if (!package.Id.Equals(QuickModpackPackageId, StringComparison.OrdinalIgnoreCase)
            || !package.InstallRoot.Equals("modpack", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var managedCandidates = package.Files
            .Concat(package.DeletedFiles ?? [])
            .Concat(package.DeletedDirectories ?? [])
            .Select(PathSafety.NormalizeRelativePath)
            .Where(path => !IsModpackUserOwnedPath(package.InstallRoot, path))
            .ToArray();
        return managedCandidates.Length > 0
               && managedCandidates.All(path => path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsModpackUserOwnedPath(string installRoot, string path) =>
        installRoot.Equals("modpack", StringComparison.OrdinalIgnoreCase)
        && (path.Equals("ModOrganizer.ini", StringComparison.OrdinalIgnoreCase)
            || ModpackUserOwnedRoots.Any(root => IsAtOrBelow(path, root)));

    private static bool IsAtOrBelow(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    private static bool IsProtectedPackage(PackageManifest package) =>
        package.Kind != PackageKind.Launcher
        && !string.Equals(package.Id, PackageId, StringComparison.OrdinalIgnoreCase);

    private static async Task<ArtifactSource> LoadArtifactAsync(
        PackageManifest package,
        string artifactPath,
        DateTimeOffset publishedAt,
        Dictionary<string, IReadOnlyDictionary<string, PackageFileIntegrity>> fileHashCache,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report($"Проверка архива и хешей файлов: {package.DisplayName}…");
        var info = new FileInfo(artifactPath);
        if (info.Length != package.Size)
        {
            throw new InvalidDataException($"Размер локального архива '{package.Id}' не совпадает с manifest.json.");
        }
        var cacheKey = $"{package.Size}:{package.Sha256}";
        if (!fileHashCache.TryGetValue(cacheKey, out var files))
        {
            var hashedFiles = await ReadFileHashesAsync(artifactPath, package, cancellationToken);
            files = hashedFiles.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            fileHashCache[cacheKey] = files;
        }
        else
        {
            var expected = package.Files.Select(PathSafety.NormalizeRelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!expected.SetEquals(files.Keys))
            {
                throw new InvalidDataException($"Package '{package.Id}' declares a different file set for an already known archive hash.");
            }
        }
        return new ArtifactSource(package, publishedAt, files);
    }

    private static async Task<string> ResolveArtifactAsync(
        string versionRoot,
        string releaseVersion,
        PackageManifest package,
        Dictionary<string, string> cache,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{package.Size}:{package.Sha256}";
        if (cache.TryGetValue(cacheKey, out var cached) && File.Exists(cached))
        {
            return cached;
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(versionRoot, $"{package.Id}-{releaseVersion.Trim()}.zip"),
            Path.Combine(versionRoot, $"{package.Id}-{package.Version.Trim()}.zip"),
        };
        foreach (var mirror in package.Mirrors)
        {
            if (!Uri.TryCreate(mirror.Url, UriKind.Absolute, out var uri))
            {
                continue;
            }
            if (uri.IsFile)
            {
                candidates.Add(uri.LocalPath);
            }
            var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(name))
            {
                candidates.Add(Path.Combine(versionRoot, name));
            }
        }

        var releasesRoot = Directory.GetParent(Path.GetFullPath(versionRoot))?.FullName;
        if (releasesRoot is not null && Directory.Exists(releasesRoot))
        {
            var releaseDirectories = Directory.EnumerateDirectories(releasesRoot)
                .Where(path => !string.Equals(
                    Path.GetFileName(path),
                    ".releaser-trash",
                    StringComparison.OrdinalIgnoreCase));
            foreach (var path in releaseDirectories.SelectMany(directory =>
                         Directory.EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (new FileInfo(path).Length == package.Size)
                {
                    candidates.Add(path);
                }
            }
        }

        foreach (var path in candidates.Where(File.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(path).Length != package.Size)
            {
                continue;
            }
            var hash = await ArtifactHash.ComputeSha256Async(path, cancellationToken);
            if (string.Equals(hash, package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                var resolved = Path.GetFullPath(path);
                cache[cacheKey] = resolved;
                return resolved;
            }
        }

        throw new FileNotFoundException(
            $"Локальный архив пакета '{package.Id}' с SHA-256 {package.Sha256} не найден; каталог целостности нельзя построить безопасно.");
    }

    private static async Task<IReadOnlyList<PackageFileIntegrity>> ReadFileHashesAsync(
        string artifactPath,
        PackageManifest package,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(artifactPath);
        var expected = package.Files.Select(PathSafety.NormalizeRelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<PackageFileIntegrity>(expected.Count);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }
            var relativePath = PathSafety.NormalizeRelativePath(entry.FullName);
            if (!expected.Remove(relativePath))
            {
                throw new InvalidDataException($"Архив '{package.Id}' содержит незаявленный или повторный файл '{relativePath}'.");
            }
            await using var stream = entry.Open();
            result.Add(new PackageFileIntegrity(relativePath, entry.Length, await ArtifactHash.ComputeSha256Async(stream, cancellationToken)));
        }
        if (expected.Count > 0)
        {
            throw new InvalidDataException($"В архиве '{package.Id}' отсутствует {expected.Count} заявленных файлов.");
        }
        return result.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task CreateCatalogArchiveAsync(string catalogPath, string artifactPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        var temporary = artifactPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            var entry = archive.CreateEntry(CatalogRelativePath, CompressionLevel.SmallestSize);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await using var target = entry.Open();
            await using var source = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, cancellationToken);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
        File.Move(temporary, artifactPath, true);
    }

    private sealed record ArtifactSource(
        PackageManifest Package,
        DateTimeOffset PublishedAt,
        IReadOnlyDictionary<string, PackageFileIntegrity> Files);

    private sealed record ManagedFile(
        ArtifactSource Source,
        PackageFileIntegrity File,
        string OwnerPackageId,
        string RequiredPackageVersion);
}
