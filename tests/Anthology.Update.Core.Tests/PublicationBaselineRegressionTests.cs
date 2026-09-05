using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthology.Contracts;
using Anthology.Releaser.Core;
using Anthology.Update.Core;

namespace Anthology.Update.Core.Tests;

public sealed class PublicationBaselineRegressionTests : IDisposable
{
    private const string KeyId = "publication-baseline-test";
    private const string BaselineVersion = "2.1.400";
    private const string NewVersion = "2.1.401";
    private const string MinimumLauncherVersion = "0.7.0-alpha.18";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"anthology-publication-baseline-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("content")]
    [InlineData("social")]
    [InlineData("addon")]
    public async Task RefreshPublicationsPreserveSignedSchemaFiveBaseline(string action)
    {
        var outputRoot = Path.Combine(_root, action, "output");
        var keysRoot = Path.Combine(_root, action, "keys");
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(keysRoot);
        var (privateKeyPath, publicKeyPath) = CreateKeys(keysRoot);
        var baselinePackages = await WriteSchemaFiveBaselineAsync(
            Path.Combine(outputRoot, "manifest.json"),
            privateKeyPath);
        var workspace = new ReleaserWorkspace
        {
            Version = NewVersion,
            Channel = "next",
        };
        var machine = CreateMachine(outputRoot, privateKeyPath, publicKeyPath);

        switch (action)
        {
            case "content":
            {
                var content = new ContentDraft
                {
                    Id = "release-news",
                    Kind = ContentKind.News,
                    Title = "Release news",
                };
                workspace.Content = [content];
                await ReleasePublicationService.PublishContentAsync(content, workspace, machine);
                break;
            }
            case "social":
                workspace.SocialLinks =
                [
                    new SocialLinkDraft
                    {
                        Id = "community",
                        Title = "Community",
                        Url = "https://example.test/community",
                        IsVisible = true,
                    },
                ];
                await ReleasePublicationService.PublishSocialLinksAsync(workspace, machine);
                break;
            case "addon":
            {
                var archivePath = Path.Combine(_root, action, "addon.zip");
                await CreateZipAsync(archivePath, "gamedata/scripts/addon.script", "return true");
                var addon = new ContentDraft
                {
                    Id = "baseline-addon",
                    Kind = ContentKind.Mod,
                    Title = "Baseline addon",
                };
                workspace.Content = [addon];
                machine.ContentArchivePaths[addon.Id] = archivePath;
                await ReleasePublicationService.PublishAddonAsync(addon, workspace, machine);
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown publication action: {action}");
        }

        await AssertPreservedSchemaFiveBaselineAsync(
            Path.Combine(outputRoot, NewVersion, "manifest.json"),
            publicKeyPath,
            baselinePackages);
    }

    [Fact]
    public async Task QuickPublicationPreservesSignedSchemaFiveBaseline()
    {
        var outputRoot = Path.Combine(_root, "quick", "output");
        var keysRoot = Path.Combine(_root, "quick", "keys");
        var sourcePath = Path.Combine(_root, "quick", "current.ltx");
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(keysRoot);
        await File.WriteAllTextAsync(sourcePath, "enabled = true");
        var (privateKeyPath, publicKeyPath) = CreateKeys(keysRoot);
        var baselinePackages = await WriteSchemaFiveBaselineAsync(
            Path.Combine(outputRoot, "manifest.json"),
            privateKeyPath);
        var workspace = new ReleaserWorkspace
        {
            Version = NewVersion,
            Channel = "next",
        };
        var machine = CreateMachine(outputRoot, privateKeyPath, publicKeyPath);
        machine.QuickReleaseFiles =
        [
            new QuickReleaseFileDraft
            {
                SourcePath = sourcePath,
                InstallRoot = "game",
                RelativePath = "gamedata/configs/current.ltx",
            },
        ];

        await ReleasePublicationService.PublishQuickFilesAsync(workspace, machine);

        var manifest = await AssertPreservedSchemaFiveBaselineAsync(
            Path.Combine(outputRoot, NewVersion, "manifest.json"),
            publicKeyPath,
            baselinePackages);
        Assert.Contains(manifest.Payload.Packages, package => package.Id == "anthology-files-game");
    }

    [Fact]
    public async Task LauncherPublicationPreservesSignedSchemaFiveBaseline()
    {
        var outputRoot = Path.Combine(_root, "launcher", "output");
        var keysRoot = Path.Combine(_root, "launcher", "keys");
        var gameRoot = Path.Combine(_root, "launcher", "game");
        var launcherRoot = Path.Combine(gameRoot, "AnthologyLauncher");
        var launcherAppRoot = Path.Combine(launcherRoot, "App");
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(keysRoot);
        Directory.CreateDirectory(Path.Combine(launcherAppRoot, "TrustedKeys"));
        var launcherAssembly = Path.Combine(launcherAppRoot, "AnthologyLauncher.Next.dll");
        File.Copy(typeof(GoogleDrivePublisher).Assembly.Location, launcherAssembly);
        await File.WriteAllTextAsync(
            Path.Combine(launcherRoot, "Start-AnthologyLauncherNext.ps1"),
            "Write-Output 'launcher'");
        var (privateKeyPath, publicKeyPath) = CreateKeys(keysRoot);
        var baselinePackages = await WriteSchemaFiveBaselineAsync(
            Path.Combine(outputRoot, "manifest.json"),
            privateKeyPath);
        var workspace = new ReleaserWorkspace
        {
            Version = NewVersion,
            Channel = "next",
        };
        var machine = CreateMachine(outputRoot, privateKeyPath, publicKeyPath);
        machine.GameSourceRoot = gameRoot;

        await ReleasePublicationService.PublishLauncherAsync(workspace, machine);

        var manifest = await AssertPreservedSchemaFiveBaselineAsync(
            Path.Combine(outputRoot, NewVersion, "manifest.json"),
            publicKeyPath,
            baselinePackages);
        Assert.Contains(manifest.Payload.Packages, package => package.Id == "anthology-launcher");
        var versionFiles = Directory.EnumerateFiles(Path.Combine(outputRoot, NewVersion))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.DoesNotContain("launcher-update.json", versionFiles, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            versionFiles,
            file => file!.StartsWith("anthology-launcher-payload-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LauncherBootstrapOverSchemaFourKeepsCurrentChannelVersionAndLegacyShape()
    {
        var outputRoot = Path.Combine(_root, "launcher-bootstrap", "output");
        var versionRoot = Path.Combine(outputRoot, BaselineVersion);
        var keysRoot = Path.Combine(_root, "launcher-bootstrap", "keys");
        var gameRoot = Path.Combine(_root, "launcher-bootstrap", "game");
        var launcherRoot = Path.Combine(gameRoot, "AnthologyLauncher");
        var launcherAppRoot = Path.Combine(launcherRoot, "App");
        Directory.CreateDirectory(versionRoot);
        Directory.CreateDirectory(keysRoot);
        Directory.CreateDirectory(Path.Combine(launcherAppRoot, "TrustedKeys"));
        File.Copy(
            typeof(GoogleDrivePublisher).Assembly.Location,
            Path.Combine(launcherAppRoot, "AnthologyLauncher.Next.dll"));
        await File.WriteAllTextAsync(
            Path.Combine(launcherRoot, "Start-AnthologyLauncherNext.ps1"),
            "Write-Output 'launcher'");
        var (privateKeyPath, publicKeyPath) = CreateKeys(keysRoot);
        var gameArtifact = Path.Combine(versionRoot, $"anthology-game-{BaselineVersion}.zip");
        await CreateZipAsync(gameArtifact, "bin/xrEngine.exe", "engine");
        var gamePackage = new PackageManifest(
            "anthology-game",
            "Current game",
            BaselineVersion,
            PackageKind.Game,
            "game",
            "zip",
            new FileInfo(gameArtifact).Length,
            await ArtifactHash.ComputeSha256Async(gameArtifact),
            [new MirrorManifest("https", $"https://example.test/{Path.GetFileName(gameArtifact)}", 10)],
            ["bin/xrEngine.exe"],
            PackageUpdateMode.ManagedExact,
            true);
        var integrityPackage = new PackageManifest(
            PackageIntegrityCatalogBuilder.PackageId,
            "Existing integrity catalog",
            new string('f', 64),
            PackageKind.Launcher,
            "game",
            "zip",
            97,
            new string('e', 64),
            [new MirrorManifest("https", "https://example.test/existing-integrity.zip", 10)],
            [PackageIntegrityCatalogBuilder.CatalogRelativePath],
            PackageUpdateMode.Merge);
        using (var privateKey = ECDsa.Create())
        {
            privateKey.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
            var baseline = ManifestSecurity.Sign(
                new UpdateManifest(
                    4,
                    "next",
                    BaselineVersion,
                    DateTimeOffset.UtcNow,
                    null,
                    [gamePackage, integrityPackage],
                    new ContentCatalog(4, BaselineVersion, DateTimeOffset.UtcNow, [])),
                privateKey,
                KeyId);
            await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(
                Path.Combine(outputRoot, "manifest.json"),
                baseline);
        }
        File.Delete(gameArtifact);

        var workspace = new ReleaserWorkspace
        {
            // Schema 10 workspaces predate ArtifactUrl. Their Yandex GameUrl and
            // Mo2Url may already have been migrated to direct loose-file roots.
            SchemaVersion = 10,
            Version = BaselineVersion,
            Channel = "next",
            Mirrors =
            [
                new ReleaseMirrorSet
                {
                    Provider = "yandex-disk",
                    Priority = 20,
                    GameUrl = "https://disk.yandex.ru/d/project?path=/AlreadyUploadedGame/{path}",
                    Mo2Url = "https://disk.yandex.ru/d/project?path=/AlreadyUploadedMo2/{path}",
                    ManifestUrl = "https://disk.yandex.ru/d/project?path=/AnthologyUpdateChannel/manifest.json",
                },
            ],
        };
        var machine = CreateMachine(outputRoot, privateKeyPath, publicKeyPath);
        machine.GameSourceRoot = gameRoot;

        var publication = await ReleasePublicationService.PublishLauncherAsync(workspace, machine);

        await using var stream = File.OpenRead(Path.Combine(outputRoot, "manifest.json"));
        var manifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options);
        Assert.NotNull(manifest);
        ManifestValidator.ValidateAndThrow(manifest);
        Assert.Equal(4, manifest.Payload.SchemaVersion);
        Assert.Equal("next", manifest.Payload.Channel);
        Assert.Equal(BaselineVersion, manifest.Payload.Version);
        Assert.Null(manifest.Payload.MinimumLauncherVersion);
        Assert.All(manifest.Payload.Packages, package => Assert.Null(package.LooseFiles));
        var preservedGame = Assert.Single(manifest.Payload.Packages, package => package.Id == gamePackage.Id);
        Assert.Equal(gamePackage.Version, preservedGame.Version);
        Assert.Equal(gamePackage.Sha256, preservedGame.Sha256);
        var preservedIntegrity = Assert.Single(
            manifest.Payload.Packages,
            package => package.Id == PackageIntegrityCatalogBuilder.PackageId);
        Assert.Equal(integrityPackage.Version, preservedIntegrity.Version);
        Assert.Equal(integrityPackage.Sha256, preservedIntegrity.Sha256);
        Assert.Equal(integrityPackage.Size, preservedIntegrity.Size);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.Combine(outputRoot, BaselineVersion)).Select(Path.GetFileName),
            file => file!.StartsWith("anthology-integrity-", StringComparison.OrdinalIgnoreCase));
        var launcherPackage = Assert.Single(
            manifest.Payload.Packages,
            package => package.Id == "anthology-launcher");
        var launcherMirror = Assert.Single(launcherPackage.Mirrors);
        Assert.DoesNotContain("{path}", launcherMirror.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"path=/AnthologyUpdateChannel/{BaselineVersion}/{Path.GetFileName(publication.ArtifactPath)}",
            launcherMirror.Url,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(outputRoot, ReleaseHistoryCatalogBuilder.FileName)));
        using var publicKey = ECDsa.Create();
        publicKey.ImportFromPem(await File.ReadAllTextAsync(publicKeyPath));
        Assert.True(ManifestSecurity.Verify(manifest, publicKey));
    }

