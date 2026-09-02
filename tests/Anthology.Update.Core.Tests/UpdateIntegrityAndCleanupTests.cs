using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthology.Contracts;

namespace Anthology.Update.Core.Tests;

public sealed class UpdateIntegrityAndCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"anthology-integrity-flow-{Guid.NewGuid():N}");

    [Fact]
    public async Task SameVersionDeletedAndModifiedFilesAreAutomaticallyRepairedFromSignedOrigins()
    {
        var fixture = await CreateInstalledIntegrityFixtureAsync(
            ("gamedata/configs/restore-missing.ltx", "missing-original"),
            ("mods/example/restore-modified.txt", "correct-value"));
        var missing = Path.Combine(fixture.GameRoot, "gamedata", "configs", "restore-missing.ltx");
        var modified = Path.Combine(fixture.GameRoot, "mods", "example", "restore-modified.txt");

        File.Delete(missing);
        await File.WriteAllTextAsync(modified, "broken-value!"); // Same byte count as the original.

        var check = await fixture.Coordinator.CheckAsync(
            fixture.ManifestPath,
            fixture.PublicKeyPath,
            "next",
            fixture.StateRoot,
            fixture.InstallRoots);
        var repair = Assert.Single(check.Packages, package => package.RepairRequired);
        Assert.StartsWith("repair-", repair.Package.Id, StringComparison.Ordinal);
        Assert.False(repair.TrackInstallation);
        Assert.Equal(
            ["gamedata/configs/restore-missing.ltx", "mods/example/restore-modified.txt"],
            repair.RepairFiles!.Order(StringComparer.OrdinalIgnoreCase));

        await fixture.Coordinator.ApplyAsync(check, fixture.InstallRoots, fixture.StateRoot);

        Assert.Equal("missing-original", await File.ReadAllTextAsync(missing));
        Assert.Equal("correct-value", await File.ReadAllTextAsync(modified));
        var installedJson = await File.ReadAllTextAsync(Path.Combine(fixture.StateRoot, "installed-packages.json"));
        Assert.DoesNotContain("repair-", installedJson, StringComparison.OrdinalIgnoreCase);
        var finalCheck = await fixture.Coordinator.CheckAsync(
            fixture.ManifestPath,
            fixture.PublicKeyPath,
            "next",
            fixture.StateRoot,
            fixture.InstallRoots);
        Assert.False(finalCheck.HasUpdates);
    }

    [Fact]
    public async Task MissingOrCorruptIntegrityCatalogRepairsOnlyTheSmallCatalogPackage()
    {
        var fixture = await CreateInstalledIntegrityFixtureAsync(("gamedata/configs/example.ltx", "value"));
        var catalogPath = Path.Combine(
            fixture.GameRoot,
            "AnthologyLauncher",
            "Update",
            "Integrity",
            "package-integrity.json");

        File.Delete(catalogPath);
        var missingCheck = await fixture.Coordinator.CheckAsync(
            fixture.ManifestPath,
            fixture.PublicKeyPath,
            "next",
            fixture.StateRoot,
            fixture.InstallRoots);
        var missingRepair = Assert.Single(missingCheck.Packages, package => package.RepairRequired);
        Assert.Equal("anthology-integrity", missingRepair.Package.Id);
        Assert.Equal([PackageIntegrityCatalogPath], missingRepair.RepairFiles);
        await fixture.Coordinator.ApplyAsync(missingCheck, fixture.InstallRoots, fixture.StateRoot);
        Assert.True(File.Exists(catalogPath));

        await File.WriteAllTextAsync(catalogPath, "{ definitely-not-valid-json");
        var corruptCheck = await fixture.Coordinator.CheckAsync(
            fixture.ManifestPath,
            fixture.PublicKeyPath,
            "next",
            fixture.StateRoot,
            fixture.InstallRoots);
        var corruptRepair = Assert.Single(corruptCheck.Packages, package => package.RepairRequired);
        Assert.Equal("anthology-integrity", corruptRepair.Package.Id);
        await fixture.Coordinator.ApplyAsync(corruptCheck, fixture.InstallRoots, fixture.StateRoot);

        await using var restored = File.OpenRead(catalogPath);
        Assert.NotNull(await JsonSerializer.DeserializeAsync<SignedPackageIntegrityCatalog>(restored, ManifestJson.Options));
    }

    [Fact]
    public async Task CumulativePackageCanRepairFilesFromSeveralHistoricalOriginArchives()
    {
        Directory.CreateDirectory(_root);
        var artifactRoot = Path.Combine(_root, "origin-artifacts");
        var gameRoot = Path.Combine(_root, "origin-game");
        var stateRoot = Path.Combine(_root, "origin-state");
        Directory.CreateDirectory(artifactRoot);
        Directory.CreateDirectory(gameRoot);
        Directory.CreateDirectory(stateRoot);
        var originV1 = await CreateArchiveAsync(
            Path.Combine(artifactRoot, "origin-v1.zip"),
            ("gamedata/from-v1.txt", "origin-one"));
        var originV2 = await CreateArchiveAsync(
            Path.Combine(artifactRoot, "origin-v2.zip"),
            ("gamedata/from-v2.txt", "origin-two"));
        var packageV1 = CreatePackage("anthology-files-game", "1.0.0", PackageKind.Game, "game", originV1);
        var packageV2 = CreatePackage("anthology-files-game", "2.0.0", PackageKind.Game, "game", originV2);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalog = ManifestSecurity.Sign(
            new PackageIntegrityCatalog(
                1,
                "next",
                "2.0.0",
                DateTimeOffset.UtcNow,
                [
                    CreateArtifact("artifact-origin-v1", packageV1, originV1, "2.0.0"),
                    CreateArtifact("artifact-origin-v2", packageV2, originV2, "2.0.0"),
                ]),
            key,
            "test-key-01");
        var installedCatalogPath = Path.Combine(gameRoot, PackageIntegrityCatalogPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(installedCatalogPath)!);
        await File.WriteAllTextAsync(installedCatalogPath, JsonSerializer.Serialize(catalog, ManifestJson.Options));
        var catalogArchive = await CreateArchiveFromFilesAsync(
            Path.Combine(artifactRoot, "catalog.zip"),
            (PackageIntegrityCatalogPath, installedCatalogPath));
        var catalogPackage = CreatePackage(
            "anthology-integrity",
            "catalog-v2",
            PackageKind.Launcher,
            "game",
            catalogArchive);
        var publicKeyPath = Path.Combine(_root, "origin-public.pem");
        var manifestPath = await WriteManifestAndKeyAsync(
            key,
            "2.0.0",
            [packageV2, catalogPackage],
            Path.Combine(_root, "origin-manifest.json"),
            publicKeyPath);
        await File.WriteAllTextAsync(
            Path.Combine(stateRoot, "installed-packages.json"),
            "{\"packages\":{\"anthology-files-game\":\"2.0.0\",\"anthology-integrity\":\"catalog-v2\"}}");
        var firstPath = Path.Combine(gameRoot, "gamedata", "from-v1.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
        await File.WriteAllTextAsync(firstPath, "corrupted!");
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = gameRoot };
        var coordinator = new UpdateCoordinator(new HttpClient());

        var check = await coordinator.CheckAsync(manifestPath, publicKeyPath, "next", stateRoot, roots);
        var repairs = check.Packages.Where(update => update.RepairRequired).ToArray();
        Assert.Equal(2, repairs.Length);
        Assert.Equal(2, repairs.Select(update => update.Package.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(repairs, repair => Assert.False(repair.TrackInstallation));

        await coordinator.ApplyAsync(check, roots, stateRoot);

        Assert.Equal("origin-one", await File.ReadAllTextAsync(firstPath));
        Assert.Equal("origin-two", await File.ReadAllTextAsync(Path.Combine(gameRoot, "gamedata", "from-v2.txt")));
    }

    [Fact]
    public async Task LegacySameVersionWithoutVerifiedBaselineDoesNotTrustOrRedownloadPlayerFiles()
    {
        Directory.CreateDirectory(_root);
        var gameRoot = Path.Combine(_root, "legacy-game");
        var stateRoot = Path.Combine(_root, "legacy-state");
        Directory.CreateDirectory(gameRoot);
        Directory.CreateDirectory(stateRoot);
        var archivePath = Path.Combine(_root, "legacy.zip");
        var archive = await CreateArchiveAsync(archivePath, ("gamedata/missing.ltx", "official"));
        var package = CreatePackage("anthology-core", "1.0.0", PackageKind.Game, "game", archive);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifestPath = await WriteManifestAndKeyAsync(
            key,
            "1.0.0",
            [package],
            Path.Combine(_root, "legacy-manifest.json"),
            Path.Combine(_root, "legacy-public.pem"));
        await File.WriteAllTextAsync(
            Path.Combine(stateRoot, "installed-packages.json"),
            "{\"packages\":{\"anthology-core\":\"1.0.0\"}}");

        var check = await new UpdateCoordinator(new HttpClient()).CheckAsync(
            manifestPath,
            Path.Combine(_root, "legacy-public.pem"),
            "next",
            stateRoot,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = gameRoot });

        Assert.False(check.HasUpdates);
        Assert.DoesNotContain(check.Packages, update => update.RepairRequired);
    }

    [Fact]
    public async Task SuccessfulAndFailedOperationsCleanWorkButNeverTouchGameUserData()
    {
        var fixture = await CreateInstalledIntegrityFixtureAsync(("gamedata/configs/example.ltx", "value"));
        var userFile = Path.Combine(fixture.GameRoot, "appdata", "user.ltx");
        Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
        await File.WriteAllTextAsync(userFile, "player-owned");

        var staleWork = Path.Combine(fixture.StateRoot, "work", "abandoned");
        var freshWork = Path.Combine(fixture.StateRoot, "work", "possibly-active");
        Directory.CreateDirectory(staleWork);
        Directory.CreateDirectory(freshWork);
        await File.WriteAllTextAsync(Path.Combine(staleWork, "artifact.zip.partial"), "junk");
        Directory.SetLastWriteTimeUtc(staleWork, DateTime.UtcNow - TimeSpan.FromHours(2));
        var referencedRollbackArchives = Directory.GetFiles(
            Path.Combine(fixture.StateRoot, "rollback-archives"),
            "*.zip");
        Assert.NotEmpty(referencedRollbackArchives);
        var orphanTransaction = Path.Combine(fixture.StateRoot, "transactions", "orphan-operation");
        Directory.CreateDirectory(orphanTransaction);
        await File.WriteAllTextAsync(Path.Combine(orphanTransaction, "journal.json"), "junk");
        Directory.SetLastWriteTimeUtc(orphanTransaction, DateTime.UtcNow - TimeSpan.FromHours(2));
        var orphanArchive = Path.Combine(fixture.StateRoot, "rollback-archives", "orphan-operation.zip");
        await File.WriteAllTextAsync(orphanArchive, "junk");
        File.SetLastWriteTimeUtc(orphanArchive, DateTime.UtcNow - TimeSpan.FromHours(2));
        var staleAtomicFile = Path.Combine(fixture.StateRoot, "update-history.json.tmp-abandoned");
        await File.WriteAllTextAsync(staleAtomicFile, "junk");
        File.SetLastWriteTimeUtc(staleAtomicFile, DateTime.UtcNow - TimeSpan.FromHours(2));

        await fixture.Coordinator.CheckAsync(
            fixture.ManifestPath,
            fixture.PublicKeyPath,
            "next",
            fixture.StateRoot,
            fixture.InstallRoots);

        Assert.False(Directory.Exists(staleWork));
        Assert.True(Directory.Exists(freshWork));
        Assert.False(Directory.Exists(orphanTransaction));
        Assert.False(File.Exists(orphanArchive));
        Assert.False(File.Exists(staleAtomicFile));
        Assert.All(referencedRollbackArchives, path => Assert.True(File.Exists(path)));
        Assert.Equal("player-owned", await File.ReadAllTextAsync(userFile));
        Directory.Delete(freshWork, true);

        var badArchive = await CreateArchiveAsync(
            Path.Combine(_root, "bad-update.zip"),
            ("gamedata/configs/example.ltx", "new-value"));
        var badPackage = CreatePackage("anthology-core", "2.0.0", PackageKind.Game, "game", badArchive) with
        {
            Size = badArchive.Bytes.Length + 1,
        };
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var badManifest = await WriteManifestAndKeyAsync(
            key,
            "2.0.0",
            [badPackage],
            Path.Combine(_root, "bad-manifest.json"),
            Path.Combine(_root, "bad-public.pem"));
        var badCheck = await new UpdateCoordinator(new HttpClient()).CheckAsync(
            badManifest,
            Path.Combine(_root, "bad-public.pem"),
            "next",
            fixture.StateRoot);

        await Assert.ThrowsAsync<AggregateException>(() =>
            new UpdateCoordinator(new HttpClient()).ApplyAsync(
                badCheck,
                fixture.InstallRoots,
                fixture.StateRoot));

        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(fixture.StateRoot, "work")));
        Assert.Equal("player-owned", await File.ReadAllTextAsync(userFile));
        Assert.Equal("value", await File.ReadAllTextAsync(Path.Combine(fixture.GameRoot, "gamedata", "configs", "example.ltx")));
    }

    [Fact]
    public async Task ExactMo2UpdatePreservesPlayerProfilesSavesAndDownloadsWhileUpdatingShippedProfile()
    {
        Directory.CreateDirectory(_root);
        var modpackRoot = Path.Combine(_root, "preserved-mo2");
        var stateRoot = Path.Combine(_root, "preserved-state");
        var playerSave = Path.Combine(modpackRoot, "profiles", "Player Profile", "saves", "player.scop");
        var playerProfile = Path.Combine(modpackRoot, "profiles", "Player Profile", "custom.ini");
        var playerDownload = Path.Combine(modpackRoot, "downloads", "personal-addon.zip");
        var playerOverwrite = Path.Combine(modpackRoot, "overwrite", "player-generated.ltx");
        var organizerSettings = Path.Combine(modpackRoot, "ModOrganizer.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(playerSave)!);
        Directory.CreateDirectory(Path.GetDirectoryName(playerDownload)!);
        Directory.CreateDirectory(Path.GetDirectoryName(playerOverwrite)!);
        await File.WriteAllTextAsync(playerSave, "save-data");
        await File.WriteAllTextAsync(playerProfile, "profile-data");
        await File.WriteAllTextAsync(playerDownload, "download-data");
        await File.WriteAllTextAsync(playerOverwrite, "overwrite-data");
        await File.WriteAllTextAsync(organizerSettings, "organizer-data");
        await File.WriteAllTextAsync(Path.Combine(modpackRoot, "obsolete-root-file.txt"), "remove-me");
        var archive = await CreateArchiveAsync(
            Path.Combine(_root, "preserved-mo2.zip"),
            ("profiles/Anthology/modlist.txt", "+Current Addon"),
            ("mods/Current Addon/content.txt", "managed"));
        var package = CreatePackage("anthology-mo2", "2.1.200", PackageKind.Modpack, "modpack", archive) with
        {
            UpdateMode = PackageUpdateMode.ManagedExact,
            PruneInstallRoot = true,
            PreservedPaths = ["profiles", "downloads", "overwrite", "ModOrganizer.ini"],
        };
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPath = Path.Combine(_root, "preserved-public.pem");
        var manifestPath = await WriteManifestAndKeyAsync(
            key,
            "2.1.200",
            [package],
            Path.Combine(_root, "preserved-manifest.json"),
            publicKeyPath);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["modpack"] = modpackRoot };
        var coordinator = new UpdateCoordinator(new HttpClient());

        await coordinator.ApplyAsync(
            await coordinator.CheckAsync(manifestPath, publicKeyPath, "next", stateRoot),
            roots,
            stateRoot);

        Assert.False(File.Exists(Path.Combine(modpackRoot, "obsolete-root-file.txt")));
        Assert.Equal("+Current Addon", await File.ReadAllTextAsync(Path.Combine(modpackRoot, "profiles", "Anthology", "modlist.txt")));
        Assert.Equal("save-data", await File.ReadAllTextAsync(playerSave));
        Assert.Equal("profile-data", await File.ReadAllTextAsync(playerProfile));
        Assert.Equal("download-data", await File.ReadAllTextAsync(playerDownload));
        Assert.Equal("overwrite-data", await File.ReadAllTextAsync(playerOverwrite));
        Assert.Equal("organizer-data", await File.ReadAllTextAsync(organizerSettings));

        await UpdateCoordinator.RollbackLatestAsync(roots, stateRoot);
        Assert.Equal("save-data", await File.ReadAllTextAsync(playerSave));
        Assert.Equal("profile-data", await File.ReadAllTextAsync(playerProfile));
        Assert.Equal("download-data", await File.ReadAllTextAsync(playerDownload));
        Assert.Equal("overwrite-data", await File.ReadAllTextAsync(playerOverwrite));
        Assert.Equal("organizer-data", await File.ReadAllTextAsync(organizerSettings));
    }

    [Fact]
    public async Task StartupCompactsReferencedRawTransactionOnlyAfterItsRollbackZipValidates()
    {
        var fixture = await CreateInstalledIntegrityFixtureAsync(("gamedata/configs/example.ltx", "value"));
        var archivePath = Directory.GetFiles(Path.Combine(fixture.StateRoot, "rollback-archives"), "*.zip")[0];
        var operationId = Path.GetFileNameWithoutExtension(archivePath);
        var transactionRoot = Path.Combine(fixture.StateRoot, "transactions", operationId);
        Directory.CreateDirectory(Path.GetDirectoryName(transactionRoot)!);
        ZipFile.ExtractToDirectory(archivePath, transactionRoot);

        await fixture.Coordinator.CheckAsync(
            fixture.ManifestPath,
            fixture.PublicKeyPath,
            "next",
            fixture.StateRoot,
            fixture.InstallRoots);

        Assert.False(Directory.Exists(transactionRoot));
        Assert.True(File.Exists(archivePath));

        ZipFile.ExtractToDirectory(archivePath, transactionRoot);
        await File.WriteAllTextAsync(archivePath, "corrupt rollback archive");
        await fixture.Coordinator.CheckAsync(
            fixture.ManifestPath,
            fixture.PublicKeyPath,
            "next",
            fixture.StateRoot,
            fixture.InstallRoots);

        Assert.True(Directory.Exists(transactionRoot));
    }

    [Fact]
    public async Task TwoUpdatesKeepOnlyCompressedLatestRollbackAndRollbackRestoresAddedDeletedAndReplacedFiles()
    {
        Directory.CreateDirectory(_root);
        var gameRoot = Path.Combine(_root, "rollback-game");
        var stateRoot = Path.Combine(_root, "rollback-state");
        Directory.CreateDirectory(gameRoot);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = gameRoot };
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPath = Path.Combine(_root, "rollback-public.pem");
        await File.WriteAllTextAsync(publicKeyPath, key.ExportSubjectPublicKeyInfoPem());

        var v1Archive = await CreateArchiveAsync(
            Path.Combine(_root, "rollback-v1.zip"),
            ("kept.txt", "v1"),
            ("deleted-in-v2.txt", "bring-me-back"));
        var v1Package = CreatePackage("anthology-core", "1.0.0", PackageKind.Game, "game", v1Archive);
        var manifestPath = await WriteManifestAsync(key, "1.0.0", [v1Package], Path.Combine(_root, "rollback-manifest.json"));
        var coordinator = new UpdateCoordinator(new HttpClient());
        await coordinator.ApplyAsync(
            await coordinator.CheckAsync(manifestPath, publicKeyPath, "next", stateRoot),
            roots,
            stateRoot);
        var firstArchive = Assert.Single(Directory.GetFiles(Path.Combine(stateRoot, "rollback-archives"), "*.zip"));
        Assert.False(Directory.Exists(Path.Combine(stateRoot, "transactions"))
                     && Directory.EnumerateDirectories(Path.Combine(stateRoot, "transactions")).Any());

        var v2Archive = await CreateArchiveAsync(
            Path.Combine(_root, "rollback-v2.zip"),
            ("kept.txt", "v2"),
            ("new-in-v2.txt", "remove-on-rollback"));
        var v2Package = CreatePackage("anthology-core", "2.0.0", PackageKind.Game, "game", v2Archive) with
        {
            DeletedFiles = ["deleted-in-v2.txt"],
        };
        await WriteManifestAsync(key, "2.0.0", [v2Package], manifestPath);
        await coordinator.ApplyAsync(
            await coordinator.CheckAsync(manifestPath, publicKeyPath, "next", stateRoot),
            roots,
            stateRoot);

        var secondArchive = Assert.Single(Directory.GetFiles(Path.Combine(stateRoot, "rollback-archives"), "*.zip"));
        Assert.NotEqual(firstArchive, secondArchive);
        Assert.False(File.Exists(firstArchive));
        Assert.False(Directory.Exists(Path.Combine(stateRoot, "transactions"))
                     && Directory.EnumerateDirectories(Path.Combine(stateRoot, "transactions")).Any());

        await UpdateCoordinator.RollbackLatestAsync(roots, stateRoot);

        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(gameRoot, "kept.txt")));
        Assert.Equal("bring-me-back", await File.ReadAllTextAsync(Path.Combine(gameRoot, "deleted-in-v2.txt")));
        Assert.False(File.Exists(Path.Combine(gameRoot, "new-in-v2.txt")));
        Assert.Null(await UpdateCoordinator.GetLatestRollbackAsync(stateRoot));
        Assert.Empty(Directory.GetFiles(Path.Combine(stateRoot, "rollback-archives"), "*.zip"));
    }

    [Fact]
    public async Task FailedNextUpdateKeepsPriorCompressedRollback()
    {
        Directory.CreateDirectory(_root);
        var gameRoot = Path.Combine(_root, "failed-game");
        var stateRoot = Path.Combine(_root, "failed-state");
        Directory.CreateDirectory(gameRoot);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = gameRoot };
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPath = Path.Combine(_root, "failed-public.pem");
        await File.WriteAllTextAsync(publicKeyPath, key.ExportSubjectPublicKeyInfoPem());
        var v1 = await CreateArchiveAsync(Path.Combine(_root, "failed-v1.zip"), ("file.txt", "v1"));
        var manifestPath = await WriteManifestAsync(
            key,
            "1.0.0",
            [CreatePackage("anthology-core", "1.0.0", PackageKind.Game, "game", v1)],
            Path.Combine(_root, "failed-manifest.json"));
        var coordinator = new UpdateCoordinator(new HttpClient());
        await coordinator.ApplyAsync(
            await coordinator.CheckAsync(manifestPath, publicKeyPath, "next", stateRoot),
            roots,
            stateRoot);
        var rollbackArchive = Assert.Single(Directory.GetFiles(Path.Combine(stateRoot, "rollback-archives"), "*.zip"));
        var candidate = await UpdateCoordinator.GetLatestRollbackAsync(stateRoot);

        var v2 = await CreateArchiveAsync(Path.Combine(_root, "failed-v2.zip"), ("file.txt", "v2"));
        var missingMirrorPackage = CreatePackage("anthology-core", "2.0.0", PackageKind.Game, "game", v2) with
        {
            Mirrors = [new MirrorManifest("local-file", new Uri(Path.Combine(_root, "does-not-exist.zip")).AbsoluteUri, 1)],
        };
        await WriteManifestAsync(key, "2.0.0", [missingMirrorPackage], manifestPath);
        var failedCheck = await coordinator.CheckAsync(manifestPath, publicKeyPath, "next", stateRoot);

        await Assert.ThrowsAsync<AggregateException>(() =>
            coordinator.ApplyAsync(failedCheck, roots, stateRoot));

        Assert.True(File.Exists(rollbackArchive));
        Assert.Equal(candidate, await UpdateCoordinator.GetLatestRollbackAsync(stateRoot));
        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(gameRoot, "file.txt")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private async Task<InstalledFixture> CreateInstalledIntegrityFixtureAsync(
        params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(_root);
        var artifacts = Path.Combine(_root, "artifacts");
        var gameRoot = Path.Combine(_root, "game");
        var stateRoot = Path.Combine(_root, "state");
        Directory.CreateDirectory(artifacts);
        Directory.CreateDirectory(gameRoot);
        var gameArchive = await CreateArchiveAsync(Path.Combine(artifacts, "game.zip"), files);
        var gamePackage = CreatePackage("anthology-core", "1.0.0", PackageKind.Game, "game", gameArchive);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var integrityPayload = new PackageIntegrityCatalog(
            1,
            "next",
            "1.0.0",
            DateTimeOffset.UtcNow,
            [new PackageArtifactIntegrity(
                "artifact-anthology-core-v1",
                gamePackage.Id,
                gamePackage.Version,
                gamePackage.Version,
                gamePackage.Kind,
                gamePackage.InstallRoot,
                gamePackage.ArchiveFormat,
                gamePackage.Size,
                gamePackage.Sha256,
                gamePackage.Mirrors,
                gameArchive.Files,
                gamePackage.Files)]);
        var signedCatalog = ManifestSecurity.Sign(integrityPayload, key, "test-key-01");
        var sourceCatalog = Path.Combine(artifacts, "package-integrity.json");
        await File.WriteAllTextAsync(sourceCatalog, JsonSerializer.Serialize(signedCatalog, ManifestJson.Options));
        var integrityArchive = await CreateArchiveFromFilesAsync(
            Path.Combine(artifacts, "integrity.zip"),
            (PackageIntegrityCatalogPath, sourceCatalog));
        var integrityPackage = CreatePackage(
            "anthology-integrity",
            "catalog-v1",
            PackageKind.Launcher,
            "game",
            integrityArchive);
        var publicKeyPath = Path.Combine(_root, "public.pem");
        var manifestPath = await WriteManifestAndKeyAsync(
            key,
            "1.0.0",
            [gamePackage, integrityPackage],
            Path.Combine(_root, "manifest.json"),
            publicKeyPath);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = gameRoot };
        var coordinator = new UpdateCoordinator(new HttpClient());
        var firstCheck = await coordinator.CheckAsync(manifestPath, publicKeyPath, "next", stateRoot, roots);
        await coordinator.ApplyAsync(firstCheck, roots, stateRoot);
        return new InstalledFixture(gameRoot, stateRoot, manifestPath, publicKeyPath, roots, coordinator);
    }

    private static async Task<ArchiveData> CreateArchiveAsync(
        string path,
        params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.SmallestSize);
                await using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(file.Content);
                await stream.WriteAsync(bytes);
            }
        }
        return await ReadArchiveDataAsync(path);
    }

    private static async Task<ArchiveData> CreateArchiveFromFilesAsync(
        string path,
        params (string Path, string SourcePath)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.SmallestSize);
                await using var target = entry.Open();
                await using var source = File.OpenRead(file.SourcePath);
                await source.CopyToAsync(target);
            }
        }
        return await ReadArchiveDataAsync(path);
    }

    private static async Task<ArchiveData> ReadArchiveDataAsync(string path)
    {
        var integrity = new List<PackageFileIntegrity>();
        using (var archive = ZipFile.OpenRead(path))
        {
            foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
            {
                await using var stream = entry.Open();
                integrity.Add(new PackageFileIntegrity(
                    entry.FullName.Replace('\\', '/'),
                    entry.Length,
                    await ArtifactHash.ComputeSha256Async(stream)));
            }
        }
        var bytes = await File.ReadAllBytesAsync(path);
        return new ArchiveData(path, bytes, integrity);
    }

    private static PackageManifest CreatePackage(
        string id,
        string version,
        PackageKind kind,
        string installRoot,
        ArchiveData archive) => new(
        id,
        id,
        version,
        kind,
        installRoot,
        "zip",
        archive.Bytes.Length,
        Convert.ToHexStringLower(SHA256.HashData(archive.Bytes)),
        [new MirrorManifest("local-file", new Uri(archive.Path).AbsoluteUri, 1)],
        archive.Files.Select(file => file.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        PackageUpdateMode.Merge);

    private static PackageArtifactIntegrity CreateArtifact(
        string artifactId,
        PackageManifest package,
        ArchiveData archive,
        string requiredPackageVersion) => new(
        artifactId,
        package.Id,
        package.Version,
        requiredPackageVersion,
        package.Kind,
        package.InstallRoot,
        package.ArchiveFormat,
        package.Size,
        package.Sha256,
        package.Mirrors,
        archive.Files,
        package.Files);

    private static async Task<string> WriteManifestAndKeyAsync(
        ECDsa key,
        string version,
        IReadOnlyList<PackageManifest> packages,
        string manifestPath,
        string publicKeyPath)
    {
        await File.WriteAllTextAsync(publicKeyPath, key.ExportSubjectPublicKeyInfoPem());
        return await WriteManifestAsync(key, version, packages, manifestPath);
    }

    private static async Task<string> WriteManifestAsync(
        ECDsa key,
        string version,
        IReadOnlyList<PackageManifest> packages,
        string manifestPath)
    {
        var signed = ManifestSecurity.Sign(
            new UpdateManifest(4, "next", version, DateTimeOffset.UtcNow, null, packages),
            key,
            "test-key-01");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(signed, ManifestJson.Options));
        return manifestPath;
    }

    private const string PackageIntegrityCatalogPath =
        "AnthologyLauncher/Update/Integrity/package-integrity.json";

    private sealed record ArchiveData(
        string Path,
        byte[] Bytes,
        IReadOnlyList<PackageFileIntegrity> Files);

    private sealed record InstalledFixture(
        string GameRoot,
        string StateRoot,
        string ManifestPath,
        string PublicKeyPath,
        IReadOnlyDictionary<string, string> InstallRoots,
        UpdateCoordinator Coordinator);
}
