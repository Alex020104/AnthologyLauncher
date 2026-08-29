using System.Security.Cryptography;
using System.Text.Json;
using Anthology.Contracts;

namespace Anthology.Update.Core;

public enum UpdateStage
{
    Checking,
    Downloading,
    Verifying,
    Extracting,
    Installing,
    RollingBack,
    Completed,
}

public sealed record UpdateProgress(
    UpdateStage Stage,
    string Message,
    string? PackageId = null,
    long DownloadedBytes = 0,
    long TotalBytes = 0,
    string? Provider = null);

public sealed record PackageUpdate(
    PackageManifest Package,
    string? InstalledVersion,
    bool UpdateAvailable);

public sealed record UpdateCheckResult(
    SignedUpdateManifest SignedManifest,
    IReadOnlyList<PackageUpdate> Packages,
    string TrustedKeyId)
{
    public bool HasUpdates => Packages.Any(package => package.UpdateAvailable);
}

public sealed record UpdateApplyResult(
    int InstalledPackages,
    int InstalledFiles,
    int DeletedFiles = 0);

public sealed record UpdateRollbackCandidate(
    string PackageId,
    string DisplayName,
    string? FromVersion,
    string ToVersion,
    string InstallRoot,
    string OperationId,
    DateTimeOffset InstalledAt,
    int PackageCount = 1);

public sealed record UpdateRollbackResult(
    string PackageId,
    string? RestoredVersion,
    int RestoredFiles);

public sealed class UpdateCoordinator
{
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<IMirrorResolver>? _resolvers;

    public UpdateCoordinator(HttpClient httpClient, IEnumerable<IMirrorResolver>? resolvers = null)
    {
        _httpClient = httpClient;
        _resolvers = resolvers?.ToArray();
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string manifestSource,
        string publicKeyPath,
        string channel,
        string stateRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);

        var signedManifest = await LoadManifestAsync(manifestSource, cancellationToken);
        ManifestValidator.ValidateAndThrow(signedManifest);
        if (!string.Equals(signedManifest.Payload.Channel, channel, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Manifest channel '{signedManifest.Payload.Channel}' does not match configured channel '{channel}'.");
        }

        var publicKeyFullPath = Path.GetFullPath(publicKeyPath);
        if (!File.Exists(publicKeyFullPath))
        {
            throw new FileNotFoundException("Trusted manifest public key was not found.", publicKeyFullPath);
        }

        using var publicKey = ECDsa.Create();
        publicKey.ImportFromPem(await File.ReadAllTextAsync(publicKeyFullPath, cancellationToken));
        if (!ManifestSecurity.Verify(signedManifest, publicKey))
        {
            throw new CryptographicException("Manifest signature is invalid for the selected trusted key.");
        }

        var installed = await ReadInstalledStateAsync(stateRoot, cancellationToken);
        var packages = signedManifest.Payload.Packages
            .Select(package =>
            {
                installed.Packages.TryGetValue(package.Id, out var currentVersion);
                return new PackageUpdate(
                    package,
                    currentVersion,
                    !string.Equals(currentVersion, package.Version, StringComparison.OrdinalIgnoreCase));
            })
            .ToArray();
        return new UpdateCheckResult(signedManifest, packages, signedManifest.Signature.KeyId);
    }

