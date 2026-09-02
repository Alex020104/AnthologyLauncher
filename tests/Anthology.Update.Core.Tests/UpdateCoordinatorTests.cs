using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Anthology.Contracts;

namespace Anthology.Update.Core.Tests;

public sealed class UpdateCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anthology-update-flow-{Guid.NewGuid():N}");

    [Fact]
    public async Task SignedPackageIsDownloadedExtractedInstalledAndRecorded()
    {
        const string relativePath = "gamedata/configs/anthology-test.ltx";
        var archiveBytes = CreateArchive((relativePath, "working = true"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = CreateSignedManifest(key, archiveBytes, [relativePath]);
        var manifestPath = await WriteTrustFilesAsync(key, signed);
        var gameRoot = Path.Combine(_root, "game");
        var stateRoot = Path.Combine(_root, "state");
        Directory.CreateDirectory(gameRoot);
        using var client = new HttpClient(new ArtifactHandler(archiveBytes));
        var coordinator = new UpdateCoordinator(client);

        var check = await coordinator.CheckAsync(manifestPath, GetPublicKeyPath(), "next", stateRoot);
        Assert.True(check.HasUpdates);

        var result = await coordinator.ApplyAsync(
            check,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = gameRoot },
            stateRoot);

        Assert.Equal(1, result.InstalledPackages);
        Assert.Equal("working = true", await File.ReadAllTextAsync(Path.Combine(gameRoot, relativePath)));
        var secondCheck = await coordinator.CheckAsync(manifestPath, GetPublicKeyPath(), "next", stateRoot);
        Assert.False(secondCheck.HasUpdates);

        var candidate = await UpdateCoordinator.GetLatestRollbackAsync(stateRoot);
        Assert.NotNull(candidate);
        Assert.Equal("1.0.0", candidate.ToVersion);
        var rollback = await UpdateCoordinator.RollbackLatestAsync(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = gameRoot },
            stateRoot);

        Assert.Equal("anthology-core", rollback.PackageId);
        Assert.False(File.Exists(Path.Combine(gameRoot, relativePath)));
        var afterRollback = await coordinator.CheckAsync(manifestPath, GetPublicKeyPath(), "next", stateRoot);
        Assert.True(afterRollback.HasUpdates);
    }

    [Fact]
    public async Task ManifestSignedByAnotherKeyIsRejected()
    {
        var archiveBytes = CreateArchive(("gamedata/test.txt", "test"));
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var trustedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = CreateSignedManifest(signingKey, archiveBytes, ["gamedata/test.txt"]);
        var manifestPath = await WriteTrustFilesAsync(trustedKey, signed);
        using var client = new HttpClient(new ArtifactHandler(archiveBytes));

        await Assert.ThrowsAsync<CryptographicException>(() =>
            new UpdateCoordinator(client).CheckAsync(
                manifestPath,
                GetPublicKeyPath(),
                "next",
                Path.Combine(_root, "state")));
    }

    [Fact]
    public async Task RawGitHubManifestBypassesSharedCacheWithoutDroppingExistingQuery()
    {
        var archiveBytes = CreateArchive(("gamedata/test.txt", "test"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = CreateSignedManifest(key, archiveBytes, ["gamedata/test.txt"]);
        await WriteTrustFilesAsync(key, signed);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(signed, ManifestJson.Options);
        using var handler = new ManifestHandler(manifestBytes);
        using var client = new HttpClient(handler);
        var coordinator = new UpdateCoordinator(client);
        const string source = "https://raw.githubusercontent.com/owner/repository/alpha15/manifest.json?channel=next";

        await coordinator.CheckAsync(source, GetPublicKeyPath(), "next", Path.Combine(_root, "github-state-1"));
        await coordinator.CheckAsync(source, GetPublicKeyPath(), "next", Path.Combine(_root, "github-state-2"));

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("raw.githubusercontent.com", request.RequestUri.Host);
            Assert.Equal("/owner/repository/alpha15/manifest.json", request.RequestUri.AbsolutePath);
            Assert.Contains("channel=next", request.RequestUri.Query, StringComparison.Ordinal);
            Assert.Matches("(?:^|[?&])anthology_cb=[0-9a-f]{32}(?:&|$)", request.RequestUri.Query);
            Assert.True(request.NoCache);
            Assert.True(request.NoStore);
            Assert.True(request.ZeroMaxAge);
            Assert.True(request.PragmaNoCache);
        });
        Assert.NotEqual(handler.Requests[0].RequestUri, handler.Requests[1].RequestUri);
    }

    [Fact]
    public async Task NonGitHubManifestUrlIsNotRewrittenOrGivenGitHubCacheHeaders()
    {
        var archiveBytes = CreateArchive(("gamedata/test.txt", "test"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = CreateSignedManifest(key, archiveBytes, ["gamedata/test.txt"]);
        await WriteTrustFilesAsync(key, signed);
        using var handler = new ManifestHandler(JsonSerializer.SerializeToUtf8Bytes(signed, ManifestJson.Options));
        using var client = new HttpClient(handler);
        var coordinator = new UpdateCoordinator(client);
        const string source = "https://cdn.example/anthology/manifest.json?channel=next";

        await coordinator.CheckAsync(source, GetPublicKeyPath(), "next", Path.Combine(_root, "cdn-state"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(source, request.RequestUri.AbsoluteUri);
        Assert.False(request.NoCache);
        Assert.False(request.NoStore);
        Assert.False(request.ZeroMaxAge);
        Assert.False(request.PragmaNoCache);
    }

    [Fact]
    public async Task ArchiveWithUndeclaredFileIsRejectedBeforeInstallation()
    {
        var archiveBytes = CreateArchive(
            ("gamedata/declared.txt", "declared"),
            ("gamedata/undeclared.txt", "undeclared"));
        var archivePath = Path.Combine(_root, "unsafe.zip");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(archivePath, archiveBytes);
        var package = CreatePackage(archiveBytes, ["gamedata/declared.txt"]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeZipExtractor.ExtractAsync(archivePath, Path.Combine(_root, "staging"), package));
    }

    [Fact]
    public async Task ManagedExactReleaseDeletesOldManagedFileAndRollbackRestoresIt()
    {
        var firstArchive = CreateArchive(("kept.txt", "v1"), ("obsolete.txt", "old"));
        var secondArchive = CreateArchive(("kept.txt", "v2"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var gameRoot = Path.Combine(_root, "managed-game");
        var stateRoot = Path.Combine(_root, "managed-state");
        Directory.CreateDirectory(gameRoot);
        Directory.CreateDirectory(Path.Combine(gameRoot, "appdata"));
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "legacy.txt"), "remove-on-first-managed-release");
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "appdata", "user.ltx"), "preserve-user-data");

        var firstPackage = CreatePackage(firstArchive, ["kept.txt", "obsolete.txt"]) with
        {
            Version = "2.1.131",
            UpdateMode = PackageUpdateMode.ManagedExact,
            PruneInstallRoot = true,
            PreservedPaths = ["appdata"],
        };
        var firstManifest = ManifestSecurity.Sign(new UpdateManifest(
            2, "next", "2.1.131", DateTimeOffset.UtcNow, null, [firstPackage]), key, "test-key-01");
        var manifestPath = await WriteTrustFilesAsync(key, firstManifest);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = gameRoot };
        using (var client = new HttpClient(new ArtifactHandler(firstArchive)))
        {
            var coordinator = new UpdateCoordinator(client);
            var check = await coordinator.CheckAsync(manifestPath, GetPublicKeyPath(), "next", stateRoot);
            await coordinator.ApplyAsync(check, roots, stateRoot);
        }

        Assert.False(File.Exists(Path.Combine(gameRoot, "legacy.txt")));
        Assert.Equal("preserve-user-data", await File.ReadAllTextAsync(Path.Combine(gameRoot, "appdata", "user.ltx")));

        var secondPackage = CreatePackage(secondArchive, ["kept.txt"]) with
        {
            Version = "2.1.132",
            UpdateMode = PackageUpdateMode.ManagedExact,
        };
        var secondManifest = ManifestSecurity.Sign(new UpdateManifest(
            2, "next", "2.1.132", DateTimeOffset.UtcNow, null, [secondPackage]), key, "test-key-01");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(secondManifest, ManifestJson.Options));
        using (var client = new HttpClient(new ArtifactHandler(secondArchive)))
        {
            var coordinator = new UpdateCoordinator(client);
            var check = await coordinator.CheckAsync(manifestPath, GetPublicKeyPath(), "next", stateRoot);
            var result = await coordinator.ApplyAsync(check, roots, stateRoot);
            Assert.Equal(1, result.DeletedFiles);
        }

        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(gameRoot, "kept.txt")));
        Assert.False(File.Exists(Path.Combine(gameRoot, "obsolete.txt")));

        await UpdateCoordinator.RollbackLatestAsync(roots, stateRoot);

        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(gameRoot, "kept.txt")));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(gameRoot, "obsolete.txt")));
    }

    [Fact]
    public async Task ExplicitDeletionOnlyPackageRemovesFileAndRollbackRestoresIt()
    {
        var archiveBytes = CreateArchive();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var gameRoot = Path.Combine(_root, "delete-only-game");
        var stateRoot = Path.Combine(_root, "delete-only-state");
        Directory.CreateDirectory(gameRoot);
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "obsolete.txt"), "restore-me");
        var package = CreatePackage(archiveBytes, []) with
        {
            Version = "2.1.150",
            DeletedFiles = ["obsolete.txt"],
        };
        var manifest = ManifestSecurity.Sign(new UpdateManifest(
            3, "next", "2.1.150", DateTimeOffset.UtcNow, null, [package]), key, "test-key-01");
        var manifestPath = await WriteTrustFilesAsync(key, manifest);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = gameRoot };
        using var client = new HttpClient(new ArtifactHandler(archiveBytes));
        var coordinator = new UpdateCoordinator(client);

        var check = await coordinator.CheckAsync(manifestPath, GetPublicKeyPath(), "next", stateRoot);
        var result = await coordinator.ApplyAsync(check, roots, stateRoot);

        Assert.Equal(1, result.DeletedFiles);
        Assert.False(File.Exists(Path.Combine(gameRoot, "obsolete.txt")));
        await UpdateCoordinator.RollbackLatestAsync(roots, stateRoot);
        Assert.Equal("restore-me", await File.ReadAllTextAsync(Path.Combine(gameRoot, "obsolete.txt")));
    }

    [Fact]
    public async Task DirectoryDeletionRemovesNestedAddonAndRollbackRestoresIt()
    {
        var archiveBytes = CreateArchive();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var gameRoot = Path.Combine(_root, "delete-folder-game");
        var stateRoot = Path.Combine(_root, "delete-folder-state");
        var addonRoot = Path.Combine(gameRoot, "gamedata", "addons", "legacy-addon");
        Directory.CreateDirectory(Path.Combine(addonRoot, "configs"));
        Directory.CreateDirectory(Path.Combine(addonRoot, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(addonRoot, "configs", "legacy.ltx"), "restore-config");
        await File.WriteAllTextAsync(Path.Combine(addonRoot, "scripts", "legacy.script"), "restore-script");
        var package = CreatePackage(archiveBytes, []) with
        {
            Version = "2.1.151",
            DeletedDirectories = ["gamedata/addons/legacy-addon"],
        };
        var manifest = ManifestSecurity.Sign(new UpdateManifest(
            4, "next", "2.1.151", DateTimeOffset.UtcNow, null, [package]), key, "test-key-01");
        var manifestPath = await WriteTrustFilesAsync(key, manifest);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = gameRoot };
        using var client = new HttpClient(new ArtifactHandler(archiveBytes));
        var coordinator = new UpdateCoordinator(client);

        var check = await coordinator.CheckAsync(manifestPath, GetPublicKeyPath(), "next", stateRoot);
        var result = await coordinator.ApplyAsync(check, roots, stateRoot);

        Assert.Equal(2, result.DeletedFiles);
        Assert.False(Directory.Exists(addonRoot));
        await UpdateCoordinator.RollbackLatestAsync(roots, stateRoot);
        Assert.Equal("restore-config", await File.ReadAllTextAsync(Path.Combine(addonRoot, "configs", "legacy.ltx")));
        Assert.Equal("restore-script", await File.ReadAllTextAsync(Path.Combine(addonRoot, "scripts", "legacy.script")));
    }

    [Fact]
    public async Task MissingDirectoryDeletionIsSuccessfulNoOp()
    {
        var archiveBytes = CreateArchive();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var gameRoot = Path.Combine(_root, "missing-folder-game");
        var stateRoot = Path.Combine(_root, "missing-folder-state");
        Directory.CreateDirectory(gameRoot);
        var package = CreatePackage(archiveBytes, []) with
        {
            Version = "2.1.152",
            DeletedDirectories = ["mods/already-removed-addon"],
        };
        var manifest = ManifestSecurity.Sign(new UpdateManifest(
            4, "next", "2.1.152", DateTimeOffset.UtcNow, null, [package]), key, "test-key-01");
        var manifestPath = await WriteTrustFilesAsync(key, manifest);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = gameRoot };
        using var client = new HttpClient(new ArtifactHandler(archiveBytes));
        var coordinator = new UpdateCoordinator(client);

        var check = await coordinator.CheckAsync(manifestPath, GetPublicKeyPath(), "next", stateRoot);
        var result = await coordinator.ApplyAsync(check, roots, stateRoot);

        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(1, result.InstalledPackages);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private async Task<string> WriteTrustFilesAsync(ECDsa trustedKey, SignedUpdateManifest signed)
    {
        Directory.CreateDirectory(_root);
        var manifestPath = Path.Combine(_root, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(signed, ManifestJson.Options));
        await File.WriteAllTextAsync(GetPublicKeyPath(), trustedKey.ExportSubjectPublicKeyInfoPem());
        return manifestPath;
    }

    private string GetPublicKeyPath() => Path.Combine(_root, "trusted.pub.pem");

    private static SignedUpdateManifest CreateSignedManifest(
        ECDsa key,
        byte[] archiveBytes,
        IReadOnlyList<string> files)
    {
        var package = CreatePackage(archiveBytes, files);
        var manifest = new UpdateManifest(
            1,
            "next",
            "1.0.0",
            new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero),
            null,
            [package]);
        return ManifestSecurity.Sign(manifest, key, "test-key-01");
    }

    private static PackageManifest CreatePackage(byte[] archiveBytes, IReadOnlyList<string> files) => new(
        "anthology-core",
        "Anthology Core",
        "1.0.0",
        PackageKind.Game,
        "game",
        "zip",
        archiveBytes.Length,
        Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant(),
        [new MirrorManifest("direct", "https://updates.invalid/anthology-core.zip", 10)],
        files);

    private static byte[] CreateArchive(params (string Path, string Content)[] files)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(file.Content);
            }
        }

        return memory.ToArray();
    }

    private sealed class ArtifactHandler(byte[] artifact) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(artifact),
                RequestMessage = request,
            });
    }

    private sealed class ManifestHandler(byte[] manifest) : HttpMessageHandler
    {
        public List<ManifestRequestSnapshot> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new ManifestRequestSnapshot(
                request.RequestUri ?? throw new InvalidOperationException("Manifest request has no URI."),
                request.Headers.CacheControl?.NoCache == true,
                request.Headers.CacheControl?.NoStore == true,
                request.Headers.CacheControl?.MaxAge == TimeSpan.Zero,
                request.Headers.Pragma.Any(value =>
                    string.Equals(value.Name, "no-cache", StringComparison.OrdinalIgnoreCase))));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(manifest),
                RequestMessage = request,
            });
        }
    }

    private sealed record ManifestRequestSnapshot(
        Uri RequestUri,
        bool NoCache,
        bool NoStore,
        bool ZeroMaxAge,
        bool PragmaNoCache);
}
