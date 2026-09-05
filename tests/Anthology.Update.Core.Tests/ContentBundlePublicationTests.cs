using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthology.Contracts;
using Anthology.Releaser.Core;
using Anthology.Update.Core;

namespace Anthology.Update.Core.Tests;

public sealed class ContentBundlePublicationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"anthology-content-bundle-{Guid.NewGuid():N}");

    [Fact]
    public async Task PublishesEditorialAddonAndQuickChangesWithOneFinalManifest()
    {
        var outputRoot = Path.Combine(_root, "output");
        var targetRoot = Path.Combine(_root, "target");
        var keysRoot = Path.Combine(_root, "keys");
        var addonArchive = Path.Combine(_root, "new-addon.zip");
        var quickFile = Path.Combine(_root, "quick.ltx");
        var quickFolder = Path.Combine(_root, "quick-folder");
        Directory.CreateDirectory(keysRoot);
        Directory.CreateDirectory(Path.Combine(quickFolder, "gamedata", "scripts"));
        await CreateZipAsync(addonArchive, "gamedata/new-addon.script", "return true");
        await File.WriteAllTextAsync(quickFile, "enabled = true");
        await File.WriteAllTextAsync(
            Path.Combine(quickFolder, "gamedata", "scripts", "folder-addon.script"),
            "return true");
        var (privateKeyPath, publicKeyPath) = CreateKeys(keysRoot);

        await WriteStableContentManifestAsync(
            outputRoot,
            privateKeyPath,
            "2.1.199",
            new ContentDocument(
                "existing-addon",
                ContentKind.Mod,
                "dev",
                "Existing addon",
                string.Empty,
                string.Empty,
                [],
                [],
                new ContentDownload(
                    "existing-addon.zip",
                    25,
                    new string('a', 64),
                    [new MirrorManifest(
                        "yandex-disk",
                        "https://cdn.example/2.1.199/addons/existing-addon/existing-addon.zip",
                        10)],
                    "Existing addon",
                    true)));

        var mirror = new ReleaseMirrorSet
        {
            Id = "mirror",
            Provider = "yandex-disk",
            GameUrl = "https://cdn.example/{version}/game/{file}",
            Mo2Url = "https://cdn.example/{version}/mo2/{file}",
            ContentUrl = "https://cdn.example/{version}/addons/{id}/{file}",
            Priority = 10,
        };
        var existingAddon = new ContentDraft
        {
            Id = "existing-addon",
            Kind = ContentKind.Mod,
            Section = "dev",
            Title = "Existing addon",
            IsPublished = true,
        };
        var selectedAddon = new ContentDraft
        {
            Id = "selected-addon",
            Kind = ContentKind.Mod,
            Section = "solutions",
            Title = "Selected addon",
        };
        var unselectedDraft = new ContentDraft
        {
            Id = "draft-addon",
            Kind = ContentKind.Mod,
            Title = "Draft addon",
        };
        var news = new ContentDraft { Id = "latest-news", Kind = ContentKind.News, Title = "Latest news" };
        var information = new ContentDraft { Id = "about", Kind = ContentKind.Information, Title = "About" };
        var support = new ContentDraft { Id = "support", Kind = ContentKind.ProjectSupport, Title = "Support" };
        var workspace = new ReleaserWorkspace
        {
            Version = "2.1.200",
            Channel = "next",
            Mirrors = [mirror],
            Content = [news, information, support, existingAddon, selectedAddon, unselectedDraft],
            SocialLinks =
            [
                new SocialLinkDraft
                {
                    Id = "discord",
                    Title = "Discord",
                    Url = "https://discord.example/anthology",
                    IsVisible = true,
                },
            ],
            ProjectPeople =
            [
                new ProjectPersonDraft
                {
                    Id = "developer",
                    Name = "Developer",
                    Role = "Author",
                    IsVisible = true,
                },
            ],
            LiveStreams =
            [
                new LiveStreamDraft
                {
                    Id = "live",
                    Title = "Live",
                    Url = "https://video.example/live",
                    IsVisible = true,
                },
            ],
            Changelog = new ReleaseChangelogDraft
            {
                Title = "2.1.200",
                Body = "Content-only release",
            },
        };
        var machine = new ReleaserMachineSettings
        {
            OutputRoot = outputRoot,
            PrivateKeyPath = privateKeyPath,
            PublicKeyPath = publicKeyPath,
            KeyId = "content-bundle-test",
            ContentArchivePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [selectedAddon.Id] = addonArchive,
            },
            QuickReleaseFiles =
            [
                new QuickReleaseFileDraft
                {
                    SourcePath = quickFile,
                    InstallRoot = "game",
                    RelativePath = "gamedata/configs/quick.ltx",
                },
            ],
            QuickReleaseFolders =
            [
                new QuickReleaseFolderDraft
                {
                    SourcePath = quickFolder,
                    InstallRoot = "game",
                    RelativePath = "addons/folder-addon",
                },
            ],
            QuickDeleteFiles =
            [
                new QuickDeleteFileDraft
                {
                    InstallRoot = "game",
                    RelativePath = "gamedata/configs/obsolete.ltx",
                },
            ],
            QuickDeleteFolders =
            [
                new QuickDeleteFolderDraft
                {
                    InstallRoot = "game",
                    RelativePath = "gamedata/scripts/obsolete-addon",
                },
            ],
            PublicationRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [mirror.Id] = targetRoot,
            },
        };

        var result = await ReleasePublicationService.PublishContentBundleAsync(workspace, machine);

        Assert.Equal("2.1.200", result.Version);
        Assert.Equal(5, result.ContentItems);
        Assert.Equal(["selected-addon"], result.PublishedAddonIds);
        Assert.Equal(["existing-addon"], result.PreservedAddonIds);
        Assert.Equal(2, result.AddedFiles);
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(1, result.AddedFolders);
        Assert.Equal(1, result.DeletedFolders);
        Assert.True(news.IsPublished);
        Assert.NotNull(news.PublishedAt);
        Assert.True(information.IsPublished);
        Assert.True(support.IsPublished);
        Assert.True(selectedAddon.IsPublished);
        Assert.False(unselectedDraft.IsPublished);

        await using var manifestStream = File.OpenRead(result.ManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(manifestStream, ManifestJson.Options);
        Assert.NotNull(manifest);
        using (var publicKey = ECDsa.Create())
        {
            publicKey.ImportFromPem(await File.ReadAllTextAsync(publicKeyPath));
            Assert.True(ManifestSecurity.Verify(manifest, publicKey));
        }
        var publishedExisting = Assert.Single(
            manifest.Payload.Content!.Items,
            item => item.Id == "existing-addon");
        Assert.Equal(
            "https://cdn.example/2.1.199/addons/existing-addon/existing-addon.zip",
            Assert.Single(publishedExisting.Download!.Mirrors).Url);
        var publishedSelected = Assert.Single(
            manifest.Payload.Content.Items,
            item => item.Id == "selected-addon");
        Assert.Equal(
            "https://cdn.example/2.1.200/addons/selected-addon/new-addon.zip",
            Assert.Single(publishedSelected.Download!.Mirrors).Url);
        Assert.DoesNotContain(manifest.Payload.Content.Items, item => item.Id == "draft-addon");
        Assert.Single(manifest.Payload.Content.SocialLinks!);
        Assert.Single(manifest.Payload.Content.ProjectPeople!);
        Assert.Single(manifest.Payload.Content.LiveStreams!);
        Assert.NotNull(manifest.Payload.Content.Changelog);

        var quickPackage = Assert.Single(
            manifest.Payload.Packages,
            package => package.Id == "anthology-files-game");
        Assert.Equal(
            ["addons/folder-addon/gamedata/scripts/folder-addon.script", "gamedata/configs/quick.ltx"],
            quickPackage.Files);
        Assert.Equal(["gamedata/configs/obsolete.ltx"], quickPackage.DeletedFiles);
        Assert.Equal(["gamedata/scripts/obsolete-addon"], quickPackage.DeletedDirectories);

        var addonRelative = "addons/selected-addon/new-addon.zip";
        Assert.True(File.Exists(Path.Combine(targetRoot, workspace.Version, addonRelative)));
        Assert.True(File.Exists(Path.Combine(targetRoot, workspace.Version, "content.json")));
        Assert.True(File.Exists(Path.Combine(targetRoot, workspace.Version, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(targetRoot, "manifest.json")));
        Assert.True(Array.IndexOf(result.PublicationFiles.ToArray(), addonRelative)
                    < Array.IndexOf(result.PublicationFiles.ToArray(), "manifest.json"));
        Assert.Equal("content.json", result.PublicationFiles[^2]);
        Assert.Equal("manifest.json", result.PublicationFiles[^1]);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.Combine(outputRoot, workspace.Version), "*.zip", SearchOption.TopDirectoryOnly),
            path => Path.GetFileName(path).StartsWith("anthology-game-", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).StartsWith("anthology-modpack-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreservesSchemaFiveLoosePackagesAndMinimumLauncherVersion()
    {
        var outputRoot = Path.Combine(_root, "schema-five-output");
        var versionRoot = Path.Combine(outputRoot, "2.1.201");
        var keysRoot = Path.Combine(_root, "schema-five-keys");
        Directory.CreateDirectory(versionRoot);
        Directory.CreateDirectory(keysRoot);
        var (privateKeyPath, publicKeyPath) = CreateKeys(keysRoot);
        var looseFile = new PackageLooseFile(
            "gamedata/configs/current.ltx",
            7,
            new string('b', 64));
        var loosePackage = new PackageManifest(
            "anthology-game-loose",
            "Existing loose game",
            "2.1.201",
            PackageKind.Game,
            "game",
            "loose",
            looseFile.Size,
            LoosePackageHash.ComputeSha256([looseFile]),
            [new MirrorManifest("yandex-disk", "https://cdn.example/files/{path}", 10)],
            [],
            PackageUpdateMode.ManagedExact,
            LooseFiles: [looseFile]);
        using (var privateKey = ECDsa.Create())
        {
            privateKey.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
            var existing = ManifestSecurity.Sign(
                new UpdateManifest(
                    5,
                    "next",
                    "2.1.201",
                    DateTimeOffset.UtcNow,
                    "2.1.0-alpha.18",
                    [loosePackage]),
                privateKey,
                "content-bundle-test");
            await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(
                Path.Combine(versionRoot, "manifest.json"),
                existing);
        }
        var workspace = new ReleaserWorkspace
        {
            Version = "2.1.201",
            Channel = "next",
            Content =
            [
                new ContentDraft
                {
                    Id = "about-project",
                    Kind = ContentKind.Information,
                    Title = "About project",
                },
            ],
        };
        var machine = new ReleaserMachineSettings
        {
            OutputRoot = outputRoot,
            PrivateKeyPath = privateKeyPath,
            PublicKeyPath = publicKeyPath,
            KeyId = "content-bundle-test",
        };

        var result = await ReleasePublicationService.PublishContentBundleAsync(workspace, machine);

        await using var stream = File.OpenRead(result.ManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options);
        Assert.NotNull(manifest);
        Assert.Equal(5, manifest.Payload.SchemaVersion);
        Assert.Equal("2.1.0-alpha.18", manifest.Payload.MinimumLauncherVersion);
        var preserved = Assert.Single(manifest.Payload.Packages);
        Assert.Equal(loosePackage.Id, preserved.Id);
        Assert.NotNull(preserved.LooseFiles);
        Assert.Empty(result.Artifacts);
        Assert.Equal(["content.json", "manifest.json"], result.PublicationFiles);
    }

    [Fact]
    public async Task NewVersionInheritsSignedStableSchemaFiveBaseline()
    {
        const string baselineVersion = "2.1.203";
        const string newVersion = "2.1.204";
        const string minimumLauncherVersion = "0.7.0-alpha.18";
        var outputRoot = Path.Combine(_root, "stable-schema-five-output");
        var keysRoot = Path.Combine(_root, "stable-schema-five-keys");
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(keysRoot);
        var (privateKeyPath, publicKeyPath) = CreateKeys(keysRoot);
        var looseFile = new PackageLooseFile(
            "gamedata/configs/current.ltx",
            7,
            new string('c', 64));
        var loosePackage = new PackageManifest(
            "anthology-game-loose",
            "Stable loose game",
            baselineVersion,
            PackageKind.Game,
            "game",
            "loose",
            looseFile.Size,
            LoosePackageHash.ComputeSha256([looseFile]),
            [new MirrorManifest("yandex-disk", "https://cdn.example/files/{path}", 10)],
            [],
            PackageUpdateMode.ManagedExact,
            LooseFiles: [looseFile]);
        using (var privateKey = ECDsa.Create())
        {
            privateKey.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
            var stable = ManifestSecurity.Sign(
                new UpdateManifest(
                    5,
                    "next",
                    baselineVersion,
                    DateTimeOffset.UtcNow,
                    minimumLauncherVersion,
                    [loosePackage]),
                privateKey,
                "stable-schema-five-test");
            await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(
                Path.Combine(outputRoot, "manifest.json"),
                stable);
        }

        var workspace = new ReleaserWorkspace
        {
            Version = newVersion,
            Channel = "next",
            Content =
            [
                new ContentDraft
                {
                    Id = "new-version-news",
                    Kind = ContentKind.News,
                    Title = "New version news",
                },
            ],
        };
        var machine = new ReleaserMachineSettings
        {
            OutputRoot = outputRoot,
            PrivateKeyPath = privateKeyPath,
            PublicKeyPath = publicKeyPath,
            KeyId = "stable-schema-five-test",
        };

        var result = await ReleasePublicationService.PublishContentBundleAsync(workspace, machine);

        Assert.False(File.Exists(Path.Combine(outputRoot, baselineVersion, "manifest.json")));
        await using var stream = File.OpenRead(result.ManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options);
        Assert.NotNull(manifest);
        Assert.Equal(newVersion, manifest.Payload.Version);
        Assert.Equal(5, manifest.Payload.SchemaVersion);
        Assert.Equal(minimumLauncherVersion, manifest.Payload.MinimumLauncherVersion);
        var preserved = Assert.Single(manifest.Payload.Packages);
        Assert.Equal(loosePackage.Id, preserved.Id);
        Assert.Equal(loosePackage.Version, preserved.Version);
        Assert.NotNull(preserved.LooseFiles);
        using var publicKey = ECDsa.Create();
        publicKey.ImportFromPem(await File.ReadAllTextAsync(publicKeyPath));
        Assert.True(ManifestSecurity.Verify(manifest, publicKey));
    }

    [Fact]
    public async Task MissingSelectedAddonDoesNotExposeANewManifest()
    {
        var outputRoot = Path.Combine(_root, "failed-output");
        var targetRoot = Path.Combine(_root, "failed-target");
        var keysRoot = Path.Combine(_root, "failed-keys");
        Directory.CreateDirectory(targetRoot);
        Directory.CreateDirectory(keysRoot);
        var sentinelManifest = Path.Combine(targetRoot, "manifest.json");
        var (privateKeyPath, publicKeyPath) = CreateKeys(keysRoot);
        await WriteStableContentManifestAsync(
            targetRoot,
            privateKeyPath,
            "2.1.201",
            new ContentDocument(
                "existing-news",
                ContentKind.News,
                "news",
                "Existing news",
                string.Empty,
                string.Empty,
                [],
                []));
        var sentinelContents = await File.ReadAllTextAsync(sentinelManifest);
        var mirror = new ReleaseMirrorSet { Id = "target", Provider = "http" };
        var addon = new ContentDraft
        {
            Id = "missing-addon",
            Kind = ContentKind.Mod,
            Title = "Missing addon",
        };
        var workspace = new ReleaserWorkspace
        {
            Version = "2.1.202",
            Mirrors = [mirror],
            Content = [addon],
        };
        var machine = new ReleaserMachineSettings
        {
            OutputRoot = outputRoot,
            PrivateKeyPath = privateKeyPath,
            PublicKeyPath = publicKeyPath,
            KeyId = "content-bundle-test",
            ContentArchivePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [addon.Id] = Path.Combine(_root, "does-not-exist.zip"),
            },
            PublicationRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [mirror.Id] = targetRoot,
            },
        };

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            ReleasePublicationService.PublishContentBundleAsync(workspace, machine));

        Assert.Equal(sentinelContents, await File.ReadAllTextAsync(sentinelManifest));
        Assert.False(File.Exists(Path.Combine(targetRoot, workspace.Version, "manifest.json")));
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
        Directory.Delete(_root, true);
    }

    private static (string PrivateKey, string PublicKey) CreateKeys(string root)
    {
        var privateKey = Path.Combine(root, "private.pem");
        var publicKey = Path.Combine(root, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKey, publicKey);
        return (privateKey, publicKey);
    }

    private static async Task CreateZipAsync(string path, string entryPath, string content)
    {
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryPath);
        await using var stream = entry.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
    }

    private static async Task WriteStableContentManifestAsync(
        string outputRoot,
        string privateKeyPath,
        string version,
        ContentDocument content)
    {
        Directory.CreateDirectory(outputRoot);
        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
        var catalog = new ContentCatalog(4, version, DateTimeOffset.UtcNow, [content]);
        var signed = ManifestSecurity.Sign(
            new UpdateManifest(4, "next", version, DateTimeOffset.UtcNow, null, [], catalog),
            privateKey,
            "content-bundle-test");
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(
            Path.Combine(outputRoot, "manifest.json"),
            signed);
    }
}