    public async Task<UpdateApplyResult> ApplyAsync(
        UpdateCheckResult check,
        IReadOnlyDictionary<string, string> installRoots,
        string stateRoot,
        IProgress<UpdateProgress>? progress = null,
        string? preferredMirrorProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(installRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);

        var pending = check.Packages.Where(package => package.UpdateAvailable).ToArray();
        if (pending.Length == 0)
        {
            return new UpdateApplyResult(0, 0, 0);
        }

        var resolvedRoots = pending.ToDictionary(
            update => update.Package.Id,
            update => ResolveInstallRoot(update.Package, installRoots),
            StringComparer.OrdinalIgnoreCase);
        var operationRoot = Path.Combine(
            Path.GetFullPath(stateRoot),
            "work",
            $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(operationRoot);

        var downloader = new ArtifactDownloader(_httpClient, _resolvers);
        var installedState = await ReadInstalledStateAsync(stateRoot, cancellationToken);
        var history = await ReadHistoryAsync(stateRoot, cancellationToken);
        var historyStart = history.Entries.Count;
        var batchId = $"release-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
        var applied = new List<AppliedPackage>();
        var installedPackages = 0;
        var installedFiles = 0;
        var deletedFiles = 0;

        try
        {
            foreach (var update in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var package = update.Package;
                var packageRoot = Path.Combine(operationRoot, package.Id);
                var artifactPath = Path.Combine(packageRoot, "artifact.zip");
                var stagingRoot = Path.Combine(packageRoot, "staging");
                Directory.CreateDirectory(packageRoot);

                progress?.Report(new UpdateProgress(UpdateStage.Downloading, $"Загрузка {package.DisplayName}", package.Id, 0, package.Size));
                var downloadProgress = progress is null
                    ? null
                    : new Progress<DownloadProgress>(value => progress.Report(new UpdateProgress(
                        UpdateStage.Downloading,
                        $"Загрузка {package.DisplayName}",
                        package.Id,
                        value.DownloadedBytes,
                        value.TotalBytes,
                        value.Provider)));
                await downloader.DownloadAsync(
                    package,
                    artifactPath,
                    downloadProgress,
                    preferredMirrorProvider,
                    cancellationToken);

                progress?.Report(new UpdateProgress(UpdateStage.Verifying, $"Проверка {package.DisplayName}", package.Id, package.Size, package.Size));
                progress?.Report(new UpdateProgress(UpdateStage.Extracting, $"Распаковка {package.DisplayName}", package.Id));
                await SafeZipExtractor.ExtractAsync(artifactPath, stagingRoot, package, cancellationToken);

                var previousManagedFiles = await ReadManagedFilesAsync(stateRoot, package.Id, cancellationToken);
                var obsoleteFiles = (package.DeletedFiles ?? []).ToList();
                foreach (var directory in package.DeletedDirectories ?? [])
                {
                    obsoleteFiles.AddRange(EnumerateDirectoryFiles(resolvedRoots[package.Id], directory));
                }
                if (package.UpdateMode == PackageUpdateMode.ManagedExact)
                {
                    obsoleteFiles.AddRange(
                        previousManagedFiles.Except(package.Files, StringComparer.OrdinalIgnoreCase));
                }
                if (package.PruneInstallRoot)
                {
                    obsoleteFiles.AddRange(EnumeratePrunableFiles(
                        resolvedRoots[package.Id],
                        package.PreservedPaths ?? [])
                        .Except(package.Files, StringComparer.OrdinalIgnoreCase));
                }
                progress?.Report(new UpdateProgress(UpdateStage.Installing, $"Установка {package.DisplayName}", package.Id));
                var installResult = await TransactionalFileInstaller.ApplyAsync(
                    stagingRoot,
                    resolvedRoots[package.Id],
                    stateRoot,
                    package.Files,
                    obsoleteFiles.Distinct(StringComparer.OrdinalIgnoreCase),
                    cancellationToken);

                applied.Add(new AppliedPackage(update, installResult, previousManagedFiles));
                foreach (var directory in package.DeletedDirectories ?? [])
                {
                    DeleteEmptyDirectoryTree(resolvedRoots[package.Id], directory);
                }
            }

            foreach (var item in applied)
            {
                var package = item.Update.Package;
                await WriteManagedSnapshotAsync(stateRoot, item.Install.OperationId, item.PreviousManagedFiles, cancellationToken);
                await WriteManagedFilesAsync(stateRoot, package.Id, package.Files, cancellationToken);
                installedState.Packages[package.Id] = package.Version;
                history.Entries.Add(new UpdateHistoryEntry(
                    package.Id,
                    package.DisplayName,
                    item.Update.InstalledVersion,
                    package.Version,
                    package.InstallRoot,
                    item.Install.OperationId,
                    DateTimeOffset.UtcNow,
                    null,
                    batchId,
                    item.Install.DeletedFiles));
                installedPackages++;
                installedFiles += item.Install.InstalledFiles;
                deletedFiles += item.Install.DeletedFiles;
            }

            await WriteInstalledStateAsync(stateRoot, installedState, cancellationToken);
            await WriteHistoryAsync(stateRoot, history, cancellationToken);
        }
        catch (Exception updateError)
        {
            progress?.Report(new UpdateProgress(UpdateStage.RollingBack, "Ошибка обновления — возвращаем всю предыдущую сборку"));
            var rollbackErrors = new List<Exception>();
            foreach (var item in applied.AsEnumerable().Reverse())
            {
                var package = item.Update.Package;
                try
                {
                    await TransactionalFileInstaller.RollbackAsync(
                        resolvedRoots[package.Id],
                        stateRoot,
                        item.Install.OperationId,
                        CancellationToken.None);
                }
                catch (Exception rollbackError) when (rollbackError is IOException
                                                       or InvalidDataException
                                                       or InvalidOperationException
                                                       or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(rollbackError);
                }
                finally
                {
                    await WriteManagedFilesAsync(stateRoot, package.Id, item.PreviousManagedFiles, CancellationToken.None);
                    if (item.Update.InstalledVersion is null)
                    {
                        installedState.Packages.Remove(package.Id);
                    }
                    else
                    {
                        installedState.Packages[package.Id] = item.Update.InstalledVersion;
                    }
                }
            }

            if (history.Entries.Count > historyStart)
            {
                history.Entries.RemoveRange(historyStart, history.Entries.Count - historyStart);
            }

            await WriteInstalledStateAsync(stateRoot, installedState, CancellationToken.None);
            await WriteHistoryAsync(stateRoot, history, CancellationToken.None);
            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException(
                    "Обновление остановлено, но часть резервных копий не удалось восстановить.",
                    [updateError, .. rollbackErrors]);
            }

            throw;
        }

        progress?.Report(new UpdateProgress(UpdateStage.Completed, "Обновление установлено"));
        return new UpdateApplyResult(installedPackages, installedFiles, deletedFiles);
    }

