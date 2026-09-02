using Anthology.Releaser.Core;
using Anthology.Update.Core;

namespace Anthology.Update.Core.Tests;

public sealed class QuickReleaseDestinationMapperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "anthology-quick-path-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void SelectedAddonFolderKeepsTheModsDirectoryInItsDestination()
    {
        var mo2Root = CreateMo2Root();
        var addon = CreateDirectory(mo2Root, "mods", "Example Addon");

        var destination = QuickReleaseDestinationMapper.CreateFolderDestination(
            "modpack",
            mo2Root,
            addon);

        Assert.Equal("mods/Example Addon", destination);
    }

    [Fact]
    public void ModsRootCanBeConfiguredWithoutLosingTheModsPrefix()
    {
        var mo2Root = CreateMo2Root();
        var modsRoot = Path.Combine(mo2Root, "mods");
        var addon = CreateDirectory(modsRoot, "Example Addon");

        var destination = QuickReleaseDestinationMapper.CreateFolderDestination(
            "modpack",
            modsRoot,
            addon);

        Assert.Equal("mods/Example Addon", destination);
    }

    [Fact]
    public void LegacyAddonDestinationIsRepairedButAlreadyPrefixedPathIsNotDoubled()
    {
        var mo2Root = CreateMo2Root();
        var addon = CreateDirectory(mo2Root, "mods", "Example Addon");

        var repaired = QuickReleaseDestinationMapper.NormalizeFolderDestination(
            "modpack",
            mo2Root,
            addon,
            "Example Addon");
        var alreadyCorrect = QuickReleaseDestinationMapper.NormalizeFolderDestination(
            "modpack",
            mo2Root,
            addon,
            "mods/Example Addon");

        Assert.Equal("mods/Example Addon", repaired);
        Assert.Equal("mods/Example Addon", alreadyCorrect);
    }

    [Fact]
    public void ExplicitMo2DestinationOutsideModsIsPreserved()
    {
        var mo2Root = CreateMo2Root();
        var addon = CreateDirectory(mo2Root, "mods", "Example Addon");

        var destination = QuickReleaseDestinationMapper.NormalizeFolderDestination(
            "modpack",
            mo2Root,
            addon,
            "tools/ManuallyMappedAddon");

        Assert.Equal("tools/ManuallyMappedAddon", destination);
    }

    [Fact]
    public void SelectedFileInsideAnAddonGetsItsCompleteModsPath()
    {
        var mo2Root = CreateMo2Root();
        var configs = CreateDirectory(mo2Root, "mods", "Example Addon", "gamedata", "configs");
        var source = Path.Combine(configs, "example.ltx");
        File.WriteAllText(source, "enabled = true");

        var destination = QuickReleaseDestinationMapper.CreateFileDestination(
            "modpack",
            mo2Root,
            source);

        Assert.Equal("mods/Example Addon/gamedata/configs/example.ltx", destination);
    }

    [Fact]
    public async Task QuickReleaseIsAppliedInsidePlayerMo2ModsInsteadOfBesideModOrganizer()
    {
        var authorMo2 = CreateMo2Root("AuthorMo2");
        var playerMo2 = CreateMo2Root("PlayerMo2");
        var addon = CreateDirectory(authorMo2, "mods", "Example Addon", "gamedata", "configs");
        var sourceFile = Path.Combine(addon, "example.ltx");
        await File.WriteAllTextAsync(sourceFile, "enabled = true");
        var keys = CreateDirectory(_root, "keys");
        var privateKey = Path.Combine(keys, "private.pem");
        var publicKey = Path.Combine(keys, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKey, publicKey);
        var workspace = new ReleaserWorkspace
        {
            Version = "2.1.200",
            Channel = "next",
        };
        var machine = new ReleaserMachineSettings
        {
            Mo2SourceRoot = authorMo2,
            OutputRoot = CreateDirectory(_root, "output"),
            PrivateKeyPath = privateKey,
            PublicKeyPath = publicKey,
            KeyId = "quick-mo2-path-test",
            QuickReleaseFolders =
            [
                new QuickReleaseFolderDraft
                {
                    SourcePath = Path.Combine(authorMo2, "mods", "Example Addon"),
                    InstallRoot = "modpack",
                    // This is the faulty destination produced by the old releaser.
                    RelativePath = "Example Addon",
                },
            ],
        };

        var release = await ReleasePublicationService.PublishQuickFilesAsync(workspace, machine);
        var stateRoot = CreateDirectory(_root, "updater-state");
        var coordinator = new UpdateCoordinator(new HttpClient());
        var check = await coordinator.CheckAsync(
            release.ManifestPath,
            publicKey,
            "next",
            stateRoot);
        await coordinator.ApplyAsync(
            check,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["game"] = CreateDirectory(_root, "PlayerGame"),
                ["modpack"] = playerMo2,
            },
            stateRoot);

        var installed = Path.Combine(
            playerMo2,
            "mods",
            "Example Addon",
            "gamedata",
            "configs",
            "example.ltx");
        Assert.True(File.Exists(installed));
        Assert.False(File.Exists(Path.Combine(
            playerMo2,
            "Example Addon",
            "gamedata",
            "configs",
            "example.ltx")));
    }

    private string CreateMo2Root(string name = "ModOrganizer")
    {
        var root = CreateDirectory(_root, name);
        File.WriteAllText(Path.Combine(root, "ModOrganizer.exe"), string.Empty);
        return root;
    }

    private static string CreateDirectory(params string[] segments)
    {
        var path = Path.Combine(segments);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
