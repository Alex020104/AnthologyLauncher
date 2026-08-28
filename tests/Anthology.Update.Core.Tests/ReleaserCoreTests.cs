using System.Text.Json;
using Anthology.Contracts;
using Anthology.Releaser.Core;

namespace Anthology.Update.Core.Tests;

public sealed class ReleaserCoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anthology-releaser-{Guid.NewGuid():N}");

    [Fact]
    public async Task UnifiedBuilderCreatesOneVersionForGameMo2AndContent()
    {
        var game = Path.Combine(_root, "game");
        var mo2 = Path.Combine(_root, "mo2");
        var output = Path.Combine(_root, "output");
        var keys = Path.Combine(_root, "keys");
        Directory.CreateDirectory(Path.Combine(game, "bin"));
        Directory.CreateDirectory(Path.Combine(game, "appdata"));
        Directory.CreateDirectory(Path.Combine(mo2, "mods", "example"));
        Directory.CreateDirectory(Path.Combine(mo2, "overwrite"));
        await File.WriteAllTextAsync(Path.Combine(game, "fsgame.ltx"), "game");
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "Anomaly.exe"), "binary");
        await File.WriteAllTextAsync(Path.Combine(game, "appdata", "user.ltx"), "private");
        await File.WriteAllTextAsync(Path.Combine(mo2, "ModOrganizer.exe"), "mo2");
        await File.WriteAllTextAsync(Path.Combine(mo2, "mods", "example", "mod.txt"), "mod");
        await File.WriteAllTextAsync(Path.Combine(mo2, "overwrite", "transient.txt"), "transient");
        Directory.CreateDirectory(keys);
        var privateKey = Path.Combine(keys, "private.pem");
        var publicKey = Path.Combine(keys, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKey, publicKey);
        var workspace = new ReleaserWorkspace
        {
            Version = "2.1.131",
            Content =
            [
                new ContentDraft
                {
                    Id = "news-131",
                    Kind = ContentKind.News,
                    Title = "Версия 2.1.131",
                    Summary = "Тест",
                    Body = "Полный текст",
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
            KeyId = "test-01",
        };

        var result = await UnifiedReleaseBuilder.BuildAsync(new UnifiedReleaseRequest(workspace, machine));

        Assert.Equal(2, result.Artifacts.Count);
        Assert.Equal(1, result.ContentItems);
        await using var stream = File.OpenRead(result.ManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options);
        Assert.NotNull(manifest);
        Assert.Equal(2, manifest.Payload.SchemaVersion);
        Assert.All(manifest.Payload.Packages, package => Assert.Equal(PackageUpdateMode.ManagedExact, package.UpdateMode));
        Assert.All(manifest.Payload.Packages, package => Assert.True(package.PruneInstallRoot));
        Assert.DoesNotContain(manifest.Payload.Packages.Single(package => package.InstallRoot == "game").Files, file => file.StartsWith("appdata/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(manifest.Payload.Packages.Single(package => package.InstallRoot == "modpack").Files, file => file.StartsWith("overwrite/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SharedWorkspaceSynchronizesWithoutMachineSecrets()
    {
        var shared = Path.Combine(_root, "shared");
        var local = new ReleaserWorkspace { Revision = 3, UpdatedBy = "Первый", Version = "2.1.133" };

        var published = await WorkspaceSyncService.SyncAsync(local, shared, null);
        var received = await WorkspaceSyncService.SyncAsync(new ReleaserWorkspace { Revision = 1 }, shared, null);

        Assert.Equal(WorkspaceSyncDirection.Published, published.Direction);
        Assert.Equal(WorkspaceSyncDirection.Received, received.Direction);
        Assert.Equal("2.1.133", received.Workspace.Version);
        Assert.True(File.Exists(Path.Combine(shared, WorkspaceSyncService.SharedFileName)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