    public static async Task<UpdateRollbackCandidate?> GetLatestRollbackAsync(
        string stateRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        var history = await ReadHistoryAsync(stateRoot, cancellationToken);
        var entry = history.Entries.LastOrDefault(item => item.RolledBackAt is null);
        if (entry is null)
        {
            return null;
        }

        var batchId = entry.BatchId ?? entry.OperationId;
        var batch = history.Entries
            .Where(item => item.RolledBackAt is null
                           && string.Equals(item.BatchId ?? item.OperationId, batchId, StringComparison.Ordinal))
            .ToArray();
        return new UpdateRollbackCandidate(
            entry.PackageId,
            batch.Length > 1 ? $"Anthology {entry.ToVersion}" : entry.DisplayName,
            batch.Select(item => item.FromVersion).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                ? entry.FromVersion
                : "смешанная версия",
            entry.ToVersion,
            string.Join(" + ", batch.Select(item => item.InstallRoot).Distinct(StringComparer.OrdinalIgnoreCase)),
            batchId,
            batch.Max(item => item.InstalledAt),
            batch.Length);
    }

    public static async Task<UpdateRollbackResult> RollbackLatestAsync(
        IReadOnlyDictionary<string, string> installRoots,
        string stateRoot,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        var history = await ReadHistoryAsync(stateRoot, cancellationToken);
        var latestIndex = history.Entries.FindLastIndex(item => item.RolledBackAt is null);
        if (latestIndex < 0)
        {
            throw new InvalidOperationException("Нет обновлений, доступных для отката.");
        }

        var latest = history.Entries[latestIndex];
        var batchId = latest.BatchId ?? latest.OperationId;
        var indexes = history.Entries
            .Select((entry, index) => (entry, index))
            .Where(item => item.entry.RolledBackAt is null
                           && string.Equals(item.entry.BatchId ?? item.entry.OperationId, batchId, StringComparison.Ordinal))
            .ToArray();
        foreach (var item in indexes)
        {
            if (!installRoots.TryGetValue(item.entry.InstallRoot, out var root)
                || string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException($"Корень установки '{item.entry.InstallRoot}' не настроен.");
            }
        }

        progress?.Report(new UpdateProgress(
            UpdateStage.RollingBack,
            $"Откат Anthology {latest.ToVersion}",
            latest.PackageId));
        var installedState = await ReadInstalledStateAsync(stateRoot, cancellationToken);
        var restoredFiles = 0;
        foreach (var item in indexes.Reverse())
        {
            var entry = item.entry;
            var targetRoot = Path.GetFullPath(installRoots[entry.InstallRoot]);
            var rollback = await TransactionalFileInstaller.RollbackAsync(
                targetRoot,
                stateRoot,
                entry.OperationId,
                cancellationToken);
            restoredFiles += rollback.RestoredFiles;
            var previousManagedFiles = await ReadManagedSnapshotAsync(stateRoot, entry.OperationId, cancellationToken);
            await WriteManagedFilesAsync(stateRoot, entry.PackageId, previousManagedFiles, cancellationToken);
            if (entry.FromVersion is null)
            {
                installedState.Packages.Remove(entry.PackageId);
            }
            else
            {
                installedState.Packages[entry.PackageId] = entry.FromVersion;
            }

            history.Entries[item.index] = entry with { RolledBackAt = DateTimeOffset.UtcNow };
        }

        await WriteInstalledStateAsync(stateRoot, installedState, cancellationToken);
        await WriteHistoryAsync(stateRoot, history, cancellationToken);
        progress?.Report(new UpdateProgress(UpdateStage.Completed, "Предыдущая версия восстановлена"));
        return new UpdateRollbackResult(latest.PackageId, latest.FromVersion, restoredFiles);
    }

