using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthology.Contracts;
using Anthology.Releaser.Core;

namespace Anthology.Update.Core.Tests;

public sealed class LoosePackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anthology-loose-{Guid.NewGuid():N}");

    [Fact]
    public void ValidatorAcceptsTemplatesAndExactMirrors()
    {
        var templated = CreatePackage(
            "templated-package",
            [CreateFile("gamedata/configs/a file.ltx", "a")],
            [new MirrorManifest("yandex-disk", "https://disk.yandex.ru/d/example?path=/Game/{path}")]);
        var exactFile = CreateFile(
            "mods/Test/file.script",
            "b",
            [new MirrorManifest("google-drive", "https://drive.google.com/file/d/example/view")]);
        var exact = CreatePackage("exact-package", [exactFile], []);
        var signed = CreateUnsignedForValidation([templated, exact]);

        Assert.Empty(ManifestValidator.Validate(signed));
    }

    [Fact]
    public void ExactFileMirrorKeepsPackageFolderMirrorAsFallback()
    {
        var file = CreateFile(
            "gamedata/configs/a file.ltx",
            "value",
            [new MirrorManifest("google-drive", "https://drive.google.com/file/d/example-file/view", 30)]);
        var package = CreatePackage(
            "multi-source-package",
            [file],
            [new MirrorManifest("yandex-disk", "https://disk.yandex.ru/d/example?path=/Game/{path}", 10)]);

        var mirrors = LoosePackageDownloader.ResolveMirrors(package, file);

        Assert.Equal(2, mirrors.Count);
        Assert.Equal("yandex-disk", mirrors[0].Provider);
        Assert.Equal(
            "https://disk.yandex.ru/d/example?path=/Game/gamedata/configs/a%20file.ltx",
            mirrors[0].Url);
        Assert.Equal("google-drive", mirrors[1].Provider);
        Assert.Equal("https://drive.google.com/file/d/example-file/view", mirrors[1].Url);
    }

    [Fact]
    public void ValidatorRejectsUnsafePathBrokenHashAndTemplate()
    {
        var valid = CreateFile("gamedata/test.ltx", "value");
        var package = CreatePackage(
            "unsafe-package",
            [valid],
            [new MirrorManifest("http", "https://cdn.invalid/no-placeholder")]);
        package = package with { LooseFiles = [valid with { Path = "../outside.ltx" }] };
        var brokenHash = CreatePackage(
            "broken-hash-package",
            [valid],
            [new MirrorManifest("http", "https://cdn.invalid/{path}")]) with
        {
            Sha256 = new string('0', 64),
        };

        var errors = ManifestValidator.Validate(CreateUnsignedForValidation([package, brokenHash]));

        Assert.Contains(errors, error => error.Contains("unsafe path", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("{path}", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("table SHA-256", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidatorRequiresStagedLauncherVersionForLooseProtocol()
    {
        var package = CreatePackage(
            "staged-package",
            [CreateFile("gamedata/test.ltx", "value")],
            [new MirrorManifest("http", "https://cdn.invalid/{path}")]);
        var signed = CreateUnsignedForValidation([package]);
        signed = signed with { Payload = signed.Payload with { MinimumLauncherVersion = null } };

        var error = Assert.Single(ManifestValidator.Validate(signed));

        Assert.Contains("minimum launcher version", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloaderUsesBoundedParallelismRetriesAndVerifiesFiles()
    {
        var first = CreateFile(
            "folder/a file.txt",
            "alpha",
            [new MirrorManifest("http", "https://files.invalid/exact/a%20file.txt")]);
        var second = CreateFile("folder/retry.txt", "bravo");
        var package = CreatePackage(
            "download-package",
            [first, second],
            [new MirrorManifest("http", "https://files.invalid/root/{path}")]);
        using var handler = new FileHandler(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["/exact/a%20file.txt"] = Encoding.UTF8.GetBytes("alpha"),
            ["/root/folder/retry.txt"] = Encoding.UTF8.GetBytes("bravo"),
        }, failFirstPath: "/root/folder/retry.txt", delayMilliseconds: 25);
        using var client = new HttpClient(handler);
        var progress = new InlineProgress<DownloadProgress>(_ => { });
        var staging = Path.Combine(_root, "download-staging");

        await new LoosePackageDownloader(client, maximumParallelDownloads: 2)
            .DownloadAsync(package, staging, package.GetFilePaths(), progress);

        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(staging, "folder", "a file.txt")));
        Assert.Equal("bravo", await File.ReadAllTextAsync(Path.Combine(staging, "folder", "retry.txt")));
        Assert.Equal(2, handler.Attempts["/root/folder/retry.txt"]);
        Assert.InRange(handler.MaximumConcurrency, 1, 2);
    }

    [Fact]
    public async Task CoordinatorCancellationStopsDownloadsAndRemovesOperationStaging()
    {
        var package = CreatePackage(
            "cancel-package",
            [CreateFile("gamedata/slow.txt", "eventual content")],
            [new MirrorManifest("http", "https://files.invalid/{path}")]);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifestPath = await WriteManifestAsync(key, [package]);
        var gameRoot = Path.Combine(_root, "cancel-game");
        var stateRoot = Path.Combine(_root, "cancel-state");
        Directory.CreateDirectory(gameRoot);
        using var handler = new FileHandler(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["/gamedata/slow.txt"] = Encoding.UTF8.GetBytes("eventual content"),
        }, delayMilliseconds: 5_000);
        using var client = new HttpClient(handler);
        var coordinator = new UpdateCoordinator(client);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = gameRoot };
        var check = await coordinator.CheckAsync(manifestPath, GetPublicKeyPath(), "next", stateRoot, roots);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.ApplyAsync(check, roots, stateRoot, cancellationToken: cancellation.Token));

        Assert.False(File.Exists(Path.Combine(gameRoot, "gamedata", "slow.txt")));
        Assert.Empty(Directory.Exists(Path.Combine(stateRoot, "work"))
            ? Directory.EnumerateDirectories(Path.Combine(stateRoot, "work"))
            : []);
    }

    [Fact]
    public async Task RemoteManifestContentLengthIsGuardedBeforeBuffering()
    {
        using var client = new HttpClient(new OversizedManifestHandler());
        var coordinator = new UpdateCoordinator(client);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.CheckAsync(
            "https://updates.invalid/manifest.json",
            Path.Combine(_root, "unused-public-key.pem"),
            "next",
            Path.Combine(_root, "manifest-state")));

        Assert.Contains("128 MiB", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoordinatorDownloadsOnlyDamagedFilesThenRepairsByHash()
    {
        var first = CreateFile("gamedata/a.txt", "alpha");
        var second = CreateFile("gamedata/b.txt", "bravo");
        var package = CreatePackage(
            "anthology-game",
            [first, second],
            [new MirrorManifest("http", "https://files.invalid/{path}")]);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifestPath = await WriteManifestAsync(key, [package]);
        var gameRoot = Path.Combine(_root, "repair-game");
        var stateRoot = Path.Combine(_root, "repair-state");
        Directory.CreateDirectory(Path.Combine(gameRoot, "gamedata"));
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "gamedata", "a.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "gamedata", "b.txt"), "old");
        var bytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["/gamedata/a.txt"] = Encoding.UTF8.GetBytes("alpha"),
            ["/gamedata/b.txt"] = Encoding.UTF8.GetBytes("bravo"),
        };
        using var handler = new FileHandler(bytes);
        using var client = new HttpClient(handler);
        var coordinator = new UpdateCoordinator(client);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = gameRoot };

        var check = await coordinator.CheckAsync(manifestPath, GetPublicKeyPath(), "next", stateRoot, roots);
        Assert.True(check.HasUpdates);
        Assert.Equal(["gamedata/b.txt"], Assert.Single(check.Packages).RepairFiles);
        var result = await coordinator.ApplyAsync(check, roots, stateRoot);

        Assert.Equal(1, result.InstalledFiles);
        Assert.Equal("bravo", await File.ReadAllTextAsync(Path.Combine(gameRoot, "gamedata", "b.txt")));
        Assert.DoesNotContain("/gamedata/a.txt", handler.Attempts.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(Directory.Exists(Path.Combine(stateRoot, "work"))
            ? Directory.EnumerateDirectories(Path.Combine(stateRoot, "work"))
            : []);

        await File.WriteAllTextAsync(Path.Combine(gameRoot, "gamedata", "a.txt"), "corrupt");
        handler.Attempts.Clear();
        var repair = await coordinator.CheckAsync(manifestPath, GetPublicKeyPath(), "next", stateRoot, roots);
        var repairPackage = Assert.Single(repair.Packages);
        Assert.True(repairPackage.RepairRequired);
        Assert.Equal(["gamedata/a.txt"], repairPackage.RepairFiles);
        await coordinator.ApplyAsync(repair, roots, stateRoot);

        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(gameRoot, "gamedata", "a.txt")));
        Assert.Equal(1, handler.Attempts["/gamedata/a.txt"]);
        Assert.DoesNotContain("/gamedata/b.txt", handler.Attempts.Keys, StringComparer.OrdinalIgnoreCase);

        await UpdateCoordinator.RollbackLatestAsync(roots, stateRoot);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(gameRoot, "gamedata", "b.txt")));
    }

    [Fact]
    public async Task LaterLooseFailureRollsBackEarlierPackageAndCleansStaging()
    {
        var good = CreatePackage(
            "first-package",
            [CreateFile("first.txt", "new")],
            [new MirrorManifest("http", "https://files.invalid/first/{path}")]);
        var bad = CreatePackage(
            "second-package",
            [CreateFile("second.txt", "expected")],
            [new MirrorManifest("http", "https://files.invalid/second/{path}")]);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifestPath = await WriteManifestAsync(key, [good, bad]);
        var gameRoot = Path.Combine(_root, "rollback-game");
        var stateRoot = Path.Combine(_root, "rollback-state");
        Directory.CreateDirectory(gameRoot);
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "first.txt"), "old");
        using var handler = new FileHandler(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["/first/first.txt"] = Encoding.UTF8.GetBytes("new"),
            ["/second/second.txt"] = Encoding.UTF8.GetBytes("wrong"),
        });
        using var client = new HttpClient(handler);
        var coordinator = new UpdateCoordinator(client);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = gameRoot };
        var check = await coordinator.CheckAsync(manifestPath, GetPublicKeyPath(), "next", stateRoot, roots);

        await Assert.ThrowsAnyAsync<Exception>(() => coordinator.ApplyAsync(check, roots, stateRoot));

        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(gameRoot, "first.txt")));
        Assert.False(File.Exists(Path.Combine(gameRoot, "second.txt")));
        Assert.Empty(Directory.Exists(Path.Combine(stateRoot, "work"))
            ? Directory.EnumerateDirectories(Path.Combine(stateRoot, "work"))
            : []);
        Assert.Null(await UpdateCoordinator.GetLatestRollbackAsync(stateRoot));
    }

    [Fact]
    public async Task LooseBuildWritesOnlySignedMetadataAndHonorsExclusions()
    {
        var game = Path.Combine(_root, "builder-game");
        var mo2 = Path.Combine(_root, "builder-mo2");
        var output = Path.Combine(_root, "builder-output");
        var publication = Path.Combine(_root, "builder-publication");
        var keys = Path.Combine(_root, "builder-keys");
        Directory.CreateDirectory(Path.Combine(game, "bin"));
        Directory.CreateDirectory(Path.Combine(game, "appdata"));
        Directory.CreateDirectory(Path.Combine(mo2, "mods", "Example"));
        Directory.CreateDirectory(Path.Combine(mo2, "downloads"));
        Directory.CreateDirectory(keys);
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "Anomaly.exe"), "game");
        await File.WriteAllTextAsync(Path.Combine(game, "appdata", "user.ltx"), "private");
        await File.WriteAllTextAsync(Path.Combine(mo2, "ModOrganizer.exe"), "mo2");
        await File.WriteAllTextAsync(Path.Combine(mo2, "mods", "Example", "addon.txt"), "addon");
        await File.WriteAllTextAsync(Path.Combine(mo2, "downloads", "cache.zip"), "private");
        var privateKey = Path.Combine(keys, "private.pem");
        var publicKey = Path.Combine(keys, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKey, publicKey);
        var workspace = new ReleaserWorkspace
        {
            Version = "2.1.300",
            Mirrors =
            [
                new ReleaseMirrorSet
                {
                    Id = "yandex-test",
                    Provider = "yandex-disk",
                    GameUrl = "https://disk.yandex.ru/d/project?path=/Game/{path}",
                    Mo2Url = "https://disk.yandex.ru/d/project?path=/MO2/{path}",
                    Priority = 10,
                },
            ],
        };
        var machine = new ReleaserMachineSettings
        {
            GameSourceRoot = game,
            Mo2SourceRoot = mo2,
            OutputRoot = output,
            PrivateKeyPath = privateKey,
            PublicKeyPath = publicKey,
            KeyId = "loose-builder-test",
            PublicationRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["yandex-test"] = publication,
            },
        };
        var staleArchive = Path.Combine(output, workspace.Version, "stale-archive.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(staleArchive)!);
        await File.WriteAllTextAsync(staleArchive, "must not be published");

        var result = await UnifiedReleaseBuilder.BuildLooseAsync(new UnifiedReleaseRequest(
            workspace,
            machine,
            LooseFileMirrors:
            [
                new LooseFileMirrorOverride(
                    "anthology-game",
                    "bin/Anomaly.exe",
                    [new MirrorManifest("google-drive", "https://drive.google.com/file/d/game-file-id/view", 5)]),
            ],
            MinimumLauncherVersion: "0.8.0-alpha.1"));

        Assert.Empty(result.Artifacts);
        Assert.True(File.Exists(staleArchive));
        Assert.NotNull(result.PublicationFiles);
        Assert.DoesNotContain(staleArchive, result.PublicationFiles, StringComparer.OrdinalIgnoreCase);
        await using var stream = File.OpenRead(result.ManifestPath);
        var signed = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options);
        Assert.NotNull(signed);
        Assert.Equal(5, signed.Payload.SchemaVersion);
        Assert.Equal("0.8.0-alpha.1", signed.Payload.MinimumLauncherVersion);
        Assert.Equal(2, signed.Payload.Packages.Count);
        Assert.All(signed.Payload.Packages, package =>
        {
            Assert.NotNull(package.LooseFiles);
            Assert.Empty(package.Files);
            Assert.Equal("loose", package.ArchiveFormat);
            Assert.Contains("{path}", Assert.Single(package.Mirrors).Url, StringComparison.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain(
            signed.Payload.Packages.Single(package => package.InstallRoot == "game").LooseFiles!,
            file => file.Path.StartsWith("appdata/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            signed.Payload.Packages.Single(package => package.InstallRoot == "modpack").LooseFiles!,
            file => file.Path.StartsWith("downloads/", StringComparison.OrdinalIgnoreCase));
        var googleFile = signed.Payload.Packages
            .Single(package => package.Id == "anthology-game")
            .LooseFiles!
            .Single(file => file.Path == "bin/Anomaly.exe");
        Assert.Equal("google-drive", Assert.Single(googleFile.Mirrors!).Provider);
        Assert.Equal(
            "https://drive.google.com/file/d/game-file-id/view",
            Assert.Single(googleFile.Mirrors!).Url);
        using var verificationKey = ECDsa.Create();
        verificationKey.ImportFromPem(await File.ReadAllTextAsync(publicKey));
        Assert.True(ManifestSecurity.Verify(signed, verificationKey));
        Assert.Empty(ManifestValidator.Validate(signed));

        await ReleasePublicationService.PublishReleaseAsync(result, workspace, machine);
        Assert.True(File.Exists(Path.Combine(publication, workspace.Version, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(publication, workspace.Version, "content.json")));
        Assert.False(File.Exists(Path.Combine(publication, workspace.Version, "stale-archive.zip")));
    }

    [Fact]
    public async Task LooseBuildRequiresExplicitCompatibleLauncherVersion()
    {
        var keys = Path.Combine(_root, "guard-keys");
        Directory.CreateDirectory(keys);
        var privateKey = Path.Combine(keys, "private.pem");
        var publicKey = Path.Combine(keys, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKey, publicKey);
        var machine = new ReleaserMachineSettings
        {
            OutputRoot = Path.Combine(_root, "guard-output"),
            PrivateKeyPath = privateKey,
            PublicKeyPath = publicKey,
            KeyId = "guard-test",
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UnifiedReleaseBuilder.BuildLooseAsync(new UnifiedReleaseRequest(
                new ReleaserWorkspace { Version = "2.1.301" },
                machine)));

        Assert.Contains("compatible launcher", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private async Task<string> WriteManifestAsync(
        ECDsa key,
        IReadOnlyList<PackageManifest> packages)
    {
        Directory.CreateDirectory(_root);
        var payload = new UpdateManifest(
            5,
            "next",
            packages[0].Version,
            DateTimeOffset.UtcNow,
            "0.8.0-alpha.1",
            packages);
        var signed = ManifestSecurity.Sign(payload, key, "loose-test-key");
        var manifestPath = Path.Combine(_root, $"manifest-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(signed, ManifestJson.Options));
        await File.WriteAllTextAsync(GetPublicKeyPath(), key.ExportSubjectPublicKeyInfoPem());
        return manifestPath;
    }

    private string GetPublicKeyPath() => Path.Combine(_root, "trusted-public.pem");

    private static SignedUpdateManifest CreateUnsignedForValidation(IReadOnlyList<PackageManifest> packages) => new(
        new UpdateManifest(5, "next", "2.1.300", DateTimeOffset.UtcNow, "0.8.0", packages),
        new ManifestSignature(ManifestSecurity.Algorithm, "test-key", "AA=="));

    private static PackageManifest CreatePackage(
        string id,
        IReadOnlyList<PackageLooseFile> files,
        IReadOnlyList<MirrorManifest> mirrors,
        string version = "2.1.300") => new(
        id,
        id,
        version,
        PackageKind.Game,
        "game",
        "loose",
        files.Sum(file => file.Size),
        LoosePackageHash.ComputeSha256(files),
        mirrors,
        [],
        PackageUpdateMode.Merge,
        LooseFiles: files);

    private static PackageLooseFile CreateFile(
        string path,
        string content,
        IReadOnlyList<MirrorManifest>? mirrors = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new PackageLooseFile(
            path,
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            mirrors);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FileHandler(
        IReadOnlyDictionary<string, byte[]> files,
        string? failFirstPath = null,
        int delayMilliseconds = 0) : HttpMessageHandler
    {
        private int _concurrency;
        private int _maximumConcurrency;

        public Dictionary<string, int> Attempts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath
                       ?? throw new InvalidOperationException("Request has no URI.");
            lock (Attempts)
            {
                Attempts.TryGetValue(path, out var attempt);
                Attempts[path] = attempt + 1;
            }
            var concurrency = Interlocked.Increment(ref _concurrency);
            UpdateMaximum(concurrency);
            try
            {
                if (delayMilliseconds > 0)
                {
                    await Task.Delay(delayMilliseconds, cancellationToken);
                }
                if (string.Equals(path, failFirstPath, StringComparison.OrdinalIgnoreCase)
                    && Attempts[path] == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
                }
                if (!files.TryGetValue(path, out var content))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content),
                    RequestMessage = request,
                };
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrency);
                if (value <= current || Interlocked.CompareExchange(ref _maximumConcurrency, value, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class OversizedManifestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = 129L * 1024 * 1024;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request,
            });
        }
    }
}