    [Fact]
    public async Task PublicationCanRecoverBaselineFromConfiguredLocalMirror()
    {
        var outputRoot = Path.Combine(_root, "mirror-fallback", "output");
        var targetRoot = Path.Combine(_root, "mirror-fallback", "target");
        var keysRoot = Path.Combine(_root, "mirror-fallback", "keys");
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(targetRoot);
        Directory.CreateDirectory(keysRoot);
        var (privateKeyPath, publicKeyPath) = CreateKeys(keysRoot);
        var baselinePackages = await WriteSchemaFiveBaselineAsync(
            Path.Combine(targetRoot, "manifest.json"),
            privateKeyPath);
        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, "manifest.json"),
            "{ invalid higher-priority stable manifest");
        var mirror = new ReleaseMirrorSet
        {
            Id = "local-yandex",
            Provider = "yandex-disk",
        };
        var workspace = new ReleaserWorkspace
        {
            Version = NewVersion,
            Channel = "next",
            Mirrors = [mirror],
        };
        var machine = CreateMachine(outputRoot, privateKeyPath, publicKeyPath);
        machine.PublicationRoots[mirror.Id] = targetRoot;

        await ReleasePublicationService.PublishSocialLinksAsync(workspace, machine);

        await AssertPreservedSchemaFiveBaselineAsync(
            Path.Combine(outputRoot, NewVersion, "manifest.json"),
            publicKeyPath,
            baselinePackages);
    }

    [Fact]
    public async Task FullArchiveBuildInheritsVerifiedLauncherSchemaAndMinimumFromStableBaseline()
    {
        var outputRoot = Path.Combine(_root, "full-build", "output");
        var keysRoot = Path.Combine(_root, "full-build", "keys");
        var gameRoot = Path.Combine(_root, "full-build", "game");
        var mo2Root = Path.Combine(_root, "full-build", "mo2");
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(keysRoot);
        Directory.CreateDirectory(Path.Combine(gameRoot, "bin"));
        Directory.CreateDirectory(Path.Combine(mo2Root, "mods"));
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "bin", "xrEngine.exe"), "engine");
        await File.WriteAllTextAsync(Path.Combine(mo2Root, "ModOrganizer.exe"), "mo2");
        await File.WriteAllTextAsync(Path.Combine(mo2Root, "mods", "base.txt"), "base mod");
        var (privateKeyPath, publicKeyPath) = CreateKeys(keysRoot);
        var baselinePackages = await WriteSchemaFiveBaselineAsync(
            Path.Combine(outputRoot, "manifest.json"),
            privateKeyPath,
            includeLauncher: true,
            includeIndependentPackages: true);
        var workspace = new ReleaserWorkspace
        {
            Version = NewVersion,
            Channel = "next",
        };
        var machine = CreateMachine(outputRoot, privateKeyPath, publicKeyPath);
        machine.GameSourceRoot = gameRoot;
        machine.Mo2SourceRoot = mo2Root;

        var result = await UnifiedReleaseBuilder.BuildAsync(new UnifiedReleaseRequest(
            workspace,
            machine,
            MinimumLauncherVersion: "0.6.0-alpha.1"));

        await using var stream = File.OpenRead(result.ManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options);
        Assert.NotNull(manifest);
        Assert.Equal(5, manifest.Payload.SchemaVersion);
        Assert.Equal(MinimumLauncherVersion, manifest.Payload.MinimumLauncherVersion);
        var baselineLauncher = Assert.Single(baselinePackages, package => package.Kind == PackageKind.Launcher);
        var preservedLauncher = Assert.Single(manifest.Payload.Packages, package => package.Id == baselineLauncher.Id);
        Assert.Equal(baselineLauncher.Version, preservedLauncher.Version);
        Assert.Equal(baselineLauncher.Sha256, preservedLauncher.Sha256);
        Assert.Equal(baselineLauncher.Size, preservedLauncher.Size);
        Assert.Equal(
            Assert.Single(baselineLauncher.Mirrors).Url,
            Assert.Single(preservedLauncher.Mirrors).Url);
        var independentTool = Assert.Single(baselinePackages, package => package.Id == "independent-tool");
        var preservedTool = Assert.Single(manifest.Payload.Packages, package => package.Id == independentTool.Id);
        Assert.Equal(independentTool.Version, preservedTool.Version);
        Assert.Equal(independentTool.Sha256, preservedTool.Sha256);
        Assert.DoesNotContain(manifest.Payload.Packages, package => package.Id == "anthology-files-game");
        Assert.Contains(
            manifest.Payload.Packages,
            package => package.Id == "anthology-game"
                       && package.Version == NewVersion
                       && package.LooseFiles is null);
        Assert.Contains(
            manifest.Payload.Packages,
            package => package.Id == "anthology-mo2"
                       && package.Version == NewVersion
                       && package.LooseFiles is null);
        using var publicKey = ECDsa.Create();
        publicKey.ImportFromPem(await File.ReadAllTextAsync(publicKeyPath));
        Assert.True(ManifestSecurity.Verify(manifest, publicKey));
    }

    private static async Task<IReadOnlyList<PackageManifest>> WriteSchemaFiveBaselineAsync(
        string manifestPath,
        string privateKeyPath,
        bool includeLauncher = false,
        bool includeIndependentPackages = false)
    {
        var gameFile = new PackageLooseFile(
            "gamedata/configs/game.ltx",
            11,
            new string('a', 64));
        var mo2File = new PackageLooseFile(
            "mods/base/meta.ini",
            13,
            new string('b', 64));
        var packages = new List<PackageManifest>
        {
            CreateLoosePackage(
                "anthology-game",
                PackageKind.Game,
                "game",
                gameFile),
            CreateLoosePackage(
                "anthology-mo2",
                PackageKind.Modpack,
                "modpack",
                mo2File),
        };
        if (includeLauncher)
        {
            packages.Add(new PackageManifest(
                "anthology-launcher",
                "Anthology Launcher",
                MinimumLauncherVersion,
                PackageKind.Launcher,
                "launcher",
                "zip",
                17,
                new string('c', 64),
                [new MirrorManifest("yandex-disk", "https://example.test/launcher.zip", 10)],
                ["AnthologyLauncher.Next.exe"]));
        }
        if (includeIndependentPackages)
        {
            var toolFile = new PackageLooseFile(
                "tools/independent-helper.dll",
                23,
                new string('d', 64));
            packages.Add(CreateLoosePackage(
                "independent-tool",
                PackageKind.Tool,
                "game",
                toolFile));
            packages.Add(new PackageManifest(
                "anthology-files-game",
                "Absorbed quick patch",
                BaselineVersion,
                PackageKind.Game,
                "game",
                "zip",
                29,
                new string('e', 64),
                [new MirrorManifest("yandex-disk", "https://example.test/quick.zip", 10)],
                ["gamedata/configs/absorbed.ltx"]));
        }
        var catalog = new ContentCatalog(
            4,
            BaselineVersion,
            DateTimeOffset.UtcNow,
            [new ContentDocument("baseline-news", ContentKind.News, "news", "Baseline", string.Empty, string.Empty, [], [])]);
        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
        var signed = ManifestSecurity.Sign(
            new UpdateManifest(
                5,
                "next",
                BaselineVersion,
                DateTimeOffset.UtcNow,
                MinimumLauncherVersion,
                packages,
                catalog),
            privateKey,
            KeyId);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(manifestPath, signed);
        return packages;
    }

    private static PackageManifest CreateLoosePackage(
        string id,
        PackageKind kind,
        string installRoot,
        PackageLooseFile file) =>
        new(
            id,
            id,
            BaselineVersion,
            kind,
            installRoot,
            "loose",
            file.Size,
            LoosePackageHash.ComputeSha256([file]),
            [new MirrorManifest("yandex-disk", $"https://example.test/{id}/{{path}}", 10)],
            [],
            PackageUpdateMode.ManagedExact,
            LooseFiles: [file]);

    private static async Task<SignedUpdateManifest> AssertPreservedSchemaFiveBaselineAsync(
        string manifestPath,
        string publicKeyPath,
        IReadOnlyList<PackageManifest> baselinePackages)
    {
        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options);
        Assert.NotNull(manifest);
        Assert.Equal(5, manifest.Payload.SchemaVersion);
        Assert.Equal(NewVersion, manifest.Payload.Version);
        Assert.Equal(MinimumLauncherVersion, manifest.Payload.MinimumLauncherVersion);
        foreach (var baselinePackage in baselinePackages)
        {
            var preserved = Assert.Single(
                manifest.Payload.Packages,
                package => package.Id == baselinePackage.Id);
            Assert.Equal(baselinePackage.Version, preserved.Version);
            Assert.NotNull(preserved.LooseFiles);
        }

        using var publicKey = ECDsa.Create();
        publicKey.ImportFromPem(await File.ReadAllTextAsync(publicKeyPath));
        Assert.True(ManifestSecurity.Verify(manifest, publicKey));
        return manifest;
    }

    private static ReleaserMachineSettings CreateMachine(
        string outputRoot,
        string privateKeyPath,
        string publicKeyPath) =>
        new()
        {
            OutputRoot = outputRoot,
            PrivateKeyPath = privateKeyPath,
            PublicKeyPath = publicKeyPath,
            KeyId = KeyId,
        };

    private static (string PrivateKeyPath, string PublicKeyPath) CreateKeys(string root)
    {
        Directory.CreateDirectory(root);
        var privateKeyPath = Path.Combine(root, "private.pem");
        var publicKeyPath = Path.Combine(root, "public.pem");
        UnifiedReleaseBuilder.GenerateKeys(privateKeyPath, publicKeyPath);
        return (privateKeyPath, publicKeyPath);
    }

    private static async Task CreateZipAsync(string path, string entryPath, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryPath);
        await using var stream = entry.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
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
}