    private async Task<SignedUpdateManifest> LoadManifestAsync(
        string source,
        CancellationToken cancellationToken)
    {
        Stream stream;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            {
                throw new InvalidDataException("Remote manifest must use HTTPS.");
            }

            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumManifestBytes)
            {
                throw new InvalidDataException("Manifest exceeds the 4 MiB safety limit.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("Manifest exceeds the 4 MiB safety limit.");
            }

            stream = new MemoryStream(bytes, writable: false);
        }
        else
        {
            var path = Path.GetFullPath(source);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Update manifest was not found.", path);
            }

            var file = new FileInfo(path);
            if (file.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("Manifest exceeds the 4 MiB safety limit.");
            }

            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        }

        await using (stream)
        {
            return await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options, cancellationToken)
                ?? throw new InvalidDataException("Manifest is empty or invalid JSON.");
        }
    }

    private static string ResolveInstallRoot(
        PackageManifest package,
        IReadOnlyDictionary<string, string> installRoots)
    {
        if (!installRoots.TryGetValue(package.InstallRoot, out var root) || string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                $"Install root '{package.InstallRoot}' is not configured for package '{package.Id}'.");
        }

        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Install root does not exist: {fullRoot}");
        }

        return fullRoot;
    }

    private static IEnumerable<string> EnumeratePrunableFiles(
        string targetRoot,
        IReadOnlyList<string> preservedPaths)
    {
        var root = Path.GetFullPath(targetRoot);
        var preserved = preservedPaths
            .Select(PathSafety.NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Select(PathSafety.NormalizeRelativePath)
            .Where(path => !preserved.Any(item =>
                string.Equals(path, item, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(item + "/", StringComparison.OrdinalIgnoreCase)));
    }

    private static string[] EnumerateDirectoryFiles(string targetRoot, string relativeDirectory)
    {
        var root = Path.GetFullPath(targetRoot);
        var normalizedDirectory = PathSafety.NormalizeRelativePath(relativeDirectory);
        var directory = PathSafety.ResolveUnderRoot(root, normalizedDirectory);
        if (!Directory.Exists(directory))
        {
            // Keep one safe no-op path so a directory-deletion-only package succeeds
            // even when this player has already removed the addon.
            return [normalizedDirectory];
        }

        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Directory deletion cannot follow a reparse point: {normalizedDirectory}");
        }

        var files = Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Select(PathSafety.NormalizeRelativePath)
            .ToArray();
        return files.Length == 0 ? [normalizedDirectory] : files;
    }

    private static void DeleteEmptyDirectoryTree(string targetRoot, string relativeDirectory)
    {
        var root = Path.GetFullPath(targetRoot);
        var normalizedDirectory = PathSafety.NormalizeRelativePath(relativeDirectory);
        var directory = PathSafety.ResolveUnderRoot(root, normalizedDirectory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Directory deletion cannot follow a reparse point: {normalizedDirectory}");
        }

        var descendants = Directory.EnumerateDirectories(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            .OrderByDescending(path => path.Length)
            .ToArray();
        foreach (var child in descendants)
        {
            if (!Directory.EnumerateFileSystemEntries(child).Any())
            {
                Directory.Delete(child);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    private static async Task<InstalledState> ReadInstalledStateAsync(
        string stateRoot,
        CancellationToken cancellationToken)
    {
        var path = GetInstalledStatePath(stateRoot);
        if (!File.Exists(path))
        {
            return new InstalledState(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous);
        var state = await JsonSerializer.DeserializeAsync<InstalledState>(stream, ManifestJson.Options, cancellationToken)
            ?? new InstalledState([]);
        return new InstalledState(new Dictionary<string, string>(state.Packages, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task WriteInstalledStateAsync(
        string stateRoot,
        InstalledState state,
        CancellationToken cancellationToken)
    {
        var path = GetInstalledStatePath(stateRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, state, ManifestJson.Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporary, path, true);
    }

    private static string GetInstalledStatePath(string stateRoot) =>
        Path.Combine(Path.GetFullPath(stateRoot), "installed-packages.json");

    private static async Task<string[]> ReadManagedFilesAsync(
        string stateRoot,
        string packageId,
        CancellationToken cancellationToken)
    {
        var path = GetManagedFilesPath(stateRoot, packageId);
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous);
        var files = await JsonSerializer.DeserializeAsync<string[]>(stream, ManifestJson.Options, cancellationToken) ?? [];
        return files
            .Select(PathSafety.NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Task WriteManagedFilesAsync(
        string stateRoot,
        string packageId,
        IEnumerable<string> files,
        CancellationToken cancellationToken) =>
        WriteStringArrayAtomicallyAsync(
            GetManagedFilesPath(stateRoot, packageId),
            files,
            cancellationToken);

    private static Task WriteManagedSnapshotAsync(
        string stateRoot,
        string operationId,
        IEnumerable<string> files,
        CancellationToken cancellationToken) =>
        WriteStringArrayAtomicallyAsync(
            Path.Combine(Path.GetFullPath(stateRoot), "managed-snapshots", operationId + ".json"),
            files,
            cancellationToken);

    private static async Task<string[]> ReadManagedSnapshotAsync(
        string stateRoot,
        string operationId,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetFullPath(stateRoot), "managed-snapshots", operationId + ".json");
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<string[]>(stream, ManifestJson.Options, cancellationToken) ?? [];
    }

    private static async Task WriteStringArrayAtomicallyAsync(
        string path,
        IEnumerable<string> files,
        CancellationToken cancellationToken)
    {
        var normalized = files
            .Select(PathSafety.NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, normalized, ManifestJson.Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporary, path, true);
    }

    private static string GetManagedFilesPath(string stateRoot, string packageId) =>
        Path.Combine(Path.GetFullPath(stateRoot), "managed-files", packageId + ".json");

    private static async Task<UpdateHistory> ReadHistoryAsync(
        string stateRoot,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetFullPath(stateRoot), "update-history.json");
        if (!File.Exists(path))
        {
            return new UpdateHistory([]);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<UpdateHistory>(stream, ManifestJson.Options, cancellationToken)
            ?? new UpdateHistory([]);
    }

    private static async Task WriteHistoryAsync(
        string stateRoot,
        UpdateHistory history,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetFullPath(stateRoot), "update-history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, history, ManifestJson.Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporary, path, true);
    }

    private sealed record InstalledState(Dictionary<string, string> Packages);

    private sealed record UpdateHistory(List<UpdateHistoryEntry> Entries);

    private sealed record AppliedPackage(
        PackageUpdate Update,
        InstallResult Install,
        IReadOnlyList<string> PreviousManagedFiles);

    private sealed record UpdateHistoryEntry(
        string PackageId,
        string DisplayName,
        string? FromVersion,
        string ToVersion,
        string InstallRoot,
        string OperationId,
        DateTimeOffset InstalledAt,
        DateTimeOffset? RolledBackAt,
        string? BatchId = null,
        int DeletedFiles = 0);
}
