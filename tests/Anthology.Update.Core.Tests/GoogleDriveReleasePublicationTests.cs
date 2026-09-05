using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthology.Contracts;
using Anthology.Releaser.Core;
using Anthology.Update.Core;

namespace Anthology.Update.Core.Tests;

public sealed class GoogleDriveReleasePublicationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"anthology-google-publication-{Guid.NewGuid():N}");

    [Fact]
    public async Task PublishesPayloadsEnrichesExactMirrorsAndActivatesStableManifestLast()
    {
        const string version = "2.1.300";
        var outputRoot = Path.Combine(_root, "output");
        var versionRoot = Path.Combine(outputRoot, version);
        var yandexRoot = Path.Combine(_root, "yandex");
        Directory.CreateDirectory(versionRoot);
        var (privateKeyPath, publicKeyPath) = CreateKeys(Path.Combine(_root, "keys"));
        var gameArchiveName = $"anthology-game-{version}.zip";
        var gameArchivePath = Path.Combine(versionRoot, gameArchiveName);
        var addonRelativePath = "addons/demo-addon/demo-addon.zip";
        var addonPath = Path.Combine(versionRoot, addonRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(addonPath)!);
        await File.WriteAllBytesAsync(gameArchivePath, [1, 3, 3, 7, 9]);
        await File.WriteAllBytesAsync(addonPath, [2, 4, 6, 8]);
        var gameHash = await ArtifactHash.ComputeSha256Async(gameArchivePath);
        var addonHash = await ArtifactHash.ComputeSha256Async(addonPath);
        var looseFile = new PackageLooseFile(
            "gamedata/configs/current.ltx",
            7,
            new string('b', 64),
            [new MirrorManifest("yandex-disk", "https://disk.example/current.ltx", 10)]);
        var archivePackage = new PackageManifest(
            "anthology-game",
            "Game archive",
            version,
            PackageKind.Game,
            "game",
            "zip",
            new FileInfo(gameArchivePath).Length,
            gameHash,
            [new MirrorManifest("yandex-disk", $"https://disk.example/{version}/{gameArchiveName}", 10)],
            ["bin/xrEngine.exe"]);
        var loosePackage = new PackageManifest(
            "anthology-mo2",
            "MO2 loose",
            version,
            PackageKind.Modpack,
            "modpack",
            "loose",
            looseFile.Size,
            LoosePackageHash.ComputeSha256([looseFile]),
            [new MirrorManifest("yandex-disk", "https://disk.example/mo2/{path}", 10)],
            [],
            PackageUpdateMode.ManagedExact,
            LooseFiles: [looseFile]);
        var catalog = new ContentCatalog(
            4,
            version,
            DateTimeOffset.UtcNow,
            [
                new ContentDocument(
                    "demo-addon",
                    ContentKind.Mod,
                    "mods",
                    "Demo addon",
                    string.Empty,
                    string.Empty,
                    [],
                    [],
                    new ContentDownload(
                        "demo-addon.zip",
                        new FileInfo(addonPath).Length,
                        addonHash,
                        [new MirrorManifest("yandex-disk", $"https://disk.example/{version}/{addonRelativePath}", 10)])),
            ]);
        var payload = new UpdateManifest(
            5,
            "next",
            version,
            DateTimeOffset.UtcNow,
            "0.7.0-alpha.18",
            [archivePackage, loosePackage],
            catalog);
        using (var privateKey = ECDsa.Create())
        {
            privateKey.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
            var signed = ManifestSecurity.Sign(payload, privateKey, "google-publication-test");
            await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(
                Path.Combine(versionRoot, "content.json"),
                catalog);
            await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(
                Path.Combine(versionRoot, "manifest.json"),
                signed);
        }

        var mirror = new ReleaseMirrorSet
        {
            Id = "yandex",
            Provider = "yandex-disk",
            Priority = 10,
        };
        var workspace = new ReleaserWorkspace
        {
            Version = version,
            Channel = "next",
            Mirrors = [mirror],
        };
        var machine = CreateMachine(outputRoot, privateKeyPath, publicKeyPath);
        machine.PublicationRoots[mirror.Id] = yandexRoot;
        var runner = new InMemoryRcloneRunner();
        var publisher = new GoogleDrivePublisher(runner);
        var release = new UnifiedReleaseResult(
            version,
            Path.Combine(versionRoot, "manifest.json"),
            [gameArchivePath, addonPath],
            2,
            new FileInfo(gameArchivePath).Length + new FileInfo(addonPath).Length,
            1,
            [gameArchiveName, addonRelativePath, "content.json", "manifest.json"]);

        var result = await ReleasePublicationService.PublishReleaseAsync(
            release,
            workspace,
            machine,
            publisher);

        Assert.Equal(2, result.Targets);
        Assert.Contains(result.Destinations, path => path.StartsWith("google-drive:/", StringComparison.Ordinal));
        var publishedManifestPath = Path.Combine(yandexRoot, version, "manifest.json");
        await using var publishedStream = File.OpenRead(publishedManifestPath);
        var published = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(publishedStream, ManifestJson.Options);
        Assert.NotNull(published);
        using (var publicKey = ECDsa.Create())
        {
            publicKey.ImportFromPem(await File.ReadAllTextAsync(publicKeyPath));
            Assert.True(ManifestSecurity.Verify(published, publicKey));
        }

        var publishedArchive = Assert.Single(published.Payload.Packages, item => item.Id == archivePackage.Id);
        var archiveGoogleMirror = Assert.Single(
            publishedArchive.Mirrors,
            item => item.Provider == GoogleDrivePublisher.Provider);
        Assert.Contains("drive.google.com/file/d/", archiveGoogleMirror.Url, StringComparison.Ordinal);
        var publishedLoose = Assert.Single(published.Payload.Packages, item => item.Id == loosePackage.Id);
        Assert.DoesNotContain(
            publishedLoose.Mirrors,
            item => item.Provider == GoogleDrivePublisher.Provider);
        Assert.DoesNotContain(
            Assert.Single(publishedLoose.LooseFiles!).Mirrors!,
            item => item.Provider == GoogleDrivePublisher.Provider);
        var publishedDownload = Assert.Single(published.Payload.Content!.Items).Download!;
        Assert.Single(
            publishedDownload.Mirrors,
            item => item.Provider == GoogleDrivePublisher.Provider);

        var copiedCatalog = JsonSerializer.Deserialize<ContentCatalog>(
            await File.ReadAllTextAsync(Path.Combine(yandexRoot, version, "content.json")),
            ManifestJson.Options);
        Assert.NotNull(copiedCatalog);
        Assert.Equal(
            publishedDownload.Mirrors,
            Assert.Single(copiedCatalog.Items).Download!.Mirrors);

        var copyCommands = runner.Commands.Where(command => command.Arguments[0] == "copyto").ToArray();
        Assert.NotEmpty(copyCommands);
        Assert.EndsWith(
            ":ANTHOLOGY/AnthologyUpdateChannel/manifest.json",
            copyCommands[^1].Arguments[2],
            StringComparison.Ordinal);
        Assert.Contains(
            copyCommands,
            command => command.Arguments[2].EndsWith(
                $"/AnthologyUpdateChannel/{version}/{gameArchiveName}",
                StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments[0] == "sync");

        var stableRemote = runner.Files["anthology drive:ANTHOLOGY/AnthologyUpdateChannel/manifest.json"];
        var stableManifest = JsonSerializer.Deserialize<SignedUpdateManifest>(stableRemote.Content, ManifestJson.Options);
        Assert.NotNull(stableManifest);
        Assert.Equal(
            archiveGoogleMirror.Url,
            Assert.Single(
                Assert.Single(stableManifest.Payload.Packages, item => item.Id == archivePackage.Id).Mirrors,
                item => item.Provider == GoogleDrivePublisher.Provider).Url);
    }

    [Fact]
    public async Task UnpublishCurrentVersionDeletesStableManifestsAndExactVersionFolder()
    {
        const string version = "2.1.301";
        var outputRoot = Path.Combine(_root, "unpublish-output");
        var versionRoot = Path.Combine(outputRoot, version);
        var yandexRoot = Path.Combine(_root, "unpublish-yandex");
        var gameRoot = Path.Combine(_root, "unpublish-game");
        var launcherUpdateRoot = Path.Combine(gameRoot, "AnthologyLauncher", "Update");
        Directory.CreateDirectory(versionRoot);
        Directory.CreateDirectory(Path.Combine(yandexRoot, version));
        Directory.CreateDirectory(Path.Combine(gameRoot, "AnthologyLauncher", "App", "TrustedKeys"));
        Directory.CreateDirectory(launcherUpdateRoot);
        var (privateKeyPath, publicKeyPath) = CreateKeys(Path.Combine(_root, "unpublish-keys"));
        var signedManifest = CreateSignedManifest(privateKeyPath, version);
        await File.WriteAllBytesAsync(Path.Combine(versionRoot, "manifest.json"), signedManifest);
        await File.WriteAllBytesAsync(Path.Combine(outputRoot, "manifest.json"), signedManifest);
        await File.WriteAllBytesAsync(Path.Combine(yandexRoot, version, "manifest.json"), signedManifest);
        await File.WriteAllBytesAsync(Path.Combine(yandexRoot, "manifest.json"), signedManifest);
        await File.WriteAllBytesAsync(Path.Combine(launcherUpdateRoot, "manifest.json"), signedManifest);
        var mirror = new ReleaseMirrorSet { Id = "yandex", Provider = "yandex-disk" };
        var workspace = new ReleaserWorkspace
        {
            Version = version,
            Channel = "next",
            Mirrors = [mirror],
        };
        var machine = CreateMachine(outputRoot, privateKeyPath, publicKeyPath);
        machine.GameSourceRoot = gameRoot;
        machine.PublicationRoots[mirror.Id] = yandexRoot;
        var runner = new InMemoryRcloneRunner();
        runner.Seed(
            "anthology drive:ANTHOLOGY/AnthologyUpdateChannel/manifest.json",
            signedManifest);

        var result = await ReleasePublicationService.UnpublishVersionAsync(
            workspace,
            machine,
            new GoogleDrivePublisher(runner));

        Assert.Equal(2, result.Targets);
        var destructiveCommands = runner.Commands
            .Where(command => command.Arguments[0] is "deletefile" or "purge")
            .ToArray();
        Assert.Equal(2, destructiveCommands.Length);
        Assert.Equal("deletefile", destructiveCommands[0].Arguments[0]);
        Assert.Equal(
            "anthology drive:ANTHOLOGY/AnthologyUpdateChannel/manifest.json",
            destructiveCommands[0].Arguments[1]);
        Assert.Equal("purge", destructiveCommands[1].Arguments[0]);
        Assert.Equal(
            $"anthology drive:ANTHOLOGY/AnthologyUpdateChannel/{version}",
            destructiveCommands[1].Arguments[1]);
        Assert.DoesNotContain(
            destructiveCommands,
            command => command.Arguments[1].Equals(
                "anthology drive:ANTHOLOGY/AnthologyUpdateChannel",
                StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(outputRoot, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(yandexRoot, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(launcherUpdateRoot, "manifest.json")));
        Assert.False(Directory.Exists(versionRoot));
        Assert.False(Directory.Exists(Path.Combine(yandexRoot, version)));
        Assert.DoesNotContain(
            "anthology drive:ANTHOLOGY/AnthologyUpdateChannel/manifest.json",
            runner.Files.Keys,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnpublishOldVersionPreservesCurrentStableManifests()
    {
        const string oldVersion = "2.1.302";
        const string currentVersion = "2.1.303";
        var outputRoot = Path.Combine(_root, "unpublish-old-output");
        var versionRoot = Path.Combine(outputRoot, oldVersion);
        var yandexRoot = Path.Combine(_root, "unpublish-old-yandex");
        var gameRoot = Path.Combine(_root, "unpublish-old-game");
        var launcherUpdateRoot = Path.Combine(gameRoot, "AnthologyLauncher", "Update");
        Directory.CreateDirectory(versionRoot);
        Directory.CreateDirectory(Path.Combine(yandexRoot, oldVersion));
        Directory.CreateDirectory(Path.Combine(gameRoot, "AnthologyLauncher", "App", "TrustedKeys"));
        Directory.CreateDirectory(launcherUpdateRoot);
        var (privateKeyPath, publicKeyPath) = CreateKeys(Path.Combine(_root, "unpublish-old-keys"));
        var oldManifest = CreateSignedManifest(privateKeyPath, oldVersion);
        var currentManifest = CreateSignedManifest(privateKeyPath, currentVersion);
        await File.WriteAllBytesAsync(Path.Combine(versionRoot, "manifest.json"), oldManifest);
        await File.WriteAllBytesAsync(Path.Combine(outputRoot, "manifest.json"), currentManifest);
        await File.WriteAllBytesAsync(Path.Combine(yandexRoot, oldVersion, "manifest.json"), oldManifest);
        await File.WriteAllBytesAsync(Path.Combine(yandexRoot, "manifest.json"), currentManifest);
        await File.WriteAllBytesAsync(Path.Combine(launcherUpdateRoot, "manifest.json"), currentManifest);
        var mirror = new ReleaseMirrorSet { Id = "yandex", Provider = "yandex-disk" };
        var workspace = new ReleaserWorkspace
        {
            Version = oldVersion,
            Channel = "next",
            Mirrors = [mirror],
        };
        var machine = CreateMachine(outputRoot, privateKeyPath, publicKeyPath);
        machine.GameSourceRoot = gameRoot;
        machine.PublicationRoots[mirror.Id] = yandexRoot;
        var runner = new InMemoryRcloneRunner();
        var remoteStablePath = "anthology drive:ANTHOLOGY/AnthologyUpdateChannel/manifest.json";
        runner.Seed(remoteStablePath, currentManifest);
        runner.Seed(
            $"anthology drive:ANTHOLOGY/AnthologyUpdateChannel/{oldVersion}/payload.bin",
            [1, 2, 3]);

        await ReleasePublicationService.UnpublishVersionAsync(
            workspace,
            machine,
            new GoogleDrivePublisher(runner));

        var destructiveCommands = runner.Commands
            .Where(command => command.Arguments[0] is "deletefile" or "purge")
            .ToArray();
        var purge = Assert.Single(destructiveCommands);
        Assert.Equal("purge", purge.Arguments[0]);
        Assert.Equal(
            $"anthology drive:ANTHOLOGY/AnthologyUpdateChannel/{oldVersion}",
            purge.Arguments[1]);
        Assert.True(File.Exists(Path.Combine(outputRoot, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(yandexRoot, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(launcherUpdateRoot, "manifest.json")));
        Assert.True(runner.Files.ContainsKey(remoteStablePath));
        Assert.False(Directory.Exists(versionRoot));
        Assert.False(Directory.Exists(Path.Combine(yandexRoot, oldVersion)));
    }

    [Fact]
    public async Task UnpublishPreflightsEveryStableManifestBeforeDeletingRemoteOrLocalVersion()
    {
        const string version = "2.1.304";
        var outputRoot = Path.Combine(_root, "unpublish-preflight-output");
        var versionRoot = Path.Combine(outputRoot, version);
        var yandexRoot = Path.Combine(_root, "unpublish-preflight-yandex");
        var publishedVersionRoot = Path.Combine(yandexRoot, version);
        Directory.CreateDirectory(versionRoot);
        Directory.CreateDirectory(publishedVersionRoot);
        var (privateKeyPath, publicKeyPath) = CreateKeys(Path.Combine(_root, "unpublish-preflight-keys"));
        var signedManifest = CreateSignedManifest(privateKeyPath, version);
        await File.WriteAllBytesAsync(Path.Combine(versionRoot, "manifest.json"), signedManifest);
        await File.WriteAllBytesAsync(Path.Combine(outputRoot, "manifest.json"), signedManifest);
        await File.WriteAllBytesAsync(Path.Combine(publishedVersionRoot, "manifest.json"), signedManifest);
        await File.WriteAllTextAsync(Path.Combine(yandexRoot, "manifest.json"), "{ invalid-json");
        var mirror = new ReleaseMirrorSet { Id = "yandex", Provider = "yandex-disk" };
        var workspace = new ReleaserWorkspace
        {
            Version = version,
            Channel = "next",
            Mirrors = [mirror],
        };
        var machine = CreateMachine(outputRoot, privateKeyPath, publicKeyPath);
        machine.PublicationRoots[mirror.Id] = yandexRoot;
        var runner = new InMemoryRcloneRunner();
        var remoteStablePath = "anthology drive:ANTHOLOGY/AnthologyUpdateChannel/manifest.json";
        runner.Seed(remoteStablePath, signedManifest);
        runner.Seed(
            $"anthology drive:ANTHOLOGY/AnthologyUpdateChannel/{version}/payload.bin",
            [1, 2, 3]);

        await Assert.ThrowsAnyAsync<InvalidDataException>(() =>
            ReleasePublicationService.UnpublishVersionAsync(
                workspace,
                machine,
                new GoogleDrivePublisher(runner)));

        Assert.DoesNotContain(
            runner.Commands,
            command => command.Arguments[0] is "deletefile" or "purge");
        Assert.True(runner.Files.ContainsKey(remoteStablePath));
        Assert.True(Directory.Exists(versionRoot));
        Assert.True(Directory.Exists(publishedVersionRoot));
        Assert.True(File.Exists(Path.Combine(outputRoot, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(yandexRoot, "manifest.json")));
    }

    [Theory]
    [InlineData(ContentKind.News, true)]
    [InlineData(ContentKind.News, false)]
    [InlineData(ContentKind.Mod, true)]
    [InlineData(ContentKind.Mod, false)]
    public async Task UnpublishLastItemRemovesExactVersionButOnlyMatchingStablePointers(
        ContentKind kind,
        bool stableReferencesRemovedVersion)
    {
        const string removedVersion = "2.1.305";
        const string newerVersion = "2.1.306";
        const string contentId = "last-item";
        var scenario = $"unpublish-last-{kind}-{stableReferencesRemovedVersion}";
        var outputRoot = Path.Combine(_root, scenario, "output");
        var versionRoot = Path.Combine(outputRoot, removedVersion);
        var yandexRoot = Path.Combine(_root, scenario, "yandex");
        var publishedVersionRoot = Path.Combine(yandexRoot, removedVersion);
        var gameRoot = Path.Combine(_root, scenario, "game");
        var launcherUpdateRoot = Path.Combine(gameRoot, "AnthologyLauncher", "Update");
        Directory.CreateDirectory(versionRoot);
        Directory.CreateDirectory(publishedVersionRoot);
        Directory.CreateDirectory(Path.Combine(gameRoot, "AnthologyLauncher", "App", "TrustedKeys"));
        Directory.CreateDirectory(launcherUpdateRoot);
        var (privateKeyPath, publicKeyPath) = CreateKeys(Path.Combine(_root, scenario, "keys"));
        var removedManifest = CreateSignedManifest(privateKeyPath, removedVersion, kind, contentId);
        var stableManifest = stableReferencesRemovedVersion
            ? removedManifest
            : CreateSignedManifest(privateKeyPath, newerVersion);
        await File.WriteAllBytesAsync(Path.Combine(versionRoot, "manifest.json"), removedManifest);
        await File.WriteAllTextAsync(Path.Combine(versionRoot, "payload.bin"), "exact local payload");
        await File.WriteAllBytesAsync(Path.Combine(outputRoot, "manifest.json"), stableManifest);
        await File.WriteAllBytesAsync(Path.Combine(publishedVersionRoot, "manifest.json"), removedManifest);
        await File.WriteAllTextAsync(Path.Combine(publishedVersionRoot, "payload.bin"), "exact mirror payload");
        await File.WriteAllBytesAsync(Path.Combine(yandexRoot, "manifest.json"), stableManifest);
        await File.WriteAllBytesAsync(Path.Combine(launcherUpdateRoot, "manifest.json"), stableManifest);

        var item = new ContentDraft
        {
            Id = contentId,
            Kind = kind,
            Title = "Last item",
            IsPublished = true,
        };
        var mirror = new ReleaseMirrorSet { Id = "yandex", Provider = "yandex-disk" };
        var workspace = new ReleaserWorkspace
        {
            Version = removedVersion,
            Channel = "next",
            Mirrors = [mirror],
            Content = [item],
        };
        var machine = CreateMachine(outputRoot, privateKeyPath, publicKeyPath);
        machine.GameSourceRoot = gameRoot;
        machine.PublicationRoots[mirror.Id] = yandexRoot;
        var runner = new InMemoryRcloneRunner();
        var remoteStablePath = "anthology drive:ANTHOLOGY/AnthologyUpdateChannel/manifest.json";
        runner.Seed(remoteStablePath, stableManifest);
        runner.Seed(
            $"anthology drive:ANTHOLOGY/AnthologyUpdateChannel/{removedVersion}/payload.bin",
            [1, 2, 3]);
        var publisher = new GoogleDrivePublisher(runner);

        if (kind == ContentKind.Mod)
        {
            await ReleasePublicationService.UnpublishAddonAsync(item, workspace, machine, publisher);
        }
        else
        {
            await ReleasePublicationService.UnpublishContentAsync(item, workspace, machine, publisher);
        }

        Assert.False(item.IsPublished);
        Assert.False(Directory.Exists(versionRoot));
        Assert.False(Directory.Exists(publishedVersionRoot));
        Assert.DoesNotContain(
            runner.Files.Keys,
            path => path.StartsWith(
                $"anthology drive:ANTHOLOGY/AnthologyUpdateChannel/{removedVersion}/",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            !stableReferencesRemovedVersion,
            File.Exists(Path.Combine(outputRoot, "manifest.json")));
        Assert.Equal(
            !stableReferencesRemovedVersion,
            File.Exists(Path.Combine(yandexRoot, "manifest.json")));
        Assert.Equal(
            !stableReferencesRemovedVersion,
            File.Exists(Path.Combine(launcherUpdateRoot, "manifest.json")));
        Assert.Equal(
            !stableReferencesRemovedVersion,
            runner.Files.ContainsKey(remoteStablePath));

        var destructiveCommands = runner.Commands
            .Where(command => command.Arguments[0] is "deletefile" or "purge")
            .ToArray();
        Assert.Equal(stableReferencesRemovedVersion ? 2 : 1, destructiveCommands.Length);
        Assert.Equal("purge", destructiveCommands[^1].Arguments[0]);
        Assert.Equal(
            $"anthology drive:ANTHOLOGY/AnthologyUpdateChannel/{removedVersion}",
            destructiveCommands[^1].Arguments[1]);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private ReleaserMachineSettings CreateMachine(
        string outputRoot,
        string privateKeyPath,
        string publicKeyPath)
    {
        var toolsRoot = Path.Combine(_root, $"tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(toolsRoot);
        var rclonePath = Path.Combine(toolsRoot, "rclone.exe");
        var configPath = Path.Combine(toolsRoot, "rclone.conf");
        File.WriteAllText(rclonePath, "fake");
        File.WriteAllText(configPath, "[anthology drive]");
        return new ReleaserMachineSettings
        {
            OutputRoot = outputRoot,
            PrivateKeyPath = privateKeyPath,
            PublicKeyPath = publicKeyPath,
            KeyId = "google-publication-test",
            GoogleDriveRclonePath = rclonePath,
            GoogleDriveRcloneConfigPath = configPath,
            GoogleDriveRemoteName = "anthology drive",
            GoogleDriveProjectPath = "ANTHOLOGY",
            GoogleDriveReleasePath = "AnthologyUpdateChannel",
            GoogleDriveManifestPath = "AnthologyUpdateChannel/manifest.json",
            GoogleDriveMirrorPriority = 30,
        };
    }

    private static (string PrivateKeyPath, string PublicKeyPath) CreateKeys(string root)
    {
        Directory.CreateDirectory(root);
        var privateKeyPath = Path.Combine(root, "private.pem");
        var publicKeyPath = Path.Combine(root, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKeyPath, publicKeyPath);
        return (privateKeyPath, publicKeyPath);
    }

    private static byte[] CreateSignedManifest(
        string privateKeyPath,
        string version,
        ContentKind kind = ContentKind.News,
        string contentId = "release-news")
    {
        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(File.ReadAllText(privateKeyPath));
        var content = new ContentCatalog(
            4,
            version,
            DateTimeOffset.UtcNow,
            [new ContentDocument(contentId, kind, kind == ContentKind.Mod ? "mods" : "news", "Release", string.Empty, string.Empty, [], [])]);
        var signed = ManifestSecurity.Sign(
            new UpdateManifest(
                4,
                "next",
                version,
                DateTimeOffset.UtcNow,
                null,
                [],
                content),
            privateKey,
            "google-publication-test");
        return JsonSerializer.SerializeToUtf8Bytes(signed, ManifestJson.Options);
    }

    private sealed class InMemoryRcloneRunner : IRcloneCommandRunner
    {
        private int _nextId;

        public List<RcloneCommand> Commands { get; } = [];

        public Dictionary<string, RemoteContent> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Seed(string remotePath, byte[] content)
        {
            Files[remotePath] = new RemoteContent(NextId(), content);
        }

        public Task<RcloneCommandResult> RunAsync(
            RcloneCommand command,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            var operation = command.Arguments[0];
            switch (operation)
            {
                case "mkdir":
                    return Task.FromResult(Success());
                case "link":
                    return Task.FromResult(Success(
                        "https://drive.google.com/drive/folders/projectFolder123?usp=sharing"));
                case "copyto":
                {
                    var content = File.ReadAllBytes(command.Arguments[1]);
                    Files[command.Arguments[2]] = new RemoteContent(NextId(), content);
                    return Task.FromResult(Success());
                }
                case "lsjson":
                {
                    var parent = command.Arguments[1].TrimEnd('/');
                    var prefix = parent + "/";
                    var items = Files
                        .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .Where(pair => !pair.Key[prefix.Length..].Contains('/'))
                        .Select(pair => new
                        {
                            Path = pair.Key[prefix.Length..],
                            Name = pair.Key[prefix.Length..],
                            Size = pair.Value.Content.LongLength,
                            IsDir = false,
                            ID = pair.Value.Id,
                        })
                        .ToArray();
                    return Task.FromResult(Success(JsonSerializer.Serialize(items)));
                }
                case "cat":
                    return Task.FromResult(Files.TryGetValue(command.Arguments[1], out var remote)
                        ? Success(Encoding.UTF8.GetString(remote.Content))
                        : new RcloneCommandResult(1, string.Empty, "not found"));
                case "deletefile":
                    Files.Remove(command.Arguments[1]);
                    return Task.FromResult(Success());
                case "purge":
                {
                    var prefix = command.Arguments[1].TrimEnd('/') + "/";
                    foreach (var path in Files.Keys
                                 .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                 .ToArray())
                    {
                        Files.Remove(path);
                    }
                    return Task.FromResult(Success());
                }
                default:
                    throw new InvalidOperationException($"Unexpected fake rclone operation: {operation}");
            }
        }

        private string NextId() => $"googleFile{++_nextId:D8}";

        private static RcloneCommandResult Success(string output = "") => new(0, output, string.Empty);
    }

    private sealed record RemoteContent(string Id, byte[] Content);
}
