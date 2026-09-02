using System.Text.Json;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Diagnostics;
using Anthology.Contracts;
using Anthology.Releaser.Core;

namespace Anthology.Update.Core.Tests;

public sealed class ReleaserCoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anthology-releaser-{Guid.NewGuid():N}");

    [Fact]
    public void ProductionSigningPolicyRejectsAnotherKeyPair()
    {
        var keys = Path.Combine(_root, "wrong-production-keys");
        var privateKey = Path.Combine(keys, "private.pem");
        var publicKey = Path.Combine(keys, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKey, publicKey);
        var machine = new ReleaserMachineSettings
        {
            OutputRoot = Path.Combine(_root, "output"),
            PrivateKeyPath = privateKey,
            PublicKeyPath = publicKey,
            KeyId = ProductionSigningKeyPolicy.KeyId,
        };

        var exception = Assert.Throws<CryptographicException>(
            () => UnifiedReleaseBuilder.ValidateMachine(machine));

        Assert.Contains("другой production-ключ", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

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
        Directory.CreateDirectory(Path.Combine(mo2, "downloads"));
        Directory.CreateDirectory(Path.Combine(mo2, "profiles", "Anthology"));
        await File.WriteAllTextAsync(Path.Combine(game, "fsgame.ltx"), "game");
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "Anomaly.exe"), "binary");
        await File.WriteAllTextAsync(Path.Combine(game, "appdata", "user.ltx"), "private");
        await File.WriteAllTextAsync(Path.Combine(mo2, "ModOrganizer.exe"), "mo2");
        await File.WriteAllTextAsync(Path.Combine(mo2, "mods", "example", "mod.txt"), "mod");
        await File.WriteAllTextAsync(Path.Combine(mo2, "overwrite", "transient.txt"), "transient");
        await File.WriteAllTextAsync(Path.Combine(mo2, "downloads", "private-download.zip"), "private");
        await File.WriteAllTextAsync(Path.Combine(mo2, "ModOrganizer.ini"), "private-settings");
        await File.WriteAllTextAsync(Path.Combine(mo2, "profiles", "Anthology", "modlist.txt"), "+example");
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

        Assert.Equal(3, result.Artifacts.Count);
        Assert.Equal(1, result.ContentItems);
        await using var stream = File.OpenRead(result.ManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options);
        Assert.NotNull(manifest);
        Assert.Equal(4, manifest.Payload.SchemaVersion);
        var payloadPackages = manifest.Payload.Packages
            .Where(package => package.Kind != PackageKind.Launcher)
            .ToArray();
        Assert.Equal(2, payloadPackages.Length);
        Assert.All(payloadPackages, package => Assert.Equal(PackageUpdateMode.ManagedExact, package.UpdateMode));
        Assert.All(payloadPackages, package => Assert.True(package.PruneInstallRoot));
        var localizedNews = Assert.Single(manifest.Payload.Content!.Items);
        Assert.Equal(4, manifest.Payload.Content.SchemaVersion);
        Assert.Equal("Full text", ContentLocalization.Resolve(localizedNews, "en").Body);
        Assert.Equal("Vollständiger Text", ContentLocalization.Resolve(localizedNews, "de").Body);
        Assert.Equal("Полный текст", ContentLocalization.Resolve(localizedNews, "fr").Body);
        Assert.DoesNotContain(payloadPackages.Single(package => package.InstallRoot == "game").Files, file => file.StartsWith("appdata/", StringComparison.OrdinalIgnoreCase));
        var modpackPackage = payloadPackages.Single(package => package.InstallRoot == "modpack");
        Assert.DoesNotContain(modpackPackage.Files, file => file.StartsWith("overwrite/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(modpackPackage.Files, file => file.StartsWith("downloads/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(modpackPackage.Files, file => file.Equals("ModOrganizer.ini", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("profiles/Anthology/modlist.txt", modpackPackage.Files);
        Assert.Contains("profiles", modpackPackage.PreservedPaths!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("downloads", modpackPackage.PreservedPaths!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("overwrite", modpackPackage.PreservedPaths!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ModOrganizer.ini", modpackPackage.PreservedPaths!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnifiedBuilderRejectsModsDirectoryAsTheFullMo2Root()
    {
        var game = Path.Combine(_root, "wrong-mo2-root-game");
        var mo2 = Path.Combine(_root, "wrong-mo2-root");
        var mods = Path.Combine(mo2, "mods");
        var output = Path.Combine(_root, "wrong-mo2-root-output");
        var keys = Path.Combine(_root, "wrong-mo2-root-keys");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(keys);
        await File.WriteAllTextAsync(Path.Combine(game, "fsgame.ltx"), "game");
        await File.WriteAllTextAsync(Path.Combine(mo2, "ModOrganizer.exe"), "mo2");
        await File.WriteAllTextAsync(Path.Combine(mods, "Example Addon.txt"), "addon");
        var privateKey = Path.Combine(keys, "private.pem");
        var publicKey = Path.Combine(keys, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKey, publicKey);
        var machine = new ReleaserMachineSettings
        {
            GameSourceRoot = game,
            Mo2SourceRoot = mods,
            OutputRoot = output,
            PrivateKeyPath = privateKey,
            PublicKeyPath = publicKey,
            KeyId = "wrong-mo2-root-test",
        };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            UnifiedReleaseBuilder.BuildAsync(new UnifiedReleaseRequest(
                new ReleaserWorkspace { Version = "2.1.201" },
                machine)));

        Assert.Contains("MO2\\mods", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ModOrganizer.exe", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(output, "2.1.201")));
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
    public async Task NewerWorkspaceRevisionResolvesAStaleBaselineWithoutBlockingRelease()
    {
        var sharedRoot = Path.Combine(_root, "shared-stale-baseline");
        Directory.CreateDirectory(sharedRoot);
        var shared = new ReleaserWorkspace { Revision = 80, UpdatedBy = "Ratniy", Version = "2.1.140" };
        await WorkspaceStorage.SaveAsync(Path.Combine(sharedRoot, WorkspaceSyncService.SharedFileName), shared);
        var local = new ReleaserWorkspace { Revision = 84, UpdatedBy = "Шура", Version = "2.1.142" };

        var result = await WorkspaceSyncService.SyncAsync(local, sharedRoot, "obsolete-baseline-hash");

        Assert.Equal(WorkspaceSyncDirection.Published, result.Direction);
        Assert.Equal(84, result.Workspace.Revision);
        var synchronized = await WorkspaceStorage.LoadAsync(
            Path.Combine(sharedRoot, WorkspaceSyncService.SharedFileName),
            () => new ReleaserWorkspace());
        Assert.Equal("2.1.142", synchronized.Version);
        Assert.Single(Directory.GetFiles(Path.Combine(sharedRoot, "Conflicts"), "superseded-shared-r80-*.json"));
    }

    [Fact]
    public async Task WorkspaceStorageRestoresTheLastValidCopyWhenCloudSyncCorruptsJson()
    {
        var path = Path.Combine(_root, "resilient-storage", "release-workspace.json");
        await WorkspaceStorage.SaveAsync(path, new ReleaserWorkspace { Revision = 41, Version = "2.1.140" });
        await WorkspaceStorage.SaveAsync(path, new ReleaserWorkspace { Revision = 42, Version = "2.1.141" });
        Assert.True(File.Exists(path + ".bak"));

        await File.WriteAllBytesAsync(path, new byte[256]);
        var recovered = await WorkspaceStorage.LoadAsync(path, () => new ReleaserWorkspace());

        Assert.Equal(41, recovered.Revision);
        Assert.Equal("2.1.140", recovered.Version);
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, "release-workspace.json.corrupt-*"));
        var restoredPrimary = await WorkspaceStorage.LoadAsync(path, () => new ReleaserWorkspace());
        Assert.Equal(41, restoredPrimary.Revision);
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
    public void PlainUrlsKeepQueryParametersWhenContentCatalogIsBuilt()
    {
        const string videoUrl = "https://www.youtube.com/watch?v=R_-tFWOVSLE";
        const string downloadUrl = "https://drive.google.com/file/d/demo/view?usp=sharing";
        var mod = new ContentDraft
        {
            Id = "query-url-test",
            Kind = ContentKind.Mod,
            Title = "Query URL test",
            IsPublished = true,
            Videos = videoUrl,
            DownloadFileName = "query-url-test.zip",
            InstallFolderName = "Query URL test",
            DownloadSize = 42,
            DownloadSha256 = new string('a', 64),
            DownloadMirrors = downloadUrl,
        };
        var workspace = new ReleaserWorkspace { Version = "2.1.140", Content = [mod] };

        var catalog = UnifiedReleaseBuilder.CreateContentCatalog(workspace);

        var document = Assert.Single(catalog.Items);
        var video = Assert.Single(document.Videos);
        Assert.Equal("Видео", video.Title);
        Assert.Equal(videoUrl, video.Url);
        Assert.NotNull(document.Download);
        var mirror = Assert.Single(document.Download.Mirrors);
        Assert.Equal("http", mirror.Provider);
        Assert.Equal(downloadUrl, mirror.Url);
    }

    [Fact]
    public void SocialLinksAreEditableOrderedAndPublishedWithContentCatalog()
    {
        var workspace = new ReleaserWorkspace
        {
            Version = "2.1.140",
            SocialLinks =
            [
                new SocialLinkDraft { Id = "discord", Title = "Наш Discord", Subtitle = "Общение", Url = "https://discord.gg/uYS8JUz7J", Order = 20 },
                new SocialLinkDraft { Id = "youtube", Title = "Видео", Subtitle = "Самаэль Морнингстар", Url = "https://www.youtube.com/@Samael-w3p", Order = 10 },
                new SocialLinkDraft { Id = "moddb", Title = "Скрыто", Url = "https://www.moddb.com/mods/stalker-anomaly", Order = 30, IsVisible = false },
            ],
            Content =
            [
                new ContentDraft
                {
                    Id = "author-project",
                    Kind = ContentKind.Mod,
                    Title = "Проект автора",
                    IsPublished = true,
                    AuthorLinks =
                    [
                        new SocialLinkDraft { Id = "github", Title = "GitHub", Subtitle = "Исходный код", Url = "https://github.com/example/project", Order = 20 },
                        new SocialLinkDraft { Id = "youtube", Title = "YouTube", Subtitle = "Канал автора", Url = "https://www.youtube.com/@example", Order = 10 },
                        new SocialLinkDraft { Id = "discord", Title = "Скрыто", Url = "https://discord.gg/example", Order = 30, IsVisible = false },
                    ],
                },
            ],
        };

        var catalog = UnifiedReleaseBuilder.CreateContentCatalog(workspace);

        Assert.Equal(["youtube", "discord"], catalog.SocialLinks!.Select(link => link.Id));
        Assert.Equal("Видео", catalog.SocialLinks![0].Title);
        Assert.Equal("https://discord.gg/uYS8JUz7J", catalog.SocialLinks![1].Url);
        var content = Assert.Single(catalog.Items);
        Assert.Equal(["youtube", "github"], content.AuthorLinks!.Select(link => link.Id));
        Assert.Equal("https://github.com/example/project", content.AuthorLinks![1].Url);
    }

    [Fact]
    public void PresentationCatalogPublishesPeopleStreamsAndLocalizedChangelog()
    {
        var person = new ProjectPersonDraft
        {
            Id = "friend-of-project",
            Name = "Друг проекта",
            Role = "Автор",
            Description = "Помогает проекту",
            ImageUrl = "https://cdn.example/person.png",
            Links =
            [
                new SocialLinkDraft { Id = "youtube", Title = "YouTube", Url = "https://youtube.com/@friend", IsVisible = true },
                new SocialLinkDraft { Id = "discord", Title = "Discord", Url = "", IsVisible = false },
            ],
        };
        person.Translation("en").Name = "Project Friend";
        var stream = new LiveStreamDraft
        {
            Id = "launch-stream",
            Title = "Трансляция",
            Subtitle = "Разработка",
            Url = "https://www.youtube.com/watch?v=example",
        };
        stream.Translation("en").Title = "Live stream";
        var workspace = new ReleaserWorkspace
        {
            Version = "2.1.143",
            ProjectPeople = [person],
            LiveStreams = [stream],
            Changelog = new ReleaseChangelogDraft
            {
                Title = "Изменения 2.1.143",
                Summary = "Кратко",
                Body = "Добавлен файл.",
                Warnings = "Будет удалён старый файл.",
            },
        };
        workspace.Changelog.Translation("en").Warnings = "An old file will be removed.";

        var catalog = UnifiedReleaseBuilder.CreateContentCatalog(workspace);

        var publishedPerson = Assert.Single(catalog.ProjectPeople!);
        Assert.Equal("Project Friend", ProjectPersonLocalization.Resolve(publishedPerson, "en").Name);
        Assert.Equal("https://youtube.com/@friend", Assert.Single(publishedPerson.Links).Url);
        var publishedStream = Assert.Single(catalog.LiveStreams!);
        Assert.Equal("Live stream", LiveStreamLocalization.Resolve(publishedStream, "en").Title);
        Assert.Equal("An old file will be removed.", ReleaseChangelogLocalization.Resolve(catalog.Changelog!, "en").Warnings);
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
    public async Task GithubContentPublicationCommitsAndPushesOnlyCurrentVersion()
    {
        Directory.CreateDirectory(_root);
        var repository = Path.Combine(_root, "github-publication");
        var remote = Path.Combine(_root, "github-publication.git");
        var output = Path.Combine(_root, "github-output");
        var keys = Path.Combine(_root, "github-keys");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(keys);
        RunGit(repository, "init", "-b", "addons-unified-library");
        RunGit(repository, "config", "user.name", "Anthology test");
        RunGit(repository, "config", "user.email", "anthology-test@example.invalid");
        RunGit(_root, "init", "--bare", remote);
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "publication test");
        RunGit(repository, "add", "README.md");
        RunGit(repository, "commit", "-m", "Initial publication repository");
        RunGit(repository, "remote", "add", "origin", remote);
        RunGit(repository, "push", "-u", "origin", "addons-unified-library");

        var unrelatedDraft = Path.Combine(repository, "2.1.132", "draft.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelatedDraft)!);
        await File.WriteAllTextAsync(unrelatedDraft, "must stay local");

        var privateKey = Path.Combine(keys, "private.pem");
        var publicKey = Path.Combine(keys, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKey, publicKey);
        var mirror = new ReleaseMirrorSet
        {
            Id = "github",
            Provider = "github",
            ContentUrl = "https://raw.example/{version}/content.json",
            Priority = 10,
        };
        var information = new ContentDraft
        {
            Id = "stories",
            Kind = ContentKind.Information,
            Title = "Stories",
        };
        var workspace = new ReleaserWorkspace
        {
            Version = "2.1.150",
            Mirrors = [mirror],
            Content = [information],
        };
        var machine = new ReleaserMachineSettings
        {
            OutputRoot = output,
            PrivateKeyPath = privateKey,
            PublicKeyPath = publicKey,
            KeyId = "github-test-key",
            PublicationRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [mirror.Id] = repository,
            },
        };

        await ReleasePublicationService.PublishContentAsync(information, workspace, machine);

        Assert.True(File.Exists(Path.Combine(repository, workspace.Version, "content.json")));
        Assert.Contains("?? 2.1.132/", RunGit(repository, "status", "--short"), StringComparison.Ordinal);
        Assert.Contains(
            "Stories",
            RunGit(repository, "show", $"origin/addons-unified-library:{workspace.Version}/content.json"),
            StringComparison.Ordinal);
        Assert.Equal(
            $"Publish Anthology {workspace.Version}",
            RunGit(repository, "log", "-1", "--pretty=%s").Trim());
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
        Assert.StartsWith("https://cdn.example/2.1.141/addons/photo-news/media/01-cover-", imageUrl, StringComparison.Ordinal);
        Assert.EndsWith(".png", imageUrl, StringComparison.Ordinal);
        var imageFileName = Path.GetFileName(new Uri(imageUrl).AbsolutePath);
        Assert.True(File.Exists(Path.Combine(output, workspace.Version, "addons", news.Id, "media", imageFileName)));
        Assert.True(File.Exists(Path.Combine(published, workspace.Version, "addons", news.Id, "media", imageFileName)));

        await ReleasePublicationService.UnpublishContentAsync(news, workspace, machine);

        Assert.False(Directory.Exists(Path.Combine(output, workspace.Version, "addons", news.Id, "media")));
        Assert.False(Directory.Exists(Path.Combine(published, workspace.Version, "addons", news.Id, "media")));
    }

    [Fact]
    public async Task UploadedVideoIsPublishedAndAddedToContentCatalog()
    {
        var output = Path.Combine(_root, "video-output");
        var published = Path.Combine(_root, "video-published");
        var keys = Path.Combine(_root, "video-keys");
        var sourceVideo = Path.Combine(_root, "developer-diary.mp4");
        Directory.CreateDirectory(keys);
        await File.WriteAllBytesAsync(sourceVideo, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70]);
        var privateKey = Path.Combine(keys, "private.pem");
        var publicKey = Path.Combine(keys, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKey, publicKey);
        var mirror = new ReleaseMirrorSet
        {
            Id = "video-cdn",
            Provider = "http",
            ContentUrl = "https://cdn.example/{version}/addons/{id}/{file}",
            Priority = 10,
        };
        var news = new ContentDraft { Id = "video-news", Kind = ContentKind.News, Title = "Дневник разработки" };
        var workspace = new ReleaserWorkspace { Version = "2.1.142", Mirrors = [mirror], Content = [news] };
        var machine = new ReleaserMachineSettings
        {
            OutputRoot = output,
            PrivateKeyPath = privateKey,
            PublicKeyPath = publicKey,
            KeyId = "video-test-key",
            ContentVideoPaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [ContentMediaPublisher.ContentKey(news.Id)] = [sourceVideo],
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
        var video = Assert.Single(Assert.Single(catalog!.Items).Videos);
        Assert.Equal("developer-diary", video.Title);
        Assert.StartsWith("https://cdn.example/2.1.142/addons/video-news/media/video-01-developer-diary-", video.Url, StringComparison.Ordinal);
        Assert.EndsWith(".mp4", video.Url, StringComparison.Ordinal);
        var videoFileName = Path.GetFileName(new Uri(video.Url).AbsolutePath);
        Assert.True(File.Exists(Path.Combine(output, workspace.Version, "addons", news.Id, "media", videoFileName)));
        Assert.True(File.Exists(Path.Combine(published, workspace.Version, "addons", news.Id, "media", videoFileName)));
    }

    [Fact]
    public async Task InlineImagePrefersRawGithubUrlOverYandexSharingPage()
    {
        var versionRoot = Path.Combine(_root, "inline-media", "2.1.151");
        var sourcePhoto = Path.Combine(_root, "story-background.png");
        var sourceVideo = Path.Combine(_root, "story-trailer.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePhoto)!);
        await File.WriteAllBytesAsync(sourcePhoto, [0x89, 0x50, 0x4e, 0x47]);
        await File.WriteAllBytesAsync(sourceVideo, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70]);
        var story = new ContentDraft
        {
            Id = "stories",
            Kind = ContentKind.Information,
            Title = "Stories",
            IsPublished = true,
            Blocks =
            [
                new ContentBlockDraft
                {
                    Id = "story-soc",
                    Kind = ContentBlockKind.Article,
                    Title = "Shadow of Chernobyl",
                },
            ],
        };
        var workspace = new ReleaserWorkspace
        {
            Version = "2.1.151",
            Content = [story],
            Mirrors =
            [
                new ReleaseMirrorSet
                {
                    Id = "yandex",
                    Provider = "yandex-disk",
                    ContentUrl = "https://disk.yandex.ru/d/example?path=/{version}/addons/{id}/{file}",
                    Priority = 10,
                },
                new ReleaseMirrorSet
                {
                    Id = "github",
                    Provider = "github",
                    ContentUrl = "https://raw.githubusercontent.com/example/anthology/media/{version}/addons/{id}/{file}",
                    Priority = 20,
                },
            ],
        };
        var machine = new ReleaserMachineSettings
        {
            ContentImagePaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [ContentMediaPublisher.BlockKey(story.Id, story.Blocks[0].Id)] = [sourcePhoto],
            },
            ContentVideoPaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [ContentMediaPublisher.ContentKey(story.Id)] = [sourceVideo],
            },
        };

        var media = await ContentMediaPublisher.PrepareAsync(workspace, machine, versionRoot);

        var url = Assert.Single(media.BlockImages).Value;
        Assert.StartsWith("https://raw.githubusercontent.com/", url, StringComparison.Ordinal);
        var videoUrl = Assert.Single(media.ContentVideos[story.Id]).Url;
        Assert.StartsWith("https://disk.yandex.ru/", videoUrl, StringComparison.Ordinal);
        Assert.Equal(2, media.RelativeFiles.Count);
        Assert.All(media.RelativeFiles, relativePath => Assert.True(File.Exists(Path.Combine(versionRoot, relativePath))));
    }

    [Fact]
    public async Task ReplacingPhotoBytesChangesPublicUrlEvenWhenFileNameStaysTheSame()
    {
        var versionRoot = Path.Combine(_root, "cache-busted-media", "2.1.152");
        var sourcePhoto = Path.Combine(_root, "same-name.png");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(sourcePhoto, [0x89, 0x50, 0x4e, 0x47, 0x01]);
        var news = new ContentDraft { Id = "news", Kind = ContentKind.News, IsPublished = true };
        var workspace = new ReleaserWorkspace
        {
            Version = "2.1.152",
            Content = [news],
            Mirrors = [new ReleaseMirrorSet { Provider = "http", ContentUrl = "https://cdn.example/{version}/addons/{id}/{file}" }],
        };
        var machine = new ReleaserMachineSettings
        {
            ContentImagePaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [ContentMediaPublisher.ContentKey(news.Id)] = [sourcePhoto],
            },
        };

        var first = await ContentMediaPublisher.PrepareAsync(workspace, machine, versionRoot);
        await File.WriteAllBytesAsync(sourcePhoto, [0x89, 0x50, 0x4e, 0x47, 0x02]);
        var second = await ContentMediaPublisher.PrepareAsync(workspace, machine, versionRoot);

        Assert.NotEqual(Assert.Single(first.ContentImages[news.Id]), Assert.Single(second.ContentImages[news.Id]));
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
        var package = Assert.Single(
            manifest!.Payload.Packages,
            item => item.Id.Equals("anthology-files-game", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, manifest.Payload.SchemaVersion);
        Assert.Equal(["addons/example/gamedata/scripts/addon.script", "gamedata/configs/new-config.ltx"], package.Files);
        Assert.Equal(["gamedata/configs/obsolete.ltx"], package.DeletedFiles);
        Assert.Equal(["gamedata/scripts/old-addon"], package.DeletedDirectories);
        var artifactName = Path.GetFileName(Assert.Single(
            result.Artifacts,
            path => Path.GetFileName(path).StartsWith("anthology-files-game-", StringComparison.OrdinalIgnoreCase)));
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
            Content =
            [
                new ContentDraft
                {
                    Id = "launcher-publication-news",
                    Kind = ContentKind.News,
                    Title = "Launcher publication must preserve me",
                    IsPublished = true,
                },
            ],
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

        var firstResult = await ReleasePublicationService.PublishLauncherAsync(workspace, machine);
        var firstArtifactName = Path.GetFileName(firstResult.ArtifactPath);
        var firstArtifactHash = await ArtifactHash.ComputeSha256Async(firstResult.ArtifactPath);
        PackageManifest firstPackage;
        await using (var firstStream = File.OpenRead(firstResult.ManifestPath))
        {
            var firstManifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(firstStream, ManifestJson.Options);
            firstPackage = Assert.Single(
                firstManifest!.Payload.Packages,
                item => item.Id.Equals("anthology-launcher", StringComparison.OrdinalIgnoreCase));
        }

        await File.WriteAllTextAsync(Path.Combine(web, "app.css"), "body{color:red}");
        workspace.Content.Clear();
        workspace.Changelog = new ReleaseChangelogDraft
        {
            Title = "Launcher 0.7.0 alpha",
            Summary = "New launcher build",
            Body = "Diagnostics and settings were updated.",
            Warnings = "Restart the launcher after installation.",
        };
        var result = await ReleasePublicationService.PublishLauncherAsync(workspace, machine);

        Assert.Equal(4, result.Files);
        Assert.True(File.Exists(result.ArtifactPath));
        await using var stream = File.OpenRead(result.ManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options);
        var package = Assert.Single(
            manifest!.Payload.Packages,
            item => item.Id.Equals("anthology-launcher", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("launcher-publication-news", Assert.Single(manifest.Payload.Content!.Items).Id);
        Assert.Equal(workspace.Version, manifest.Payload.Content.Version);
        Assert.Equal("Launcher 0.7.0 alpha", manifest.Payload.Content.Changelog!.Title);
        Assert.Equal("Diagnostics and settings were updated.", manifest.Payload.Content.Changelog.Body);
        Assert.Equal("anthology-launcher", package.Id);
        Assert.Equal(PackageKind.Launcher, package.Kind);
        Assert.Equal("game", package.InstallRoot);
        Assert.Contains(package.Files, path => path.EndsWith("launcher-update.json", StringComparison.Ordinal));
        var artifactName = Path.GetFileName(result.ArtifactPath);
        Assert.Matches("^anthology-launcher-[a-zA-Z0-9._-]+-[0-9a-f]{16}\\.zip$", artifactName);
        Assert.NotEqual(firstArtifactName, artifactName);
        Assert.True(File.Exists(firstResult.ArtifactPath));
        Assert.Equal(firstArtifactHash, await ArtifactHash.ComputeSha256Async(firstResult.ArtifactPath));
        Assert.Equal(firstArtifactHash, firstPackage.Sha256);
        Assert.Equal(await ArtifactHash.ComputeSha256Async(result.ArtifactPath), package.Sha256);
        Assert.EndsWith(firstArtifactName, Assert.Single(firstPackage.Mirrors).Url, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(artifactName, Assert.Single(package.Mirrors).Url, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(publication, workspace.Version, firstArtifactName)));
        Assert.True(File.Exists(Path.Combine(publication, workspace.Version, Path.GetFileName(result.ArtifactPath))));
        Assert.True(File.Exists(Path.Combine(publication, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(output, "manifest.json")));
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
            foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            Directory.Delete(_root, true);
        }
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git did not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }

        return output;
    }
}
