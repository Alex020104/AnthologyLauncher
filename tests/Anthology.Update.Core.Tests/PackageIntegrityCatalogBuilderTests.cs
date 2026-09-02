using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthology.Contracts;
using Anthology.Releaser.Core;

namespace Anthology.Update.Core.Tests;

public sealed class PackageIntegrityCatalogBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"anthology-integrity-builder-{Guid.NewGuid():N}");

    [Fact]
    public async Task MergeCatalogKeepsCumulativeOriginsAndCanBuildAfterOldLocalArchiveWasRemoved()
    {
        var releasesRoot = Path.Combine(_root, "releases");
        var v1Root = Path.Combine(releasesRoot, "1.0.0");
        var v2Root = Path.Combine(releasesRoot, "2.0.0");
        var v3Root = Path.Combine(releasesRoot, "3.0.0");
        Directory.CreateDirectory(v1Root);
        Directory.CreateDirectory(v2Root);
        Directory.CreateDirectory(v3Root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var v1 = await CreatePackageAsync(
            v1Root,
            "anthology-files-modpack",
            "1.0.0",
            "modpack",
            ("mods/A/file-a.txt", "A1"),
            ("mods/B/file-b.txt", "B1"));
        await WriteManifestAsync(v1Root, "1.0.0", [v1], key);

        var v2 = (await CreatePackageAsync(
            v2Root,
            "anthology-files-modpack",
            "2.0.0",
            "modpack",
            ("mods/C/file-c.txt", "C2"))) with
        {
            DeletedDirectories = ["mods/B"],
        };
        var workspaceV2 = new ReleaserWorkspace { Version = "2.0.0", Channel = "next" };
        var resultV2 = await PackageIntegrityCatalogBuilder.BuildAsync(
            [v2],
            v2Root,
            workspaceV2,
            key,
            "test-key-01");

        Assert.NotNull(resultV2);
        var catalogV2 = await ReadCatalogAsync(resultV2.CatalogPath);
        Assert.True(ManifestSecurity.Verify(catalogV2, key));
        PackageIntegrityCatalogValidator.ValidateAndThrow(catalogV2);
        Assert.Equal(
            ["mods/A/file-a.txt", "mods/C/file-c.txt"],
            catalogV2.Payload.Artifacts.SelectMany(artifact => artifact.ManagedFiles).Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            catalogV2.Payload.Artifacts.SelectMany(artifact => artifact.ManagedFiles),
            path => path.StartsWith("mods/B/", StringComparison.OrdinalIgnoreCase));
        var aOrigin = Assert.Single(catalogV2.Payload.Artifacts, artifact =>
            artifact.ManagedFiles.Contains("mods/A/file-a.txt", StringComparer.OrdinalIgnoreCase));
        var cOrigin = Assert.Single(catalogV2.Payload.Artifacts, artifact =>
            artifact.ManagedFiles.Contains("mods/C/file-c.txt", StringComparer.OrdinalIgnoreCase));
        Assert.Equal("1.0.0", aOrigin.PackageVersion);
        Assert.Equal("2.0.0", aOrigin.RequiredPackageVersion);
        Assert.Equal("2.0.0", cOrigin.PackageVersion);
        Assert.Equal("2.0.0", cOrigin.RequiredPackageVersion);
        Assert.Equal(PackageKind.Launcher, resultV2.Package.Kind);
        Assert.Equal("game", resultV2.Package.InstallRoot);
        Assert.Equal([PackageIntegrityCatalogBuilder.CatalogRelativePath], resultV2.Package.Files);

        // The next publication must use the signed catalog as its cumulative
        // baseline. Keeping every old 20+ GiB archive on the releaser machine is
        // neither required nor safe as a release invariant.
        Directory.Delete(v1Root, true);
        var v3 = await CreatePackageAsync(
            v3Root,
            "anthology-files-modpack",
            "3.0.0",
            "modpack",
            ("mods/D/file-d.txt", "D3"));
        var resultV3 = await PackageIntegrityCatalogBuilder.BuildAsync(
            [v3],
            v3Root,
            new ReleaserWorkspace { Version = "3.0.0", Channel = "next" },
            key,
            "test-key-01");

        Assert.NotNull(resultV3);
        var catalogV3 = await ReadCatalogAsync(resultV3.CatalogPath);
        Assert.Equal(
            ["mods/A/file-a.txt", "mods/C/file-c.txt", "mods/D/file-d.txt"],
            catalogV3.Payload.Artifacts.SelectMany(artifact => artifact.ManagedFiles).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(catalogV3.Payload.Artifacts, artifact => Assert.Equal("3.0.0", artifact.RequiredPackageVersion));
        Assert.Contains(catalogV3.Payload.Artifacts, artifact =>
            artifact.PackageVersion == "1.0.0"
            && artifact.ManagedFiles.Contains("mods/A/file-a.txt", StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CanonicalMo2MapperDropsLegacyRootAndUserOwnedRepairTargets()
    {
        var releasesRoot = Path.Combine(_root, "relocated-releases");
        var legacyRoot = Path.Combine(releasesRoot, "2.1.157");
        var currentRoot = Path.Combine(releasesRoot, "2.1.160");
        Directory.CreateDirectory(legacyRoot);
        Directory.CreateDirectory(currentRoot);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var legacy = await CreatePackageAsync(
            legacyRoot,
            "anthology-files-modpack",
            "2.1.157",
            "modpack",
            ("Legacy Addon/gamedata/configs/legacy.ltx", "legacy"),
            ("profiles/Player/saves/player.scop", "save"),
            ("downloads/private-addon.zip", "download"),
            ("overwrite/gamedata/configs/generated.ltx", "overwrite"),
            ("ModOrganizer.ini", "settings"));
        var safeLegacyOrigin = await CreatePackageAsync(
            legacyRoot,
            "legacy-safe-origin",
            "2.1.157",
            "modpack",
            ("mods/Retained Addon/gamedata/configs/retained.ltx", "retained"));
        var safeLegacy = safeLegacyOrigin with
        {
            Id = "anthology-files-modpack",
            DisplayName = "anthology-files-modpack",
        };
        await WriteManifestAsync(legacyRoot, "2.1.157", [legacy], key);

        // Reproduce the signed legacy baseline that existed before the mods/**
        // boundary was introduced, then prove a later correctly mapped
        // publication migrates it without trusting its unsafe archive.
        await WriteLegacyIntegrityCatalogAsync(legacyRoot, key, legacy, safeLegacy);

        var current = await CreatePackageAsync(
            currentRoot,
            "anthology-files-modpack",
            "2.1.160",
            "modpack",
            ("mods/Legacy Addon/gamedata/configs/legacy.ltx", "current"));
        var result = await PackageIntegrityCatalogBuilder.BuildAsync(
            [current],
            currentRoot,
            new ReleaserWorkspace { Version = "2.1.160", Channel = "next" },
            key,
            "test-key-01");

        Assert.NotNull(result);
        var catalog = await ReadCatalogAsync(result.CatalogPath);
        PackageIntegrityCatalogValidator.ValidateAndThrow(catalog);
        var repairTargets = catalog.Payload.Artifacts
            .SelectMany(artifact => artifact.ManagedFiles)
            .ToArray();
        Assert.Equal(
            [
                "mods/Legacy Addon/gamedata/configs/legacy.ltx",
                "mods/Retained Addon/gamedata/configs/retained.ltx",
            ],
            repairTargets.Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(catalog.Payload.Artifacts, artifact =>
            artifact.ArchiveSha256.Equals(legacy.Sha256, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(catalog.Payload.Artifacts, artifact =>
            artifact.PackageVersion == "2.1.157"
            && artifact.ManagedFiles.Contains(
                "mods/Retained Addon/gamedata/configs/retained.ltx",
                StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(repairTargets, path =>
            !path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(repairTargets, path =>
            path.StartsWith("profiles/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("downloads/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("overwrite/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("ModOrganizer.ini", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GameOnlyQuickReleasePreservesModpackPackageAndItsIntegrityOwnership()
    {
        var outputRoot = Path.Combine(_root, "quick-output");
        var versionRoot = Path.Combine(outputRoot, "2.1.200");
        var keysRoot = Path.Combine(_root, "quick-keys");
        Directory.CreateDirectory(versionRoot);
        Directory.CreateDirectory(keysRoot);
        var privateKeyPath = Path.Combine(keysRoot, "private.pem");
        var publicKeyPath = Path.Combine(keysRoot, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKeyPath, publicKeyPath);
        using var key = ECDsa.Create();
        key.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
        var modpack = await CreatePackageAsync(
            versionRoot,
            "anthology-files-modpack",
            "2.1.200",
            "modpack",
            ("mods/Existing Addon/gamedata/existing.script", "return true"));
        await WriteManifestAsync(versionRoot, "2.1.200", [modpack], key);
        var quickFile = Path.Combine(_root, "quick-game-file.ltx");
        await File.WriteAllTextAsync(quickFile, "enabled = true");
        var workspace = new ReleaserWorkspace { Version = "2.1.200", Channel = "next" };
        var machine = new ReleaserMachineSettings
        {
            OutputRoot = outputRoot,
            PrivateKeyPath = privateKeyPath,
            PublicKeyPath = publicKeyPath,
            KeyId = "test-key-01",
            QuickReleaseFiles =
            [
                new QuickReleaseFileDraft
                {
                    SourcePath = quickFile,
                    InstallRoot = "game",
                    RelativePath = "gamedata/configs/quick-game-file.ltx",
                },
            ],
        };

        var result = await ReleasePublicationService.PublishQuickFilesAsync(workspace, machine);

        await using var manifestStream = File.OpenRead(result.ManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(manifestStream, ManifestJson.Options);
        Assert.NotNull(manifest);
        Assert.Contains(manifest.Payload.Packages, package => package.Id == "anthology-files-modpack");
        Assert.Contains(manifest.Payload.Packages, package => package.Id == "anthology-files-game");
        Assert.Contains(manifest.Payload.Packages, package => package.Id == PackageIntegrityCatalogBuilder.PackageId);
        var catalog = await ReadCatalogAsync(Path.Combine(versionRoot, "package-integrity.json"));
        var modpackOwner = Assert.Single(catalog.Payload.Artifacts, artifact =>
            artifact.PackageId == "anthology-files-modpack"
            && artifact.ManagedFiles.Contains(
                "mods/Existing Addon/gamedata/existing.script",
                StringComparer.OrdinalIgnoreCase));
        Assert.Equal("2.1.200", modpackOwner.RequiredPackageVersion);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static async Task<PackageManifest> CreatePackageAsync(
        string releaseRoot,
        string id,
        string version,
        string installRoot,
        params (string Path, string Content)[] files)
    {
        var archivePath = Path.Combine(releaseRoot, $"{id}-{version}.zip");
        await using (var output = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.SmallestSize);
                await using var stream = entry.Open();
                await stream.WriteAsync(Encoding.UTF8.GetBytes(file.Content));
            }
        }
        var bytes = await File.ReadAllBytesAsync(archivePath);
        return new PackageManifest(
            id,
            id,
            version,
            PackageKind.Mod,
            installRoot,
            "zip",
            bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            [new MirrorManifest("local-file", new Uri(archivePath).AbsoluteUri, 1)],
            files.Select(file => file.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            PackageUpdateMode.Merge);
    }

    private static async Task WriteManifestAsync(
        string releaseRoot,
        string version,
        IReadOnlyList<PackageManifest> packages,
        ECDsa key)
    {
        var manifest = ManifestSecurity.Sign(
            new UpdateManifest(4, "next", version, DateTimeOffset.UtcNow, null, packages),
            key,
            "test-key-01");
        await File.WriteAllTextAsync(
            Path.Combine(releaseRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest, ManifestJson.Options));
    }

    private static async Task WriteLegacyIntegrityCatalogAsync(
        string releaseRoot,
        ECDsa key,
        params PackageManifest[] packages)
    {
        var artifacts = new List<PackageArtifactIntegrity>();
        for (var index = 0; index < packages.Length; index++)
        {
            var package = packages[index];
            var archivePath = new Uri(package.Mirrors[0].Url).LocalPath;
            var archiveFiles = new List<PackageFileIntegrity>();
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
                {
                    await using var stream = entry.Open();
                    archiveFiles.Add(new PackageFileIntegrity(
                        PathSafety.NormalizeRelativePath(entry.FullName),
                        entry.Length,
                        await ArtifactHash.ComputeSha256Async(stream)));
                }
            }

            var managedFiles = archiveFiles
                .Select(file => file.Path)
                .Where(path => !path.StartsWith("profiles/", StringComparison.OrdinalIgnoreCase)
                               && !path.StartsWith("downloads/", StringComparison.OrdinalIgnoreCase)
                               && !path.StartsWith("overwrite/", StringComparison.OrdinalIgnoreCase)
                               && !path.Equals("ModOrganizer.ini", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            artifacts.Add(new PackageArtifactIntegrity(
                $"artifact-legacy-selected-modpack-{index}",
                package.Id,
                package.Version,
                package.Version,
                package.Kind,
                package.InstallRoot,
                package.ArchiveFormat,
                package.Size,
                package.Sha256,
                package.Mirrors,
                archiveFiles,
                managedFiles));
        }

        var signed = ManifestSecurity.Sign(
            new PackageIntegrityCatalog(
                1,
                "next",
                packages[0].Version,
                DateTimeOffset.UtcNow,
                artifacts),
            key,
            "test-key-01");
        await File.WriteAllTextAsync(
            Path.Combine(releaseRoot, "package-integrity.json"),
            JsonSerializer.Serialize(signed, ManifestJson.Options));
    }

    private static async Task<SignedPackageIntegrityCatalog> ReadCatalogAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SignedPackageIntegrityCatalog>(stream, ManifestJson.Options)
               ?? throw new InvalidDataException("Integrity catalog was not written.");
    }
}
