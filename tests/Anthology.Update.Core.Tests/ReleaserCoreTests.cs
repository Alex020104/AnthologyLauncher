using System.Text.Json;
using System.Security.Cryptography;
using System.IO.Compression;
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
                    TitleEn = "Version 2.1.131",
                    SummaryEn = "Test",
                    BodyEn = "Full text",
                    TitleDe = "Version 2.1.131",
                    SummaryDe = "Test",
                    BodyDe = "Vollständiger Text",
                    IsPublished = true,
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
        Assert.Equal(4, manifest.Payload.SchemaVersion);
        Assert.All(manifest.Payload.Packages, package => Assert.Equal(PackageUpdateMode.ManagedExact, package.UpdateMode));
        Assert.All(manifest.Payload.Packages, package => Assert.True(package.PruneInstallRoot));
        var localizedNews = Assert.Single(manifest.Payload.Content!.Items);
        Assert.Equal(4, manifest.Payload.Content.SchemaVersion);
        Assert.Equal("Full text", ContentLocalization.Resolve(localizedNews, "en").Body);
        Assert.Equal("Vollständiger Text", ContentLocalization.Resolve(localizedNews, "de").Body);
        Assert.Equal("Полный текст", ContentLocalization.Resolve(localizedNews, "fr").Body);
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

    [Fact]
    public async Task AddonCanBePublishedAndRemovedWithoutRebuildingTheGame()
    {
        var output = Path.Combine(_root, "addon-output");
        var published = Path.Combine(_root, "published");
        var keys = Path.Combine(_root, "addon-keys");
        var source = Path.Combine(_root, "optional-addon.zip");
        Directory.CreateDirectory(keys);
        await File.WriteAllTextAsync(source, "addon archive payload");
        var privateKey = Path.Combine(keys, "private.pem");
        var publicKey = Path.Combine(keys, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKey, publicKey);
        var mirror = new ReleaseMirrorSet
        {
            Id = "cdn-test",
            Provider = "http",
            ContentUrl = "https://cdn.example/{version}/addons/{id}/{file}",
            Priority = 10,
        };
        var addon = new ContentDraft
        {
            Id = "optional-addon",
            Kind = ContentKind.Mod,
            Section = "dev",
            Title = "Optional Addon",
        };
        var workspace = new ReleaserWorkspace
        {
            Version = "2.1.132",
            Mirrors = [mirror],
            Content = [addon],
        };
        var machine = new ReleaserMachineSettings
        {
            OutputRoot = output,
            PrivateKeyPath = privateKey,
            PublicKeyPath = publicKey,
            KeyId = "addon-test-key",
            ContentArchivePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [addon.Id] = source,
            },
            PublicationRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [mirror.Id] = published,
            },
        };

        var result = await ReleasePublicationService.PublishAddonAsync(addon, workspace, machine);

        var relativeArtifact = Path.Combine(workspace.Version, "addons", addon.Id, Path.GetFileName(source));
        Assert.True(File.Exists(Path.Combine(output, relativeArtifact)));
        Assert.True(File.Exists(Path.Combine(published, relativeArtifact)));
        Assert.Equal(1, result.Publication.Targets);
        Assert.True(addon.IsPublished);
        Assert.Equal(await ArtifactHash.ComputeSha256Async(source), addon.DownloadSha256);
        await using (var stream = File.OpenRead(result.ManifestPath))
        {
            var manifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options);
            Assert.NotNull(manifest);
            Assert.Empty(manifest.Payload.Packages);
            var publishedAddon = Assert.Single(manifest.Payload.Content!.Items);
            Assert.Equal(
                "https://cdn.example/2.1.132/addons/optional-addon/optional-addon.zip",
                Assert.Single(publishedAddon.Download!.Mirrors).Url);
            using var key = ECDsa.Create();
            key.ImportFromPem(await File.ReadAllTextAsync(publicKey));
            Assert.True(ManifestSecurity.Verify(manifest, key));
        }

        await ReleasePublicationService.UnpublishAddonAsync(addon, workspace, machine);

        Assert.False(addon.IsPublished);
        Assert.False(File.Exists(Path.Combine(output, relativeArtifact)));
        Assert.False(File.Exists(Path.Combine(published, relativeArtifact)));
        Assert.False(File.Exists(Path.Combine(output, workspace.Version, "manifest.json")));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(output, ".releaser-trash"), "optional-addon.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public void InformationCatalogPreservesOrderedRichBlocksAndTranslations()
    {
        var workspace = new ReleaserWorkspace
        {
            Version = "2.1.140",
            Content =
            [
                new ContentDraft
                {
                    Id = "installation-guide",
                    Kind = ContentKind.Information,
                    Title = "Установка",
                    Summary = "Новая вкладка",
                    IsPublished = true,
                    Blocks =
                    [
                        new ContentBlockDraft
                        {
                            Id = "requirements",
                            Kind = ContentBlockKind.Section,
                            Title = "Требования",
                            Body = "Основной текст",
                            TitleEn = "Requirements",
                            BodyEn = "Main text",
                        },
                        new ContentBlockDraft
                        {
                            Id = "download",
                            Kind = ContentBlockKind.Link,
                            Title = "Скачать файл",
                            Url = "https://cdn.example/guide.pdf",
                        },
                    ],
                },
            ],
        };

        var catalog = UnifiedReleaseBuilder.CreateContentCatalog(workspace);

        Assert.Equal(4, catalog.SchemaVersion);
        var information = Assert.Single(catalog.Items);
        var blocks = Assert.IsAssignableFrom<IReadOnlyList<ContentBlock>>(information.Blocks);
        Assert.Equal(["requirements", "download"], blocks.Select(block => block.Id));
        Assert.Equal("Requirements", ContentBlockLocalization.Resolve(blocks[0], "en").Title);
        Assert.Equal("Требования", ContentBlockLocalization.Resolve(blocks[0], "de").Title);
        Assert.Equal("https://cdn.example/guide.pdf", blocks[1].Url);
    }

    [Fact]
    public void ProjectSupportIsAnIndependentRichContentType()
    {
        var support = new ContentDraft
        {
            Id = "project-support",
            Kind = ContentKind.ProjectSupport,
            Section = "general",
            Title = "Поддержка проекта",
            Summary = "Как помочь Anthology",
            Body = "Основной текст",
            IsPublished = true,
            Videos = "Дневник | https://www.youtube.com/watch?v=example",
            Blocks =
            [
                new ContentBlockDraft
                {
                    Id = "support-link",
                    Kind = ContentBlockKind.Link,
                    Title = "Поддержать",
                    Url = "https://example.com/support",
                },
            ],
        };
        support.Translation("en").Title = "Project Support";
        var workspace = new ReleaserWorkspace { Version = "2.1.140", Content = [support] };

        var catalog = UnifiedReleaseBuilder.CreateContentCatalog(workspace);

        var document = Assert.Single(catalog.Items);
        Assert.Equal(ContentKind.ProjectSupport, document.Kind);
        Assert.Equal("Project Support", ContentLocalization.Resolve(document, "en").Title);
        Assert.Equal("https://example.com/support", Assert.Single(document.Blocks!).Url);
        Assert.Equal("https://www.youtube.com/watch?v=example", Assert.Single(document.Videos).Url);
    }

    [Fact]
    public void EditorialSeedImportsOldLauncherCopyAsEditableDrafts()
    {
        var content = new List<ContentDraft>();

        Assert.True(EditorialContentSeed.AddMissing(content));
        Assert.Equal(2, content.Count(item => item.Kind == ContentKind.News));
        Assert.Equal(4, content.Count(item => item.Kind == ContentKind.Information));
        Assert.All(content, item => Assert.False(item.IsPublished));
        var stories = Assert.Single(content, item => item.Id == "stories");
        Assert.Equal(12, stories.Blocks.Count(block => block.Kind == ContentBlockKind.Article));

        content.RemoveAll(item => item.Kind == ContentKind.News);
        Assert.True(EditorialContentSeed.AddMissing(content));
        Assert.Equal(2, content.Count(item => item.Kind == ContentKind.News));
        Assert.Equal(4, content.Count(item => item.Kind == ContentKind.Information));
    }

    [Fact]
    public async Task NewsCanBePublishedAndRemovedWithoutGameRelease()
    {
        var output = Path.Combine(_root, "news-output");
        var published = Path.Combine(_root, "news-published");
        var keys = Path.Combine(_root, "news-keys");
        Directory.CreateDirectory(keys);
        var privateKey = Path.Combine(keys, "private.pem");
        var publicKey = Path.Combine(keys, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKey, publicKey);
        var mirror = new ReleaseMirrorSet { Id = "news-cdn", Provider = "http", Priority = 10 };
        var news = new ContentDraft
        {
            Id = "news-2-1-140",
            Kind = ContentKind.News,
            Title = "Новость 2.1.140",
            IsPublished = false,
        };
        var workspace = new ReleaserWorkspace
        {
            Version = "2.1.140",
            Mirrors = [mirror],
            Content = [news],
        };
        var machine = new ReleaserMachineSettings
        {
            OutputRoot = output,
            PrivateKeyPath = privateKey,
            PublicKeyPath = publicKey,
            KeyId = "news-test-key",
            PublicationRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [mirror.Id] = published,
            },
        };

        var result = await ReleasePublicationService.PublishContentAsync(news, workspace, machine);

        Assert.True(news.IsPublished);
        Assert.NotNull(news.PublishedAt);
        Assert.Equal(1, result.Targets);
        Assert.True(File.Exists(Path.Combine(output, workspace.Version, "content.json")));
        Assert.True(File.Exists(Path.Combine(published, workspace.Version, "content.json")));

        await ReleasePublicationService.UnpublishContentAsync(news, workspace, machine);

        Assert.False(news.IsPublished);
        Assert.False(File.Exists(Path.Combine(output, workspace.Version, "content.json")));
        Assert.False(File.Exists(Path.Combine(published, workspace.Version, "content.json")));
    }

    [Fact]
    public async Task UploadedPhotoIsPublishedWithContentAndRemovedWithIt()
    {
        var output = Path.Combine(_root, "photo-output");
        var published = Path.Combine(_root, "photo-published");
        var keys = Path.Combine(_root, "photo-keys");
        var sourcePhoto = Path.Combine(_root, "cover.png");
        Directory.CreateDirectory(keys);
        await File.WriteAllBytesAsync(sourcePhoto, [0x89, 0x50, 0x4e, 0x47]);
        var privateKey = Path.Combine(keys, "private.pem");
        var publicKey = Path.Combine(keys, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKey, publicKey);
        var mirror = new ReleaseMirrorSet
        {
            Id = "photo-cdn",
            Provider = "http",
            ContentUrl = "https://cdn.example/{version}/addons/{id}/{file}",
            Priority = 10,
        };
        var news = new ContentDraft { Id = "photo-news", Kind = ContentKind.News, Title = "Новость с фото" };
        var workspace = new ReleaserWorkspace { Version = "2.1.141", Mirrors = [mirror], Content = [news] };
        var machine = new ReleaserMachineSettings
        {
            OutputRoot = output,
            PrivateKeyPath = privateKey,
            PublicKeyPath = publicKey,
            KeyId = "photo-test-key",
            ContentImagePaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [ContentMediaPublisher.ContentKey(news.Id)] = [sourcePhoto],
            },
            PublicationRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [mirror.Id] = published,
            },
        };

        await ReleasePublicationService.PublishContentAsync(news, workspace, machine);

        var catalog = JsonSerializer.Deserialize<ContentCatalog>(
            await File.ReadAllTextAsync(Path.Combine(output, workspace.Version, "content.json")),
            ManifestJson.Options);
        var imageUrl = Assert.Single(Assert.Single(catalog!.Items).Images);
        Assert.Equal("https://cdn.example/2.1.141/addons/photo-news/media/01-cover.png", imageUrl);
        Assert.True(File.Exists(Path.Combine(output, workspace.Version, "addons", news.Id, "media", "01-cover.png")));
        Assert.True(File.Exists(Path.Combine(published, workspace.Version, "addons", news.Id, "media", "01-cover.png")));

        await ReleasePublicationService.UnpublishContentAsync(news, workspace, machine);

        Assert.False(Directory.Exists(Path.Combine(output, workspace.Version, "addons", news.Id, "media")));
        Assert.False(Directory.Exists(Path.Combine(published, workspace.Version, "addons", news.Id, "media")));
    }

    [Fact]
    public async Task QuickReleasePublishesAnyFilesAndExplicitDeletionToEverySource()
    {
        var output = Path.Combine(_root, "quick-output");
        var firstTarget = Path.Combine(_root, "quick-yandex");
        var secondTarget = Path.Combine(_root, "quick-google");
        var keys = Path.Combine(_root, "quick-keys");
        var source = Path.Combine(_root, "new-config.ltx");
        var addonFolder = Path.Combine(_root, "new-addon");
        Directory.CreateDirectory(keys);
        Directory.CreateDirectory(Path.Combine(addonFolder, "gamedata", "scripts"));
        await File.WriteAllTextAsync(source, "enabled = true");
        await File.WriteAllTextAsync(Path.Combine(addonFolder, "gamedata", "scripts", "addon.script"), "return true");
        var privateKey = Path.Combine(keys, "private.pem");
        var publicKey = Path.Combine(keys, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKey, publicKey);
        var yandex = new ReleaseMirrorSet
        {
            Id = "yandex",
            Provider = "yandex-disk",
            GameUrl = "https://yandex.example/{version}/{file}",
            Priority = 10,
        };
        var google = new ReleaseMirrorSet
        {
            Id = "google",
            Provider = "google-drive",
            GameUrl = "https://google.example/{version}/{file}",
            Priority = 20,
        };
        var workspace = new ReleaserWorkspace { Version = "2.1.142", Mirrors = [yandex, google] };
        var machine = new ReleaserMachineSettings
        {
            OutputRoot = output,
            PrivateKeyPath = privateKey,
            PublicKeyPath = publicKey,
            KeyId = "quick-test-key",
            QuickReleaseFiles =
            [
                new QuickReleaseFileDraft
                {
                    SourcePath = source,
                    InstallRoot = "game",
                    RelativePath = "gamedata/configs/new-config.ltx",
                },
            ],
            QuickReleaseFolders =
            [
                new QuickReleaseFolderDraft
                {
                    SourcePath = addonFolder,
                    InstallRoot = "game",
                    RelativePath = "addons/example",
                },
            ],
            QuickDeleteFiles =
            [
                new QuickDeleteFileDraft { InstallRoot = "game", RelativePath = "gamedata/configs/obsolete.ltx" },
            ],
            QuickDeleteFolders =
            [
                new QuickDeleteFolderDraft { InstallRoot = "game", RelativePath = "gamedata/scripts/old-addon" },
            ],
            PublicationRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [yandex.Id] = firstTarget,
                [google.Id] = secondTarget,
            },
        };

        var result = await ReleasePublicationService.PublishQuickFilesAsync(workspace, machine);

        Assert.Equal(2, result.AddedFiles);
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(1, result.AddedFolders);
        Assert.Equal(1, result.DeletedFolders);
        Assert.Equal(2, result.Publication.Targets);
        await using var stream = File.OpenRead(result.ManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options);
        var package = Assert.Single(manifest!.Payload.Packages);
        Assert.Equal(4, manifest.Payload.SchemaVersion);
        Assert.Equal(["addons/example/gamedata/scripts/addon.script", "gamedata/configs/new-config.ltx"], package.Files);
        Assert.Equal(["gamedata/configs/obsolete.ltx"], package.DeletedFiles);
        Assert.Equal(["gamedata/scripts/old-addon"], package.DeletedDirectories);
        var artifactName = Path.GetFileName(Assert.Single(result.Artifacts));
        Assert.True(File.Exists(Path.Combine(firstTarget, workspace.Version, artifactName)));
        Assert.True(File.Exists(Path.Combine(secondTarget, workspace.Version, artifactName)));
        Assert.True(File.Exists(Path.Combine(firstTarget, workspace.Version, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(secondTarget, workspace.Version, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(firstTarget, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(secondTarget, "manifest.json")));
    }

    [Fact]
    public async Task LauncherPublicationStagesSignedUpdateForNextStart()
    {
        var game = Path.Combine(_root, "launcher-publication-game");
        var launcher = Path.Combine(game, "AnthologyLauncher");
        var app = Path.Combine(launcher, "App");
        var web = Path.Combine(app, "wwwroot", "css");
        var output = Path.Combine(_root, "launcher-publication-output");
        var publication = Path.Combine(_root, "launcher-publication-target");
        var keys = Path.Combine(_root, "launcher-publication-keys");
        Directory.CreateDirectory(web);
        Directory.CreateDirectory(keys);
        await File.WriteAllTextAsync(Path.Combine(app, "AnthologyLauncher.Next.exe"), "host");
        await File.WriteAllTextAsync(Path.Combine(app, "AnthologyLauncher.Next.dll"), "launcher");
        await File.WriteAllTextAsync(Path.Combine(web, "app.css"), "body{}");
        await File.WriteAllTextAsync(Path.Combine(launcher, "Start-AnthologyLauncherNext.ps1"), "# updater");
        var privateKey = Path.Combine(keys, "private.pem");
        var publicKey = Path.Combine(keys, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKey, publicKey);
        var mirror = new ReleaseMirrorSet
        {
            Provider = "local-file",
            GameUrl = new Uri(publication + Path.DirectorySeparatorChar).AbsoluteUri + "{version}/{file}",
            ManifestUrl = "https://cdn.example/manifest.json",
            Priority = 10,
        };
        var workspace = new ReleaserWorkspace
        {
            Version = "2.1.132",
            Channel = "next",
            Mirrors = [mirror],
        };
        var machine = new ReleaserMachineSettings
        {
            GameSourceRoot = game,
            OutputRoot = output,
            PrivateKeyPath = privateKey,
            PublicKeyPath = publicKey,
            KeyId = "launcher-test",
            PublicationRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [mirror.Id] = publication,
            },
        };

        var result = await ReleasePublicationService.PublishLauncherAsync(workspace, machine);

        Assert.Equal(4, result.Files);
        Assert.True(File.Exists(result.ArtifactPath));
        await using var stream = File.OpenRead(result.ManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options);
        var package = Assert.Single(manifest!.Payload.Packages);
        Assert.Equal("anthology-launcher", package.Id);
        Assert.Equal(PackageKind.Launcher, package.Kind);
        Assert.Equal("game", package.InstallRoot);
        Assert.Contains(package.Files, path => path.EndsWith("launcher-update.json", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(publication, workspace.Version, Path.GetFileName(result.ArtifactPath))));
        Assert.True(File.Exists(Path.Combine(publication, "manifest.json")));
        using var delivery = ZipFile.OpenRead(result.ArtifactPath);
        Assert.Contains(delivery.Entries, entry => entry.FullName.EndsWith("launcher-update.json", StringComparison.Ordinal));
        Assert.Contains(delivery.Entries, entry => entry.FullName.EndsWith("Start-AnthologyLauncherNext.ps1", StringComparison.Ordinal));
        Assert.Contains(delivery.Entries, entry => entry.FullName.EndsWith("Update/channel.json", StringComparison.Ordinal));
        Assert.Contains(delivery.Entries, entry => entry.FullName.Contains("launcher-payload", StringComparison.Ordinal));
        var updaterState = Path.Combine(_root, "launcher-publication-state");
        var coordinator = new UpdateCoordinator(new HttpClient());
        var check = await coordinator.CheckAsync(result.ManifestPath, publicKey, "next", updaterState);
        var applied = await coordinator.ApplyAsync(
            check,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game"] = game },
            updaterState);
        Assert.Equal(1, applied.InstalledPackages);
        Assert.Equal(4, applied.InstalledFiles);
        var installedChannel = await File.ReadAllTextAsync(Path.Combine(launcher, "Update", "channel.json"));
        Assert.Contains("https://cdn.example/manifest.json", installedChannel, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(launcher, "Update", "LauncherPending", "launcher-update.json")));
        Assert.True(Directory.EnumerateFiles(Path.Combine(launcher, "Update", "LauncherPending"), "*payload*.zip").Any());
    }

    [Fact]
    public async Task ReleaserPreparesIntegratedLauncherWithoutManualPublicKeySelection()
    {
        var game = Path.Combine(_root, "launcher-game");
        var launcher = Path.Combine(game, "AnthologyLauncher");
        var app = Path.Combine(launcher, "App");
        var keys = Path.Combine(_root, "launcher-keys");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(keys);
        await File.WriteAllTextAsync(Path.Combine(app, "AnthologyLauncher.Next.exe"), "launcher");
        var privateKey = Path.Combine(keys, "private.pem");
        var publicKey = Path.Combine(keys, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKey, publicKey);
        var workspace = new ReleaserWorkspace
        {
            Channel = "next",
            Mirrors =
            [
                new ReleaseMirrorSet
                {
                    Provider = "github",
                    ManifestUrl = "https://cdn.example/anthology/manifest.json",
                    Priority = 10,
                },
            ],
        };
        var machine = new ReleaserMachineSettings
        {
            GameSourceRoot = game,
            PublicKeyPath = publicKey,
        };

        var result = await LauncherUpdateConfigurationPublisher.PrepareAsync(workspace, machine);

        Assert.True(result.LauncherFound);
        Assert.True(result.PublicKeyCopied);
        Assert.Equal("https://cdn.example/anthology/manifest.json", result.ManifestSource);
        Assert.True(File.Exists(Path.Combine(app, "TrustedKeys", "anthology.public.pem")));
        var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(launcher, "Update", "channel.json")));
        Assert.Equal("https://cdn.example/anthology/manifest.json", descriptor.RootElement.GetProperty("manifestSource").GetString());
        Assert.Equal("next", descriptor.RootElement.GetProperty("channel").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
