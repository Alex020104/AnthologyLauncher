using System.Text.Json;
using Anthology.Releaser.Core;

namespace Anthology.Update.Core.Tests;

public sealed class GoogleDrivePublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"anthology-google-drive-tests-{Guid.NewGuid():N}");

    public GoogleDrivePublisherTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task EnsureProjectUsesArgumentListAndNeverUsesAccountHomeAsMirror()
    {
        var machine = CreateMachine();
        machine.GoogleDriveProjectPath = "Проекты/ANTHOLOGY 2.1";
        machine.GoogleDriveAccountUrl = GoogleDrivePublisher.AccountHomeUrl;
        var runner = new FakeRcloneRunner(command =>
            command.Arguments[0] == "link"
                ? Success("https://drive.google.com/drive/folders/projectFolder123?usp=sharing")
                : Success());

        var result = await new GoogleDrivePublisher(runner).EnsureProjectAsync(machine);

        Assert.Equal("anthology drive:Проекты/ANTHOLOGY 2.1", result.RemoteTarget);
        Assert.Equal(result.PublicUrl, machine.GoogleDriveProjectPublicUrl);
        Assert.Equal(2, runner.Commands.Count);
        Assert.Equal(
            ["mkdir", "anthology drive:Проекты/ANTHOLOGY 2.1", "--config", machine.GoogleDriveRcloneConfigPath],
            runner.Commands[0].Arguments);
        Assert.DoesNotContain(
            runner.Commands.SelectMany(command => command.Arguments),
            argument => argument.Contains("/drive/home", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListRemoteNamesUsesRcloneOutputWithoutReadingConfigContents()
    {
        var machine = CreateMachine();
        machine.GoogleDriveRemoteName = string.Empty;
        var runner = new FakeRcloneRunner(_ => Success("anthology drive:\r\nbackup:\r\nanthology drive:\r\n"));

        var names = await new GoogleDrivePublisher(runner).ListRemoteNamesAsync(machine);

        Assert.Equal(["anthology drive", "backup"], names);
        var command = Assert.Single(runner.Commands);
        Assert.Equal(
            ["listremotes", "--config", machine.GoogleDriveRcloneConfigPath],
            command.Arguments);
    }

    [Fact]
    public void IsConfiguredUsesOnlyLocalValidation()
    {
        var machine = CreateMachine();

        Assert.True(GoogleDrivePublisher.IsConfigured(machine));
        machine.GoogleDriveRemoteName = string.Empty;
        Assert.False(GoogleDrivePublisher.IsConfigured(machine));
        machine.GoogleDriveRemoteName = "anthology drive";
        machine.GoogleDriveRcloneConfigPath = Path.Combine(_root, "missing.conf");
        Assert.False(GoogleDrivePublisher.IsConfigured(machine));
    }

    [Fact]
    public async Task DeleteReleaseVersionPurgesOnlyOneExactVersionChild()
    {
        var machine = CreateMachine();
        var runner = new FakeRcloneRunner(_ => Success());
        var publisher = new GoogleDrivePublisher(runner);

        await publisher.DeleteReleaseVersionAsync(machine, "2.1.200-beta+1");

        var command = Assert.Single(runner.Commands);
        Assert.Equal("purge", command.Arguments[0]);
        Assert.Equal(
            "anthology drive:ANTHOLOGY/AnthologyUpdateChannel/2.1.200-beta+1",
            command.Arguments[1]);
        foreach (var unsafeVersion in new[] { "", ".", "..", "2.1/old", "2.1\\old", "../2.1" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                publisher.DeleteReleaseVersionAsync(machine, unsafeVersion));
        }
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task SyncSourcesStreamsExistingRootsAndDeletesOnlyInsideDedicatedRemoteChildren()
    {
        var machine = CreateMachine();
        machine.GoogleDriveGamePath = "Game Files";
        machine.GoogleDriveMo2Path = "MO2 Files";
        var runner = new FakeRcloneRunner(_ => Success());

        var result = await new GoogleDrivePublisher(runner).SyncSourcesAsync(machine);

        Assert.Equal(
            ["anthology drive:ANTHOLOGY/Game Files", "anthology drive:ANTHOLOGY/MO2 Files"],
            result.RemoteTargets);
        Assert.Equal(2, runner.Commands.Count);
        Assert.All(runner.Commands, command => Assert.Equal("sync", command.Arguments[0]));
        Assert.Equal(Path.GetFullPath(machine.GameSourceRoot), runner.Commands[0].Arguments[1]);
        Assert.Equal(Path.GetFullPath(machine.Mo2SourceRoot), runner.Commands[1].Arguments[1]);
        Assert.Contains("--delete-after", runner.Commands[0].Arguments);
        Assert.Contains("--drive-chunk-size", runner.Commands[0].Arguments);
        Assert.Contains("/AnomalyLauncher.cfg", runner.Commands[0].Arguments);
        Assert.Contains("/commandline.txt", runner.Commands[0].Arguments);
        Assert.Contains("/appdata/**", runner.Commands[0].Arguments);
        Assert.Contains("/ModOrganizer.ini", runner.Commands[1].Arguments);
        Assert.Contains("/downloads/**", runner.Commands[1].Arguments);
        Assert.DoesNotContain(
            runner.Commands.SelectMany(command => command.Arguments),
            argument => argument.Contains("GoogleDrive", StringComparison.OrdinalIgnoreCase)
                        && Path.IsPathFullyQualified(argument));
    }

    [Fact]
    public async Task ExactLooseOverridesUseRemoteIdsAndMatchManagedSourceFiles()
    {
        var machine = CreateMachine();
        machine.GoogleDriveGamePath = "game";
        machine.GoogleDriveMo2Path = "mo2";
        WriteSizedFile(Path.Combine(machine.GameSourceRoot, "bin", "xrEngine.exe"), 4);
        WriteSizedFile(Path.Combine(machine.GameSourceRoot, "appdata", "user.ltx"), 9);
        WriteSizedFile(Path.Combine(machine.Mo2SourceRoot, "ModOrganizer.exe"), 5);
        WriteSizedFile(Path.Combine(machine.Mo2SourceRoot, "mods", "My Mod", "gamedata", "file.ltx"), 7);

        var runner = new FakeRcloneRunner(command =>
        {
            if (command.Arguments[0] != "lsjson")
            {
                return Success();
            }
            return command.Arguments[1].EndsWith("/game", StringComparison.Ordinal)
                ? Success(Json(
                    Item("bin/xrEngine.exe", 4, "gameFile123"),
                    Item("appdata/user.ltx", 9, "ignoredFile123")))
                : Success(Json(
                    Item("ModOrganizer.exe", 5, "mo2File123"),
                    Item("mods/My Mod/gamedata/file.ltx", 7, "modFile123")));
        });

        var overrides = await new GoogleDrivePublisher(runner)
            .BuildLooseFileMirrorOverridesAsync(machine);

        Assert.Equal(3, overrides.Count);
        Assert.DoesNotContain(overrides, item => item.Path.StartsWith("appdata/", StringComparison.OrdinalIgnoreCase));
        var game = Assert.Single(overrides, item => item.PackageId == GoogleDrivePublisher.GamePackageId);
        Assert.Equal("bin/xrEngine.exe", game.Path);
        Assert.Equal("google-drive", Assert.Single(game.Mirrors).Provider);
        Assert.Equal(30, Assert.Single(game.Mirrors).Priority);
        var resolved = WebShareMirrorResolver.ResolveShareUrl(Assert.Single(game.Mirrors).Url);
        Assert.Equal("drive.usercontent.google.com", resolved.Host);
        Assert.Contains("gameFile123", resolved.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadAndDeleteUseExactRemoteFileWithoutSizeLimitOrStaging()
    {
        var machine = CreateMachine();
        var localFile = Path.Combine(_root, "release source", "anthology-game.bin");
        WriteSizedFile(localFile, 32);
        var runner = new FakeRcloneRunner(command => command.Arguments[0] switch
        {
            "lsjson" => Success(Json(Item("anthology-game.bin", 32, "releaseFile123"))),
            _ => Success(),
        });
        var publisher = new GoogleDrivePublisher(runner);

        var uploaded = await publisher.UploadFileAsync(
            machine,
            localFile,
            "releases/2.1.200/anthology-game.bin");
        await publisher.DeleteFileAsync(
            machine,
            "releases/2.1.200/anthology-game.bin");

        var upload = runner.Commands[0];
        Assert.Equal("copyto", upload.Arguments[0]);
        Assert.Equal(Path.GetFullPath(localFile), upload.Arguments[1]);
        Assert.Equal(
            "anthology drive:ANTHOLOGY/releases/2.1.200/anthology-game.bin",
            upload.Arguments[2]);
        Assert.DoesNotContain("--max-size", upload.Arguments);
        Assert.Equal("releaseFile123", uploaded.Id);
        var delete = runner.Commands[^1];
        Assert.Equal("deletefile", delete.Arguments[0]);
        Assert.Equal(upload.Arguments[2], delete.Arguments[1]);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            publisher.DeleteFileAsync(machine, "../manifest.json"));
        Assert.Equal(3, runner.Commands.Count);
    }

    [Fact]
    public async Task ManifestDiscoveryReturnsStableIdLinkAndNotAccountHome()
    {
        var machine = CreateMachine();
        machine.GoogleDriveManifestPath = "AnthologyUpdateChannel/manifest.json";
        var runner = new FakeRcloneRunner(_ =>
            Success(Json(Item("manifest.json", 123, "manifestFile123"))));

        var manifest = await new GoogleDrivePublisher(runner).DiscoverManifestAsync(machine);

        Assert.NotNull(manifest);
        Assert.Equal("AnthologyUpdateChannel/manifest.json", manifest.Path);
        Assert.Equal("manifestFile123", manifest.Id);
        Assert.Equal(
            "https://drive.google.com/file/d/manifestFile123/view?usp=sharing",
            manifest.ShareUrl);
        Assert.False(GoogleDrivePublisher.IsAccountHomeUrl(manifest.ShareUrl));
        Assert.True(GoogleDrivePublisher.IsAccountHomeUrl(machine.GoogleDriveAccountUrl));
    }

    [Fact]
    public void DedicatedStableChannelKeepsVersionArtifactsAtRootAndMovesOnlyStableManifest()
    {
        var machine = CreateMachine();
        machine.GoogleDriveManifestPath = "AnthologyUpdateChannel/manifest.json";
        var workspace = new ReleaserWorkspace
        {
            StableChannelDirectory = "modern",
        };

        Assert.Equal(
            "AnthologyUpdateChannel/modern/manifest.json",
            GoogleDrivePublisher.ResolveStableManifestRelativePath(machine, workspace));
        Assert.Equal(
            "AnthologyUpdateChannel/manifest.json",
            GoogleDrivePublisher.ResolveStableManifestRelativePath(machine));
        Assert.Equal("modern/manifest.json", ReleaseChannelLayout.GetStableManifestRelativePath(workspace));
        Assert.Equal("modern/history.json", ReleaseChannelLayout.GetStableHistoryRelativePath(workspace));
        Assert.Throws<ArgumentException>(() =>
            ReleaseChannelLayout.GetStableManifestRelativePath(new ReleaserWorkspace
            {
                StableChannelDirectory = "../escape",
            }));
    }

    [Theory]
    [InlineData("AnthologyUpdateChannel/modern/manifest.json")]
    [InlineData("another-channel/manifest.json")]
    public void DedicatedStableChannelRejectsNonLegacyGoogleManifestLocation(string configuredPath)
    {
        var machine = CreateMachine();
        machine.GoogleDriveManifestPath = configuredPath;
        var workspace = new ReleaserWorkspace
        {
            StableChannelDirectory = "modern",
        };

        var exception = Assert.Throws<InvalidDataException>(() =>
            GoogleDrivePublisher.ResolveStableManifestRelativePath(machine, workspace));

        Assert.Contains("legacy root manifest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsAccountHomeAsProjectLinkAndHonorsCancellation()
    {
        var machine = CreateMachine();
        var homeRunner = new FakeRcloneRunner(command =>
            command.Arguments[0] == "link"
                ? Success(GoogleDrivePublisher.AccountHomeUrl)
                : Success());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GoogleDrivePublisher(homeRunner).EnsureProjectAsync(machine));

        var cancelledRunner = new FakeRcloneRunner(_ => Success());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new GoogleDrivePublisher(cancelledRunner).SyncSourcesAsync(
                machine,
                cancellationToken: cancellation.Token));
        Assert.Empty(cancelledRunner.Commands);
    }

    [Fact]
    public async Task DestructiveSyncIsConfinedToDisjointConfiguredRoots()
    {
        var machine = CreateMachine();
        machine.GoogleDriveGamePath = "managed/game";
        machine.GoogleDriveMo2Path = "managed/game/mo2";
        var runner = new FakeRcloneRunner(_ => Success());
        var publisher = new GoogleDrivePublisher(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.SyncSourcesAsync(machine));
        Assert.Empty(runner.Commands);

        machine.GoogleDriveGamePath = "game";
        machine.GoogleDriveMo2Path = "mo2";
        machine.GoogleDriveReleasePath = "releases";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.SyncDirectoryAsync(machine, machine.GameSourceRoot, "unmanaged"));
        Assert.Empty(runner.Commands);

        var result = await publisher.SyncReleaseDirectoryAsync(machine, machine.GameSourceRoot);
        Assert.Equal("anthology drive:ANTHOLOGY/releases", Assert.Single(result.RemoteTargets));
        Assert.Equal("sync", Assert.Single(runner.Commands).Arguments[0]);
    }

    [Fact]
    public async Task ToolDiagnosticsRedactConfigAndCredentials()
    {
        var machine = CreateMachine();
        const string accessToken = "accessSecret123";
        const string bearerToken = "bearerSecret456";
        var leak = $"config={machine.GoogleDriveRcloneConfigPath} access_token={accessToken} Bearer {bearerToken}";
        var runner = new FakeRcloneRunner(
            _ => new RcloneCommandResult(1, string.Empty, leak),
            leak);
        var messages = new RecordingProgress();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GoogleDrivePublisher(runner).SyncSourcesAsync(machine, messages));

        var diagnostics = string.Join(Environment.NewLine, messages.Messages.Append(exception.Message));
        Assert.DoesNotContain(machine.GoogleDriveRcloneConfigPath, diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(accessToken, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(bearerToken, diagnostics, StringComparison.Ordinal);
        Assert.Contains("<rclone-config>", diagnostics, StringComparison.Ordinal);
        Assert.Contains("<redacted>", diagnostics, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("remote:name")]
    [InlineData("-remote")]
    [InlineData("remote/name")]
    public async Task RejectsUnsafeRemoteNames(string remoteName)
    {
        var machine = CreateMachine();
        machine.GoogleDriveRemoteName = remoteName;
        var runner = new FakeRcloneRunner(_ => Success());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new GoogleDrivePublisher(runner).EnsureProjectAsync(machine));

        Assert.Empty(runner.Commands);
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

    private ReleaserMachineSettings CreateMachine()
    {
        var tools = Path.Combine(_root, "tools");
        var game = Path.Combine(_root, "game source");
        var mo2 = Path.Combine(_root, "mo2 source");
        Directory.CreateDirectory(tools);
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(mo2);
        var rclone = Path.Combine(tools, "rclone.exe");
        var config = Path.Combine(tools, "rclone.conf");
        File.WriteAllText(rclone, "fake");
        File.WriteAllText(config, "[anthology drive]");
        return new ReleaserMachineSettings
        {
            GameSourceRoot = game,
            Mo2SourceRoot = mo2,
            GoogleDriveRclonePath = rclone,
            GoogleDriveRcloneConfigPath = config,
            GoogleDriveRemoteName = "anthology drive",
            GoogleDriveProjectPath = "ANTHOLOGY",
            GoogleDriveGamePath = "game",
            GoogleDriveMo2Path = "mo2",
            GoogleDriveManifestPath = "AnthologyUpdateChannel/manifest.json",
            GoogleDriveMirrorPriority = 30,
        };
    }

    private static void WriteSizedFile(string path, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', size).ToArray());
    }

    private static object Item(string path, long size, string id) => new
    {
        Path = path,
        Name = Path.GetFileName(path),
        Size = size,
        IsDir = false,
        ID = id,
    };

    private static string Json(params object[] items) => JsonSerializer.Serialize(items);

    private static RcloneCommandResult Success(string output = "") => new(0, output, string.Empty);

    private sealed class FakeRcloneRunner(
        Func<RcloneCommand, RcloneCommandResult> handler,
        string? progressLine = null) : IRcloneCommandRunner
    {
        public List<RcloneCommand> Commands { get; } = [];

        public Task<RcloneCommandResult> RunAsync(
            RcloneCommand command,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (progressLine is not null)
            {
                progress?.Report(progressLine);
            }
            return Task.FromResult(handler(command));
        }
    }

    private sealed class RecordingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }
}
