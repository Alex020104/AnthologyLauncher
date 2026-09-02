using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthology.Contracts;

namespace Anthology.Update.Core.Tests;

public sealed class Mo2PackageScopePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"anthology-mo2-scope-{Guid.NewGuid():N}");

    [Fact]
    public void ManifestRejectsEveryRootLevelDirectiveForSelectedModpackPackage()
    {
        var valid = CreatePackage(["mods/Addon/gamedata/configs/test.ltx"]);
        PackageInstallScopePolicy.ValidateAndThrow(valid);

        var invalidPackages = new[]
        {
            valid with { Files = ["Addon/gamedata/configs/test.ltx"] },
            valid with { DeletedFiles = ["Addon/obsolete.ltx"] },
            valid with { DeletedDirectories = ["Addon"] },
            valid with { PreservedPaths = ["profiles"] },
            valid with { PruneInstallRoot = true },
            valid with { InstallRoot = "mods" },
        };

        foreach (var package in invalidPackages)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var signed = ManifestSecurity.Sign(CreateManifest(package), key, "test-key-01");
            var errors = ManifestValidator.Validate(signed);

            Assert.Contains(errors, error =>
                error.Contains(package.Id, StringComparison.OrdinalIgnoreCase)
                && (error.Contains("mods/**", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("install root", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public void OtherFullMo2PackageCanStillManageTheCompleteMo2Installation()
    {
        var fullPackage = CreatePackage(
            ["ModOrganizer.exe", "profiles/Anthology/modlist.txt", "mods/Addon/meta.ini"],
            "anthology-mo2") with
        {
            UpdateMode = PackageUpdateMode.ManagedExact,
            PruneInstallRoot = true,
            PreservedPaths = ["profiles", "downloads", "overwrite", "ModOrganizer.ini"],
        };
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = ManifestSecurity.Sign(CreateManifest(fullPackage), key, "test-key-01");

        Assert.Empty(ManifestValidator.Validate(signed));
    }

    [Fact]
    public void IntegrityCatalogRejectsRootLevelRepairSourceForSelectedModpackPackage()
    {
        var hash = new string('a', 64);
        var artifact = new PackageArtifactIntegrity(
            "artifact-selected-modpack-v1",
            PackageInstallScopePolicy.Mo2ModsOnlyPackageId,
            "1.0.0",
            "1.0.0",
            PackageKind.Modpack,
            "modpack",
            "zip",
            42,
            hash,
            [new MirrorManifest("local-file", new Uri(Path.Combine(_root, "archive.zip")).AbsoluteUri)],
            [new PackageFileIntegrity("Addon/root-file.ltx", 1, hash)],
            ["Addon/root-file.ltx"]);
        var catalog = new SignedPackageIntegrityCatalog(
            new PackageIntegrityCatalog(1, "next", "1.0.0", DateTimeOffset.UtcNow, [artifact]),
            new ManifestSignature(ManifestSecurity.Algorithm, "test-key-01", "signature"));

        var exception = Assert.Throws<InvalidDataException>(
            () => PackageIntegrityCatalogValidator.ValidateAndThrow(catalog));

        Assert.Contains("mods/**", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ArchiveWithUndeclaredRootEntryIsRejectedBeforeStagingIsWritten()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "bad-layout.zip");
        await CreateArchiveAsync(
            archivePath,
            ("mods/Good Addon/meta.ini", "good"),
            ("Bad Addon/gamedata/configs/bad.ltx", "bad"));
        var package = CreatePackage(["mods/Good Addon/meta.ini"]) with
        {
            Size = new FileInfo(archivePath).Length,
            Sha256 = await ArtifactHash.ComputeSha256Async(archivePath),
        };
        var stagingRoot = Path.Combine(_root, "staging");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => SafeZipExtractor.ExtractAsync(archivePath, stagingRoot, package));

        Assert.Contains("mods/**", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(stagingRoot));
    }

    [Theory]
    [InlineData(PackageUpdateMode.Merge)]
    [InlineData(PackageUpdateMode.ManagedExact)]
    public async Task SafeUpdateMigratesLegacyManagedStateWithoutTouchingRootFile(
        PackageUpdateMode updateMode)
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "valid-layout.zip");
        await CreateArchiveAsync(archivePath, ("mods/New Addon/meta.ini", "new"));
        var package = CreatePackage(["mods/New Addon/meta.ini"]) with
        {
            Version = "2.1.161",
            Size = new FileInfo(archivePath).Length,
            Sha256 = await ArtifactHash.ComputeSha256Async(archivePath),
            Mirrors = [new MirrorManifest("local-file", new Uri(archivePath).AbsoluteUri, 1)],
            UpdateMode = updateMode,
        };
        var stateRoot = Path.Combine(_root, "state");
        var modpackRoot = Path.Combine(_root, "MO2");
        var legacyFile = Path.Combine(modpackRoot, "Legacy Addon", "meta.ini");
        var previousManagedFile = Path.Combine(modpackRoot, "mods", "Previous Addon", "meta.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(previousManagedFile)!);
        await File.WriteAllTextAsync(legacyFile, "keep");
        await File.WriteAllTextAsync(previousManagedFile, "previous");
        var managedPath = Path.Combine(stateRoot, "managed-files", package.Id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(managedPath)!);
        await File.WriteAllTextAsync(
            managedPath,
            "[\"Legacy Addon/meta.ini\",\"mods/Previous Addon/meta.ini\"]");
        var managedIntegrityPath = Path.Combine(stateRoot, "managed-integrity", package.Id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(managedIntegrityPath)!);
        await File.WriteAllTextAsync(
            managedIntegrityPath,
            $$"""
            {
              "packageVersion": "2.1.160",
              "archiveSha256": "{{new string('a', 64)}}",
              "files": [
                { "path": "Legacy Addon/meta.ini", "size": 4, "sha256": "{{new string('b', 64)}}" },
                { "path": "mods/Previous Addon/meta.ini", "size": 8, "sha256": "{{await ArtifactHash.ComputeSha256Async(previousManagedFile)}}" }
              ]
            }
            """);
        var signed = new SignedUpdateManifest(
            CreateManifest(package),
            new ManifestSignature(ManifestSecurity.Algorithm, "test-key-01", "signature"));
        var check = new UpdateCheckResult(
            signed,
            [new PackageUpdate(package, "0.9.0", true)],
            "test-key-01");
        var coordinator = new UpdateCoordinator(new HttpClient());

        var result = await coordinator.ApplyAsync(
            check,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["modpack"] = modpackRoot },
            stateRoot);

        Assert.Equal(1, result.InstalledPackages);
        Assert.Equal("keep", await File.ReadAllTextAsync(legacyFile));
        Assert.Equal("new", await File.ReadAllTextAsync(
            Path.Combine(modpackRoot, "mods", "New Addon", "meta.ini")));
        Assert.Equal(updateMode == PackageUpdateMode.Merge, File.Exists(previousManagedFile));

        var managedFiles = JsonSerializer.Deserialize<string[]>(
            await File.ReadAllTextAsync(managedPath),
            ManifestJson.Options);
        Assert.NotNull(managedFiles);
        Assert.DoesNotContain(managedFiles, path =>
            !path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("mods/New Addon/meta.ini", managedFiles, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(updateMode == PackageUpdateMode.Merge, managedFiles.Contains(
            "mods/Previous Addon/meta.ini",
            StringComparer.OrdinalIgnoreCase));

        using var integrity = JsonDocument.Parse(await File.ReadAllTextAsync(managedIntegrityPath));
        var integrityPaths = integrity.RootElement.GetProperty("files")
            .EnumerateArray()
            .Select(file => file.GetProperty("path").GetString()!)
            .ToArray();
        Assert.DoesNotContain(integrityPaths, path =>
            !path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("mods/New Addon/meta.ini", integrityPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(updateMode == PackageUpdateMode.Merge, integrityPaths.Contains(
            "mods/Previous Addon/meta.ini",
            StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SameVersionRepairCheckIgnoresUnsafeLegacyIntegrityEntry()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "same-version.zip");
        await CreateArchiveAsync(archivePath, ("mods/Current Addon/meta.ini", "current"));
        var package = CreatePackage(["mods/Current Addon/meta.ini"]) with
        {
            Version = "2.1.161",
            Size = new FileInfo(archivePath).Length,
            Sha256 = await ArtifactHash.ComputeSha256Async(archivePath),
            Mirrors = [new MirrorManifest("local-file", new Uri(archivePath).AbsoluteUri, 1)],
        };
        var stateRoot = Path.Combine(_root, "repair-state");
        var modpackRoot = Path.Combine(_root, "repair-MO2");
        var currentFile = Path.Combine(modpackRoot, "mods", "Current Addon", "meta.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(currentFile)!);
        await File.WriteAllTextAsync(currentFile, "current");
        Directory.CreateDirectory(stateRoot);
        await File.WriteAllTextAsync(
            Path.Combine(stateRoot, "installed-packages.json"),
            "{\"packages\":{\"anthology-files-modpack\":\"2.1.161\"}}");
        var managedIntegrityPath = Path.Combine(stateRoot, "managed-integrity", package.Id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(managedIntegrityPath)!);
        await File.WriteAllTextAsync(
            managedIntegrityPath,
            $$"""
            {
              "packageVersion": "2.1.161",
              "archiveSha256": "{{package.Sha256}}",
              "files": [
                { "path": "Legacy Addon/missing.ini", "size": 1, "sha256": "{{new string('b', 64)}}" },
                { "path": "mods/Current Addon/meta.ini", "size": 7, "sha256": "{{await ArtifactHash.ComputeSha256Async(currentFile)}}" }
              ]
            }
            """);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifestPath = Path.Combine(_root, "same-version-manifest.json");
        var publicKeyPath = Path.Combine(_root, "same-version-public.pem");
        await File.WriteAllTextAsync(publicKeyPath, key.ExportSubjectPublicKeyInfoPem());
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(
                ManifestSecurity.Sign(CreateManifest(package), key, "test-key-01"),
                ManifestJson.Options));
        var coordinator = new UpdateCoordinator(new HttpClient());

        var check = await coordinator.CheckAsync(
            manifestPath,
            publicKeyPath,
            "next",
            stateRoot,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["modpack"] = modpackRoot });

        var packageCheck = Assert.Single(check.Packages);
        Assert.False(packageCheck.UpdateAvailable);
        Assert.False(packageCheck.RepairRequired);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static PackageManifest CreatePackage(
        IReadOnlyList<string> files,
        string id = PackageInstallScopePolicy.Mo2ModsOnlyPackageId) => new(
        id,
        "MO2 package",
        "1.0.0",
        PackageKind.Modpack,
        "modpack",
        "zip",
        42,
        new string('a', 64),
        [new MirrorManifest("https", "https://example.test/archive.zip")],
        files,
        PackageUpdateMode.Merge);

    private static UpdateManifest CreateManifest(PackageManifest package) => new(
        4,
        "next",
        "1.0.0",
        DateTimeOffset.UtcNow,
        null,
        [package]);

    private static async Task CreateArchiveAsync(
        string path,
        params (string Path, string Content)[] files)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            var entry = archive.CreateEntry(file.Path, CompressionLevel.SmallestSize);
            await using var stream = entry.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(file.Content));
        }
    }
}
