using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.IO.Compression;
using System.Diagnostics;
using Anthology.Contracts;
using Anthology.Update.Core;

namespace Anthology.Releaser.Core;

public static partial class ReleasePublicationService
{
    private static readonly HashSet<string> SupportedArchives = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar",
    };

    public static Task<PublicationResult> PublishReleaseAsync(
        UnifiedReleaseResult release,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        PublishReleaseAsync(
            release,
            workspace,
            machine,
            googleDrivePublisher: null,
            progress,
            cancellationToken);

    public static async Task<PublicationResult> PublishReleaseAsync(
        UnifiedReleaseResult release,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        GoogleDrivePublisher? googleDrivePublisher,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        var versionRoot = Path.GetDirectoryName(Path.GetFullPath(release.ManifestPath))
                          ?? throw new InvalidOperationException("Не удалось определить папку собранного релиза.");
        var releaseFiles = release.PublicationFiles is { Count: > 0 }
            ? release.PublicationFiles
            : Directory.EnumerateFiles(versionRoot, "*", SearchOption.AllDirectories).ToArray();
        var relativeFiles = releaseFiles
            .Select(path => Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(versionRoot, path)))
            .Select(path => PathSafety.NormalizeRelativePath(Path.GetRelativePath(versionRoot, path)))
            .Where(path => !path.Equals("release-workspace.json", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.StartsWith(".releaser-trash/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return await PublishFilesAsync(
            versionRoot,
            relativeFiles,
            workspace,
            machine,
            progress,
            googleDrivePublisher,
            cancellationToken);
    }

    public static async Task<LauncherPublicationResult> PublishLauncherAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(machine);
        ReleaseVersionRules.Validate(workspace.Version);
        UnifiedReleaseBuilder.ValidateMachine(machine);
        if (string.IsNullOrWhiteSpace(machine.GameSourceRoot))
        {
            throw new InvalidOperationException("Выберите корень игры с установленным Launcher Next.");
        }

        var gameRoot = Path.GetFullPath(machine.GameSourceRoot);
        var launcherRoot = Path.Combine(gameRoot, "AnthologyLauncher");
        var channelPreparation = await LauncherUpdateConfigurationPublisher.PrepareAsync(
            workspace,
            machine,
            progress,
            cancellationToken);
        var launcherAssembly = Path.Combine(launcherRoot, "App", "AnthologyLauncher.Next.dll");
        var startScript = Path.Combine(launcherRoot, "Start-AnthologyLauncherNext.ps1");
        if (!File.Exists(launcherAssembly) || !File.Exists(startScript))
        {
            throw new FileNotFoundException("В выбранном корне не найден полный Launcher Next или его стартовый скрипт.", launcherRoot);
        }

        var launcherFiles = EnumerateLauncherUpdateFiles(launcherRoot)
            .Select(path => new QuickReleaseFileDraft
            {
                SourcePath = path,
                InstallRoot = "game",
                RelativePath = PathSafety.NormalizeRelativePath(Path.GetRelativePath(launcherRoot, path)),
            })
            .ToArray();
        if (launcherFiles.Length == 0)
        {
            throw new InvalidDataException("В Launcher Next не найдено файлов приложения для публикации.");
        }

        var launcherVersion = ResolveLauncherVersion(launcherAssembly);
        var versionRoot = Path.Combine(Path.GetFullPath(machine.OutputRoot), workspace.Version.Trim());
        Directory.CreateDirectory(versionRoot);
        var safeLauncherVersion = Regex.Replace(launcherVersion, "[^a-zA-Z0-9._-]", "-");
        var payloadName = $"anthology-launcher-payload-{safeLauncherVersion}.zip";
        var payloadPath = Path.Combine(versionRoot, payloadName);
        progress?.Report($"Упаковка Launcher Next {launcherVersion}: {launcherFiles.Length:N0} файлов…");
        await CreateMappedArchiveAsync(payloadPath, launcherFiles, cancellationToken);
        var payloadHash = await ArtifactHash.ComputeSha256Async(payloadPath, cancellationToken);

        var descriptorName = "launcher-update.json";
        var descriptorPath = Path.Combine(versionRoot, descriptorName);
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(
            descriptorPath,
            new LauncherPendingUpdate(1, launcherVersion, workspace.Version.Trim(), payloadName, payloadHash),
            cancellationToken);

        var pendingBase = "AnthologyLauncher/Update/LauncherPending";
        var deliveredFiles = new List<QuickReleaseFileDraft>
        {
            new QuickReleaseFileDraft
            {
                SourcePath = payloadPath,
                InstallRoot = "game",
                RelativePath = $"{pendingBase}/{payloadName}",
            },
            new QuickReleaseFileDraft
            {
                SourcePath = descriptorPath,
                InstallRoot = "game",
                RelativePath = $"{pendingBase}/{descriptorName}",
            },
            new QuickReleaseFileDraft
            {
                SourcePath = startScript,
                InstallRoot = "game",
                RelativePath = "AnthologyLauncher/Start-AnthologyLauncherNext.ps1",
            },
        };
        if (!string.IsNullOrWhiteSpace(channelPreparation.DescriptorPath)
            && File.Exists(channelPreparation.DescriptorPath))
        {
            deliveredFiles.Add(new QuickReleaseFileDraft
            {
                SourcePath = channelPreparation.DescriptorPath,
                InstallRoot = "game",
                RelativePath = "AnthologyLauncher/Update/channel.json",
            });
        }
        var provisionalDeliveryPath = Path.Combine(
            versionRoot,
            $".anthology-launcher-{safeLauncherVersion}-{Guid.NewGuid():N}.zip");
        string deliveryHash;
        string deliveryName;
        string deliveryPath;
        try
        {
            await CreateMappedArchiveAsync(provisionalDeliveryPath, deliveredFiles, cancellationToken);
            deliveryHash = await ArtifactHash.ComputeSha256Async(provisionalDeliveryPath, cancellationToken);
            deliveryName = $"anthology-launcher-{safeLauncherVersion}-{deliveryHash[..16]}.zip";
            deliveryPath = Path.Combine(versionRoot, deliveryName);
            if (File.Exists(deliveryPath))
            {
                var existingHash = await ArtifactHash.ComputeSha256Async(deliveryPath, cancellationToken);
                if (!string.Equals(existingHash, deliveryHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Launcher artifact hash-prefix collision for '{deliveryName}'. Existing immutable artifact was preserved.");
                }

                File.Delete(provisionalDeliveryPath);
            }
            else
            {
                File.Move(provisionalDeliveryPath, deliveryPath);
            }
        }
        catch
        {
            try
            {
                File.Delete(provisionalDeliveryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup must not hide the publication failure.
            }
            throw;
        }

        DeleteFileBestEffort(payloadPath);
        DeleteFileBestEffort(descriptorPath);

        var mirrors = workspace.Mirrors
            .Select(mirror => new
            {
                Mirror = mirror,
                Url = UnifiedReleaseBuilder.ResolveArtifactUrlTemplate(mirror, workspace),
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Url))
            .Select(mirror => new MirrorManifest(
                UnifiedReleaseBuilder.NormalizeProvider(mirror.Mirror.Provider),
                UnifiedReleaseBuilder.ExpandUrl(mirror.Url, workspace.Version, "anthology-launcher", deliveryName),
                mirror.Mirror.Priority))
            .OrderBy(item => item.Priority)
            .ToArray();
        if (mirrors.Length == 0)
        {
            mirrors = [new MirrorManifest("local-file", new Uri(deliveryPath).AbsoluteUri, 1000)];
        }

        var manifestPath = Path.Combine(versionRoot, "manifest.json");
        var baselineManifest = await PublicationManifestBaseline.LoadAsync(
            workspace,
            machine,
            cancellationToken);
        var packages = (baselineManifest?.Payload.Packages ?? [])
            .Where(package => !string.Equals(
                package.Id,
                "anthology-launcher",
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        var launcherPackage = new PackageManifest(
            "anthology-launcher",
            $"A.N.T.H.O.L.O.G.Y Launcher {launcherVersion}",
            launcherVersion,
            PackageKind.Launcher,
            "game",
            "zip",
            new FileInfo(deliveryPath).Length,
            deliveryHash,
            mirrors,
            deliveredFiles.Select(file => file.RelativePath).Order(StringComparer.Ordinal).ToArray(),
            PackageUpdateMode.Merge);
        packages.Add(launcherPackage);

        // A launcher-only publication must never erase or rewrite the already
        // published news/library/information catalog. Reuse the signed catalog
        // from this game version; only a content/release action may replace it.
        var media = PreparedContentMedia.Empty;
        var catalog = baselineManifest?.Payload.Content;
        if (catalog is null)
        {
            media = await ContentMediaPublisher.PrepareAsync(workspace, machine, versionRoot, progress, cancellationToken);
            catalog = UnifiedReleaseBuilder.CreateContentCatalog(workspace, media);
        }
        else
        {
            // Keep every already-published document and media URL, but allow each
            // launcher release to carry its own release notes and current version.
            var releaseNotes = UnifiedReleaseBuilder.CreateContentCatalog(workspace).Changelog;
            catalog = catalog with
            {
                Version = workspace.Version.Trim(),
                PublishedAt = DateTimeOffset.UtcNow,
                Changelog = releaseNotes ?? catalog.Changelog,
            };
        }
        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(await File.ReadAllTextAsync(Path.GetFullPath(machine.PrivateKeyPath), cancellationToken));
        // Publishing only the launcher must not rebuild integrity metadata for
        // already-published game or MO2 archives. Those archives can be hosted
        // exclusively by a remote mirror and intentionally absent locally.
        // Preserve the signed baseline integrity package just like every other
        // unrelated package; content/quick/full publication owns its rebuild.

        var manifestShape = PublicationManifestBaseline.ResolveShape(baselineManifest, packages);
        var publishedAt = DateTimeOffset.UtcNow;
        var channel = string.IsNullOrWhiteSpace(workspace.Channel)
            ? "next"
            : workspace.Channel.Trim().ToLowerInvariant();
        var updateManifest = new UpdateManifest(
            manifestShape.SchemaVersion,
            channel,
            workspace.Version.Trim(),
            publishedAt,
            manifestShape.MinimumLauncherVersion,
            packages,
            catalog);
        var signed = ManifestSecurity.Sign(updateManifest, privateKey, machine.KeyId.Trim());
        ManifestValidator.ValidateAndThrow(signed);
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(manifestPath, signed, cancellationToken);
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(Path.Combine(versionRoot, "content.json"), catalog, cancellationToken);

        string? compatibilityBootstrapManifestPath = null;
        if (ReleaseChannelLayout.UsesDedicatedStableChannel(workspace))
        {
            // Old launchers only understand schema 4. Keep the root channel tiny:
            // it delivers the modern launcher (whose embedded channel.json points
            // at the dedicated schema 5 channel) and never advertises huge legacy
            // game/MO2 archives that an alpha-17 client could otherwise download.
            var compatibilityBootstrap = ManifestSecurity.Sign(
                new UpdateManifest(
                    4,
                    channel,
                    workspace.Version.Trim(),
                    publishedAt,
                    null,
                    [launcherPackage],
                    catalog),
                privateKey,
                machine.KeyId.Trim());
            ManifestValidator.ValidateAndThrow(compatibilityBootstrap);
            compatibilityBootstrapManifestPath = Path.Combine(
                versionRoot,
                $".schema4-launcher-bootstrap-{Guid.NewGuid():N}.json");
            await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(
                compatibilityBootstrapManifestPath,
                compatibilityBootstrap,
                cancellationToken);
        }

        var relativeFiles = new List<string> { deliveryName, "manifest.json", "content.json" };
        relativeFiles.AddRange(media.RelativeFiles);
        PublicationResult publication;
        try
        {
            publication = await PublishFilesAsync(
                versionRoot,
                relativeFiles,
                workspace,
                machine,
                progress: progress,
                googleDrivePublisher: null,
                cancellationToken: cancellationToken,
                compatibilityBootstrapManifestPath: compatibilityBootstrapManifestPath);
        }
        finally
        {
            if (compatibilityBootstrapManifestPath is not null)
            {
                DeleteFileBestEffort(compatibilityBootstrapManifestPath);
            }
        }
        progress?.Report($"Launcher Next {launcherVersion} опубликован. Он применится до следующего запуска приложения.");
        return new LauncherPublicationResult(launcherVersion, deliveryPath, manifestPath, launcherFiles.Length, publication);
    }

    public static async Task<AddonPublicationResult> PublishAddonAsync(
        ContentDraft addon,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addon);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(machine);
        ReleaseVersionRules.Validate(workspace.Version);
        UnifiedReleaseBuilder.ValidateMachine(machine);
        if (addon.Kind != ContentKind.Mod)
        {
            throw new InvalidOperationException("Публикация файла доступна только для материала типа «Мод для библиотеки».");
        }

        var id = NormalizeId(addon.Id);
        if (!machine.ContentArchivePaths.TryGetValue(addon.Id, out var selectedArchive)
            && !machine.ContentArchivePaths.TryGetValue(id, out selectedArchive))
        {
            throw new FileNotFoundException("Сначала выберите архив аддона.");
        }

        var source = Path.GetFullPath(selectedArchive);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Выбранный архив аддона больше не существует.", source);
        }

        if (!SupportedArchives.Contains(Path.GetExtension(source)))
        {
            throw new InvalidDataException("Аддон должен быть готовым архивом ZIP, 7Z или RAR.");
        }

        var fileName = Path.GetFileName(source);
        var versionRoot = Path.Combine(Path.GetFullPath(machine.OutputRoot), workspace.Version.Trim());
        var relativeArtifact = Path.Combine("addons", id, fileName);
        var artifact = Path.Combine(versionRoot, relativeArtifact);
        progress?.Report($"Копирование аддона {addon.Title}…");
        await CopyFileAtomicallyAsync(source, artifact, cancellationToken);

        addon.DownloadFileName = fileName;
        addon.DownloadSize = new FileInfo(artifact).Length;
        addon.DownloadSha256 = await ArtifactHash.ComputeSha256Async(artifact, cancellationToken);
        addon.IsPublished = true;
        if (string.IsNullOrWhiteSpace(addon.DownloadMirrors))
        {
            addon.DownloadMirrors = string.Join(Environment.NewLine, workspace.Mirrors
                .Where(mirror => !string.IsNullOrWhiteSpace(mirror.ContentUrl))
                .OrderBy(mirror => mirror.Priority)
                .Select(mirror => $"{UnifiedReleaseBuilder.NormalizeProvider(mirror.Provider)} | {mirror.ContentUrl.Trim()}"));
        }

        if (string.IsNullOrWhiteSpace(addon.DownloadMirrors))
        {
            addon.DownloadMirrors = $"local-file | {new Uri(artifact).AbsoluteUri}";
        }

        var refresh = await RefreshManifestAsync(workspace, machine, progress, cancellationToken);
        var relativeFiles = new List<string>
        {
            relativeArtifact,
            "manifest.json",
            "content.json",
        };
        relativeFiles.AddRange(refresh.MediaFiles);
        var publication = await PublishFilesAsync(versionRoot, relativeFiles, workspace, machine, progress, cancellationToken);
        progress?.Report($"Аддон {addon.Title} выпущен.");
        return new AddonPublicationResult(id, artifact, refresh.ManifestPath, publication);
    }

    public static async Task<PublicationResult> PublishContentAsync(
        ContentDraft content,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(machine);
        if (content.Kind == ContentKind.Mod)
        {
            throw new InvalidOperationException("Для модов используйте публикацию пакета Библиотеки модов.");
        }

        ReleaseVersionRules.Validate(workspace.Version);
        UnifiedReleaseBuilder.ValidateMachine(machine);
        _ = NormalizeId(content.Id);
        if (content.Kind == ContentKind.News && content.PublishedAt is null)
        {
            content.PublishedAt = DateTimeOffset.UtcNow;
        }
        content.IsPublished = true;
        progress?.Report($"Публикация материала {content.Title}…");
        var versionRoot = Path.Combine(Path.GetFullPath(machine.OutputRoot), workspace.Version.Trim());
        var refresh = await RefreshManifestAsync(workspace, machine, progress, cancellationToken);
        var relativeFiles = new List<string> { "manifest.json", "content.json" };
        relativeFiles.AddRange(refresh.MediaFiles);
        var publication = await PublishFilesAsync(
            versionRoot,
            relativeFiles,
            workspace,
            machine,
            progress,
            cancellationToken);
        progress?.Report($"Материал {content.Title} опубликован.");
        return publication;
    }

    public static async Task<PublicationResult> PublishSocialLinksAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(machine);
        ReleaseVersionRules.Validate(workspace.Version);
        UnifiedReleaseBuilder.ValidateMachine(machine);
        progress?.Report("Публикация ссылок главного экрана…");
        var versionRoot = Path.Combine(Path.GetFullPath(machine.OutputRoot), workspace.Version.Trim());
        var refresh = await RefreshManifestAsync(workspace, machine, progress, cancellationToken);
        var relativeFiles = new List<string> { "manifest.json", "content.json" };
        relativeFiles.AddRange(refresh.MediaFiles);
        var publication = await PublishFilesAsync(
            versionRoot,
            relativeFiles,
            workspace,
            machine,
            progress,
            cancellationToken);
        progress?.Report("Ссылки главного экрана опубликованы.");
        return publication;
    }

    /// <summary>
    /// Publishes every editable content surface plus the selected addon archives
    /// and quick file changes as one version, without building the full game or
    /// MO2 packages. Payloads are staged first and the signed manifest is exposed
    /// last, so a mirror never advertises a newly selected artifact before that
    /// artifact has been copied to it.
    /// </summary>
    public static async Task<ContentBundlePublicationResult> PublishContentBundleAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(machine);
        ReleaseVersionRules.Validate(workspace.Version);
        UnifiedReleaseBuilder.ValidateMachine(machine);

        var version = workspace.Version.Trim();
        var versionRoot = Path.Combine(Path.GetFullPath(machine.OutputRoot), version);
        Directory.CreateDirectory(versionRoot);
        var manifestPath = Path.Combine(versionRoot, "manifest.json");
        var contentPath = Path.Combine(versionRoot, "content.json");
        var baselineManifest = await PublicationManifestBaseline.LoadAsync(
            workspace,
            machine,
            cancellationToken);
        var addonBaselineManifest = baselineManifest;
        var existingPackages = baselineManifest?.Payload.Packages ?? [];

        var content = workspace.Content ?? [];
        var publishedAt = DateTimeOffset.UtcNow;
        foreach (var document in content.Where(document => document.Kind != ContentKind.Mod))
        {
            _ = NormalizeId(document.Id);
            document.IsPublished = true;
            if (document.Kind == ContentKind.News && document.PublishedAt is null)
            {
                document.PublishedAt = publishedAt;
            }
        }

        var artifactPaths = new List<string>();
        var payloadRelativeFiles = new List<string>();
        var publishedAddonIds = new List<string>();
        var preservedAddonIds = new List<string>();
        foreach (var addon in content.Where(document => document.Kind == ContentKind.Mod))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasSelectedArchive = TryGetSelectedAddonArchive(
                machine,
                addon.Id,
                addon.Id.Trim().ToLowerInvariant(),
                out var selectedArchive);
            if (!addon.IsPublished && !hasSelectedArchive)
            {
                continue;
            }

            var id = NormalizeId(addon.Id);
            if (hasSelectedArchive)
            {
                var prepared = await PrepareAddonArtifactAsync(
                    addon,
                    id,
                    selectedArchive,
                    workspace,
                    versionRoot,
                    progress,
                    cancellationToken);
                artifactPaths.Add(prepared.ArtifactPath);
                payloadRelativeFiles.Add(prepared.RelativePath);
                publishedAddonIds.Add(id);
                continue;
            }

            if (!addon.IsPublished)
            {
                continue;
            }

            RestoreAddonDownloadFromExistingManifest(addon, addonBaselineManifest);
            preservedAddonIds.Add(id);
            if (TryResolveLocalAddonArtifact(versionRoot, addon, id, out var localArtifact, out var relativeArtifact))
            {
                artifactPaths.Add(localArtifact);
                payloadRelativeFiles.Add(relativeArtifact);
            }
        }

        var quick = PrepareQuickSelection(machine, allowEmpty: true);
        var hasQuickChanges = quick.Additions.Count > 0
                              || quick.Deletions.Count > 0
                              || quick.DirectoryDeletions.Count > 0;
        var changedInstallRoots = quick.Additions.Select(item => item.InstallRoot)
            .Concat(quick.Deletions.Select(item => item.InstallRoot))
            .Concat(quick.DirectoryDeletions.Select(item => item.InstallRoot))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packages = existingPackages
            .Where(package => !hasQuickChanges
                              || !string.Equals(
                                  package.Id,
                                  PackageIntegrityCatalogBuilder.PackageId,
                                  StringComparison.OrdinalIgnoreCase))
            .Where(package => !package.Id.StartsWith("anthology-files-", StringComparison.OrdinalIgnoreCase)
                              || !changedInstallRoots.Contains(package.InstallRoot))
            .ToList();

        foreach (var installRoot in new[] { "game", "modpack" })
        {
            var rootAdditions = quick.Additions.Where(item => item.InstallRoot == installRoot).ToArray();
            var addedPaths = rootAdditions.Select(item => item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rootDeletions = quick.Deletions
                .Where(item => item.InstallRoot == installRoot)
                .Select(item => item.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Except(addedPaths, StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var rootDirectoryDeletions = quick.DirectoryDeletions
                .Where(item => item.InstallRoot == installRoot)
                .Select(item => item.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (rootAdditions.Length == 0 && rootDeletions.Length == 0 && rootDirectoryDeletions.Length == 0)
            {
                continue;
            }

            var packageId = $"anthology-files-{installRoot}";
            var artifactName = $"{packageId}-{version}.zip";
            var artifactPath = Path.Combine(versionRoot, artifactName);
            progress?.Report($"Упаковка выбранных файлов: {installRoot}…");
            await CreateMappedArchiveAsync(artifactPath, rootAdditions, cancellationToken);
            var hash = await ArtifactHash.ComputeSha256Async(artifactPath, cancellationToken);
            var artifactSize = new FileInfo(artifactPath).Length;
            var mirrors = workspace.Mirrors
                .Select(mirror => new
                {
                    Mirror = mirror,
                    Url = UnifiedReleaseBuilder.ResolveArtifactUrlTemplate(mirror, workspace),
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Url))
                .Where(item => UnifiedReleaseBuilder.SupportsArtifact(item.Mirror.Provider, artifactSize))
                .Select(item => new MirrorManifest(
                    UnifiedReleaseBuilder.NormalizeProvider(item.Mirror.Provider),
                    UnifiedReleaseBuilder.ExpandUrl(item.Url, version, packageId, artifactName),
                    item.Mirror.Priority))
                .OrderBy(item => item.Priority)
                .ToArray();
            if (mirrors.Length == 0)
            {
                mirrors = [new MirrorManifest("local-file", new Uri(artifactPath).AbsoluteUri, 1000)];
            }

            packages.Add(new PackageManifest(
                packageId,
                installRoot == "game" ? "Выбранные файлы Anthology" : "Выбранные файлы Mod Organizer 2",
                version,
                installRoot == "game" ? PackageKind.Game : PackageKind.Modpack,
                installRoot,
                "zip",
                artifactSize,
                hash,
                mirrors,
                rootAdditions.Select(item => item.RelativePath).Order(StringComparer.Ordinal).ToArray(),
                PackageUpdateMode.Merge,
                false,
                null,
                rootDeletions,
                rootDirectoryDeletions));
            artifactPaths.Add(artifactPath);
            payloadRelativeFiles.Add(artifactName);
        }

        progress?.Report("Подготовка новостей, информации и медиа…");
        var media = await ContentMediaPublisher.PrepareAsync(
            workspace,
            machine,
            versionRoot,
            progress,
            cancellationToken);
        var catalog = UnifiedReleaseBuilder.CreateContentCatalog(workspace, media);

        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(await File.ReadAllTextAsync(Path.GetFullPath(machine.PrivateKeyPath), cancellationToken));
        if (hasQuickChanges)
        {
            var integrity = await PackageIntegrityCatalogBuilder.BuildAsync(
                packages,
                versionRoot,
                workspace,
                privateKey,
                machine.KeyId.Trim(),
                progress,
                cancellationToken);
            if (integrity is not null)
            {
                packages.Add(integrity.Package);
                artifactPaths.Add(integrity.ArtifactPath);
                payloadRelativeFiles.Add(PathSafety.NormalizeRelativePath(
                    Path.GetRelativePath(versionRoot, integrity.ArtifactPath)));
            }
        }

        var manifestShape = PublicationManifestBaseline.ResolveShape(baselineManifest, packages);
        var payload = new UpdateManifest(
            manifestShape.SchemaVersion,
            string.IsNullOrWhiteSpace(workspace.Channel) ? "next" : workspace.Channel.Trim().ToLowerInvariant(),
            version,
            publishedAt,
            manifestShape.MinimumLauncherVersion,
            packages,
            catalog);
        var signed = ManifestSecurity.Sign(payload, privateKey, machine.KeyId.Trim());
        ManifestValidator.ValidateAndThrow(signed);

        // content.json is written before manifest.json. This also matters when
        // OutputRoot itself is watched by a desktop sync client.
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(contentPath, catalog, cancellationToken);
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(manifestPath, signed, cancellationToken);

        payloadRelativeFiles.AddRange(media.RelativeFiles);
        var publicationFiles = payloadRelativeFiles
            .Select(NormalizePublicationRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !path.Equals("content.json", StringComparison.OrdinalIgnoreCase)
                           && !path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            .Append("content.json")
            .Append("manifest.json")
            .ToArray();
        var publication = await PublishFilesAsync(
            versionRoot,
            publicationFiles,
            workspace,
            machine,
            progress,
            cancellationToken);
        progress?.Report($"Версия {version}: контент, библиотека модов и выбранные файлы опубликованы одним выпуском.");
        return new ContentBundlePublicationResult(
            version,
            manifestPath,
            contentPath,
            catalog.Items.Count,
            publishedAddonIds,
            preservedAddonIds,
            quick.Additions.Count,
            quick.Deletions.Count,
            quick.SelectedFolders,
            quick.DirectoryDeletions.Count,
            artifactPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            publicationFiles,
            publication);
    }

    public static Task<PublicationResult> UnpublishContentAsync(
        ContentDraft content,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        UnpublishContentAsync(
            content,
            workspace,
            machine,
            googleDrivePublisher: null,
            progress,
            cancellationToken);

    public static async Task<PublicationResult> UnpublishContentAsync(
        ContentDraft content,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        GoogleDrivePublisher? googleDrivePublisher,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Kind == ContentKind.Mod)
        {
            throw new InvalidOperationException("Для модов используйте снятие пакета Библиотеки модов.");
        }

        ReleaseVersionRules.Validate(workspace.Version);
        UnifiedReleaseBuilder.ValidateMachine(machine);
        var versionRoot = Path.Combine(Path.GetFullPath(machine.OutputRoot), workspace.Version.Trim());
        var wasPublished = content.IsPublished;
        content.IsPublished = false;
        var catalog = UnifiedReleaseBuilder.CreateContentCatalog(workspace);
        content.IsPublished = wasPublished;
        var packages = await LoadExistingPackagesAsync(Path.Combine(versionRoot, "manifest.json"), cancellationToken);
        var removesExactVersion = packages.Count == 0 && catalog.Items.Count == 0;
        var targets = GetPublicationTargets(workspace, machine);
        var stablePlan = removesExactVersion
            ? await PreflightStableManifestRemovalAsync(
                workspace,
                machine,
                targets,
                googleDrivePublisher,
                progress,
                cancellationToken)
            : null;

        content.IsPublished = false;
        progress?.Report($"Снятие материала {content.Title}…");
        if (!removesExactVersion)
        {
            await MoveContentMediaToTrashAsync(content, workspace, machine, cancellationToken);
            var refresh = await RefreshManifestAsync(workspace, machine, progress, cancellationToken);
            var relativeFiles = new List<string> { "manifest.json", "content.json" };
            relativeFiles.AddRange(refresh.MediaFiles);
            return await PublishFilesAsync(
                versionRoot,
                relativeFiles,
                workspace,
                machine,
                progress,
                cancellationToken);
        }

        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var trash = Path.Combine(Path.GetFullPath(machine.OutputRoot), ".releaser-trash", stamp);
        var removed = new List<string>();
        await RemoveStableManifestPointersAsync(
            stablePlan!,
            machine,
            trash,
            removed,
            progress,
            cancellationToken);
        await RemoveExactVersionPayloadAsync(
            stablePlan!,
            machine,
            trash,
            removed,
            progress,
            cancellationToken);

        await SynchronizeGitTargetsAsync(workspace, machine, progress, cancellationToken);

        return new PublicationResult(stablePlan!.TargetCount, removed.Count, 0, removed);
    }

    public static Task<PublicationResult> UnpublishAddonAsync(
        ContentDraft addon,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        UnpublishAddonAsync(
            addon,
            workspace,
            machine,
            googleDrivePublisher: null,
            progress,
            cancellationToken);

    public static async Task<PublicationResult> UnpublishAddonAsync(
        ContentDraft addon,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        GoogleDrivePublisher? googleDrivePublisher,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addon);
        ReleaseVersionRules.Validate(workspace.Version);
        UnifiedReleaseBuilder.ValidateMachine(machine);
        var id = NormalizeId(addon.Id);
        var versionRoot = Path.Combine(Path.GetFullPath(machine.OutputRoot), workspace.Version.Trim());
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var trash = Path.Combine(Path.GetFullPath(machine.OutputRoot), ".releaser-trash", stamp);
        var removed = new List<string>();

        var wasPublished = addon.IsPublished;
        addon.IsPublished = false;
        var catalog = UnifiedReleaseBuilder.CreateContentCatalog(workspace);
        addon.IsPublished = wasPublished;
        var packages = await LoadExistingPackagesAsync(Path.Combine(versionRoot, "manifest.json"), cancellationToken);
        var removesExactVersion = packages.Count == 0 && catalog.Items.Count == 0;
        var targets = GetPublicationTargets(workspace, machine);
        var stablePlan = removesExactVersion
            ? await PreflightStableManifestRemovalAsync(
                workspace,
                machine,
                targets,
                googleDrivePublisher,
                progress,
                cancellationToken)
            : null;

        progress?.Report($"Снятие аддона {addon.Title} с публикации…");
        addon.IsPublished = false;
        if (removesExactVersion)
        {
            await RemoveStableManifestPointersAsync(
                stablePlan!,
                machine,
                trash,
                removed,
                progress,
                cancellationToken);
            await RemoveExactVersionPayloadAsync(
                stablePlan!,
                machine,
                trash,
                removed,
                progress,
                cancellationToken);
            await SynchronizeGitTargetsAsync(workspace, machine, progress, cancellationToken);
        }
        else
        {
            var localAddon = Path.Combine(versionRoot, "addons", id);
            if (Directory.Exists(localAddon))
            {
                await MoveDirectoryToTrashAsync(
                    localAddon,
                    Path.Combine(trash, "local", workspace.Version, "addons", id),
                    cancellationToken);
                removed.Add(localAddon);
            }

            foreach (var target in targets)
            {
                var publishedAddon = Path.Combine(target.Root, workspace.Version.Trim(), "addons", id);
                if (!Directory.Exists(publishedAddon))
                {
                    continue;
                }

                await MoveDirectoryToTrashAsync(
                    publishedAddon,
                    Path.Combine(trash, "published", target.Id, workspace.Version, "addons", id),
                    cancellationToken);
                removed.Add(publishedAddon);
            }

            var refresh = await RefreshManifestAsync(workspace, machine, progress, cancellationToken);
            var relativeFiles = new List<string> { "manifest.json", "content.json" };
            relativeFiles.AddRange(refresh.MediaFiles);
            await PublishFilesAsync(versionRoot, relativeFiles, workspace, machine, progress, cancellationToken);
        }

        progress?.Report($"Аддон {addon.Title} снят с публикации; резервная копия сохранена.");
        return new PublicationResult(stablePlan?.TargetCount ?? targets.Length, removed.Count, 0, removed);
    }

    public static async Task<QuickReleaseResult> PublishQuickFilesAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(machine);
        ReleaseVersionRules.Validate(workspace.Version);
        UnifiedReleaseBuilder.ValidateMachine(machine);

        var selectedFiles = (machine.QuickReleaseFiles ?? [])
            .Select(item => new QuickReleaseFileDraft
            {
                Id = item.Id,
                SourcePath = Path.GetFullPath(item.SourcePath),
                InstallRoot = NormalizeInstallRoot(item.InstallRoot),
                RelativePath = QuickReleaseDestinationMapper.NormalizeFileDestination(
                    item.InstallRoot,
                    machine.Mo2SourceRoot,
                    item.SourcePath,
                    item.RelativePath),
            })
            .ToArray();
        foreach (var addition in selectedFiles)
        {
            if (!File.Exists(addition.SourcePath))
            {
                throw new FileNotFoundException("Выбранный для публикации файл больше не найден.", addition.SourcePath);
            }
        }

        var selectedFolders = (machine.QuickReleaseFolders ?? [])
            .Select(item => new QuickReleaseFolderDraft
            {
                Id = item.Id,
                SourcePath = Path.GetFullPath(item.SourcePath),
                InstallRoot = NormalizeInstallRoot(item.InstallRoot),
                RelativePath = QuickReleaseDestinationMapper.NormalizeFolderDestination(
                    item.InstallRoot,
                    machine.Mo2SourceRoot,
                    item.SourcePath,
                    item.RelativePath),
            })
            .ToArray();
        foreach (var folder in selectedFolders)
        {
            if (!Directory.Exists(folder.SourcePath))
            {
                throw new DirectoryNotFoundException($"Выбранная для публикации папка больше не найдена: {folder.SourcePath}");
            }
        }

        var additions = selectedFiles
            .Concat(selectedFolders.SelectMany(ExpandQuickFolder))
            .ToArray();

        var deletions = (machine.QuickDeleteFiles ?? [])
            .Select(item => new QuickDeleteFileDraft
            {
                Id = item.Id,
                InstallRoot = NormalizeInstallRoot(item.InstallRoot),
                RelativePath = PathSafety.NormalizeRelativePath(item.RelativePath),
            })
            .ToArray();
        var directoryDeletions = (machine.QuickDeleteFolders ?? [])
            .Select(item => new QuickDeleteFolderDraft
            {
                Id = item.Id,
                InstallRoot = NormalizeInstallRoot(item.InstallRoot),
                RelativePath = PathSafety.NormalizeRelativePath(item.RelativePath),
            })
            .ToArray();

        ValidateQuickModpackScope(
            additions.Select(item => (item.InstallRoot, item.RelativePath, PathKind: "файл"))
                .Concat(deletions.Select(item => (item.InstallRoot, item.RelativePath, PathKind: "удаляемый файл")))
                .Concat(directoryDeletions.Select(item => (item.InstallRoot, item.RelativePath, PathKind: "удаляемая папка"))));

        if (additions.Length == 0 && deletions.Length == 0 && directoryDeletions.Length == 0)
        {
            throw new InvalidOperationException("Добавьте хотя бы один файл или папку для загрузки либо удаления.");
        }

        var duplicateAddition = additions
            .GroupBy(item => $"{item.InstallRoot}|{item.RelativePath}", StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateAddition is not null)
        {
            throw new InvalidDataException($"Один путь добавлен несколько раз: {duplicateAddition.First().RelativePath}");
        }

        var versionRoot = Path.Combine(Path.GetFullPath(machine.OutputRoot), workspace.Version.Trim());
        Directory.CreateDirectory(versionRoot);
        var manifestPath = Path.Combine(versionRoot, "manifest.json");
        var baselineManifest = await PublicationManifestBaseline.LoadAsync(
            workspace,
            machine,
            cancellationToken);
        var existingPackages = baselineManifest?.Payload.Packages ?? [];
        var changedInstallRoots = additions.Select(item => item.InstallRoot)
            .Concat(deletions.Select(item => item.InstallRoot))
            .Concat(directoryDeletions.Select(item => item.InstallRoot))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packages = existingPackages
            .Where(package => !package.Id.StartsWith("anthology-files-", StringComparison.OrdinalIgnoreCase)
                              || !changedInstallRoots.Contains(package.InstallRoot))
            .Where(package => !string.Equals(
                package.Id,
                PackageIntegrityCatalogBuilder.PackageId,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        var artifacts = new List<string>();
        foreach (var installRoot in new[] { "game", "modpack" })
        {
            var rootAdditions = additions.Where(item => item.InstallRoot == installRoot).ToArray();
            var addedPaths = rootAdditions.Select(item => item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rootDeletions = deletions
                .Where(item => item.InstallRoot == installRoot)
                .Select(item => item.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Except(addedPaths, StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var rootDirectoryDeletions = directoryDeletions
                .Where(item => item.InstallRoot == installRoot)
                .Select(item => item.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (rootAdditions.Length == 0 && rootDeletions.Length == 0 && rootDirectoryDeletions.Length == 0)
            {
                continue;
            }

            var packageId = $"anthology-files-{installRoot}";
            var artifactName = $"{packageId}-{workspace.Version.Trim()}.zip";
            var artifactPath = Path.Combine(versionRoot, artifactName);
            progress?.Report($"Упаковка выбранных файлов: {installRoot}…");
            await CreateMappedArchiveAsync(artifactPath, rootAdditions, cancellationToken);
            var hash = await ArtifactHash.ComputeSha256Async(artifactPath, cancellationToken);
            var artifactSize = new FileInfo(artifactPath).Length;
            var mirrors = workspace.Mirrors
                .Select(mirror => new
                {
                    Mirror = mirror,
                    Url = UnifiedReleaseBuilder.ResolveArtifactUrlTemplate(mirror, workspace),
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Url))
                .Where(item => UnifiedReleaseBuilder.SupportsArtifact(item.Mirror.Provider, artifactSize))
                .Select(item => new MirrorManifest(
                    UnifiedReleaseBuilder.NormalizeProvider(item.Mirror.Provider),
                    UnifiedReleaseBuilder.ExpandUrl(item.Url, workspace.Version, packageId, artifactName),
                    item.Mirror.Priority))
                .OrderBy(item => item.Priority)
                .ToArray();
            if (mirrors.Length == 0)
            {
                mirrors = [new MirrorManifest("local-file", new Uri(artifactPath).AbsoluteUri, 1000)];
            }

            packages.Add(new PackageManifest(
                packageId,
                installRoot == "game" ? "Выбранные файлы Anthology" : "Выбранные файлы Mod Organizer 2",
                workspace.Version.Trim(),
                installRoot == "game" ? PackageKind.Game : PackageKind.Modpack,
                installRoot,
                "zip",
                artifactSize,
                hash,
                mirrors,
                rootAdditions.Select(item => item.RelativePath).Order(StringComparer.Ordinal).ToArray(),
                PackageUpdateMode.Merge,
                false,
                null,
                rootDeletions,
                rootDirectoryDeletions));
            artifacts.Add(artifactPath);
        }

        var media = await ContentMediaPublisher.PrepareAsync(workspace, machine, versionRoot, progress, cancellationToken);
        var catalog = UnifiedReleaseBuilder.CreateContentCatalog(workspace, media);
        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(await File.ReadAllTextAsync(Path.GetFullPath(machine.PrivateKeyPath), cancellationToken));
        var integrity = await PackageIntegrityCatalogBuilder.BuildAsync(
            packages,
            versionRoot,
            workspace,
            privateKey,
            machine.KeyId.Trim(),
            progress,
            cancellationToken);
        if (integrity is not null)
        {
            packages.Add(integrity.Package);
            artifacts.Add(integrity.ArtifactPath);
        }
        var manifestShape = PublicationManifestBaseline.ResolveShape(baselineManifest, packages);
        var payload = new UpdateManifest(
            manifestShape.SchemaVersion,
            string.IsNullOrWhiteSpace(workspace.Channel) ? "next" : workspace.Channel.Trim().ToLowerInvariant(),
            workspace.Version.Trim(),
            DateTimeOffset.UtcNow,
            manifestShape.MinimumLauncherVersion,
            packages,
            catalog);
        var signed = ManifestSecurity.Sign(payload, privateKey, machine.KeyId.Trim());
        ManifestValidator.ValidateAndThrow(signed);
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(manifestPath, signed, cancellationToken);
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(Path.Combine(versionRoot, "content.json"), catalog, cancellationToken);

        var relativeFiles = artifacts.Select(path => Path.GetFileName(path)!).ToList();
        relativeFiles.Add("manifest.json");
        relativeFiles.Add("content.json");
        relativeFiles.AddRange(media.RelativeFiles);
        var publication = await PublishFilesAsync(versionRoot, relativeFiles, workspace, machine, progress, cancellationToken);
        progress?.Report("Выбранные файлы опубликованы во все настроенные источники.");
        return new QuickReleaseResult(
            manifestPath,
            additions.Length,
            deletions.Length,
            selectedFolders.Length,
            directoryDeletions.Length,
            artifacts,
            publication);
    }

    public static Task<PublicationResult> UnpublishVersionAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        UnpublishVersionAsync(
            workspace,
            machine,
            googleDrivePublisher: null,
            progress,
            cancellationToken);

    public static async Task<PublicationResult> UnpublishVersionAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        GoogleDrivePublisher? googleDrivePublisher,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machine);
        _ = ReleaserMachinePathNormalizer.Normalize(machine);
        ReleaseVersionRules.Validate(workspace.Version);
        if (string.IsNullOrWhiteSpace(machine.OutputRoot))
        {
            throw new ArgumentException("Выберите папку готовых релизов.");
        }

        var version = workspace.Version.Trim();
        var outputRoot = Path.GetFullPath(machine.OutputRoot);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var trash = Path.Combine(outputRoot, ".releaser-trash", stamp);
        var moved = new List<string>();
        progress?.Report($"Снятие версии {version} с публикации…");

        var targets = GetPublicationTargets(workspace, machine);
        var stablePlan = await PreflightStableManifestRemovalAsync(
            workspace,
            machine,
            targets,
            googleDrivePublisher,
            progress,
            cancellationToken);
        await RemoveStableManifestPointersAsync(
            stablePlan,
            machine,
            trash,
            moved,
            progress,
            cancellationToken);
        await RemoveExactVersionPayloadAsync(
            stablePlan,
            machine,
            trash,
            moved,
            progress,
            cancellationToken);

        await SynchronizeGitTargetsAsync(workspace, machine, progress, cancellationToken);

        progress?.Report($"Версия {version} снята с публикации; резервная копия сохранена в {trash}.");
        return new PublicationResult(stablePlan.TargetCount, moved.Count, 0, moved);
    }

    private static async Task<StableManifestRemovalPlan> PreflightStableManifestRemovalAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        PublicationTarget[] targets,
        GoogleDrivePublisher? googleDrivePublisher,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var version = workspace.Version.Trim();
        var outputRoot = Path.GetFullPath(machine.OutputRoot);
        var stableManifestRelativePath = ReleaseChannelLayout.GetStableManifestRelativePath(workspace);
        var googleDriveConfigured = GoogleDrivePublisher.IsConfigured(machine);
        var publisher = googleDriveConfigured
            ? googleDrivePublisher ?? new GoogleDrivePublisher()
            : null;
        var googleStableManifestRelativePath = publisher is null
            ? null
            : GoogleDrivePublisher.ResolveStableManifestRelativePath(machine, workspace);
        await EnsureVersionIsNotPinnedByCompatibilityBootstrapAsync(
            workspace,
            machine,
            version,
            outputRoot,
            targets,
            publisher,
            cancellationToken);
        var removeGoogleStableManifest = false;
        if (publisher is not null)
        {
            var remoteManifest = await publisher.ReadManifestAsync(
                machine,
                workspace,
                progress,
                cancellationToken);
            if (remoteManifest is not null)
            {
                await PublicationManifestBaseline.ValidateAsync(
                    remoteManifest,
                    workspace,
                    machine,
                    requireCurrentVersion: false,
                    cancellationToken);
                removeGoogleStableManifest = remoteManifest.Payload.Version.Equals(
                    version,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        var targetStableManifestsToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            var stableManifest = PathSafety.ResolveUnderRoot(target.Root, stableManifestRelativePath);
            if (await StableManifestReferencesVersionAsync(
                    stableManifest,
                    version,
                    workspace,
                    machine,
                    cancellationToken))
            {
                targetStableManifestsToRemove.Add(stableManifest);
            }
        }

        var outputStableManifest = PathSafety.ResolveUnderRoot(outputRoot, stableManifestRelativePath);
        var removeOutputStableManifest = await StableManifestReferencesVersionAsync(
            outputStableManifest,
            version,
            workspace,
            machine,
            cancellationToken);
        var launcherStableManifest = LauncherUpdateConfigurationPublisher.ResolveLocalManifestPath(machine);
        var removeLauncherStableManifest = launcherStableManifest is not null
                                           && await StableManifestReferencesVersionAsync(
                                               launcherStableManifest,
                                               version,
                                               workspace,
                                               machine,
                                               cancellationToken);

        return new StableManifestRemovalPlan(
            version,
            outputRoot,
            stableManifestRelativePath,
            targets,
            targetStableManifestsToRemove,
            outputStableManifest,
            removeOutputStableManifest,
            removeLauncherStableManifest,
            publisher,
            googleStableManifestRelativePath,
            removeGoogleStableManifest,
            targets.Length + (googleDriveConfigured ? 1 : 0));
    }

    private static async Task EnsureVersionIsNotPinnedByCompatibilityBootstrapAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        string version,
        string outputRoot,
        IReadOnlyCollection<PublicationTarget> targets,
        GoogleDrivePublisher? googleDrivePublisher,
        CancellationToken cancellationToken)
    {
        if (!ReleaseChannelLayout.UsesDedicatedStableChannel(workspace))
        {
            return;
        }

        var rootManifestPaths = new[] { outputRoot }
            .Concat(targets.Select(target => target.Root))
            .Select(root => Path.Combine(root, ReleaseChannelLayout.ManifestFileName))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var rootManifestPath in rootManifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bootstrap = await LoadExistingManifestAsync(rootManifestPath, cancellationToken);
            if (bootstrap is null)
            {
                continue;
            }

            await PublicationManifestBaseline.ValidateAsync(
                bootstrap,
                workspace,
                machine,
                requireCurrentVersion: false,
                cancellationToken);
            ThrowIfCompatibilityBootstrapPinsVersion(bootstrap, version, rootManifestPath);
        }

        if (googleDrivePublisher is not null)
        {
            var remoteBootstrap = await googleDrivePublisher.ReadManifestAsync(
                machine,
                progress: null,
                cancellationToken);
            if (remoteBootstrap is not null)
            {
                await PublicationManifestBaseline.ValidateAsync(
                    remoteBootstrap,
                    workspace,
                    machine,
                    requireCurrentVersion: false,
                    cancellationToken);
                ThrowIfCompatibilityBootstrapPinsVersion(
                    remoteBootstrap,
                    version,
                    $"google-drive:/{GoogleDrivePublisher.ResolveStableManifestRelativePath(machine)}");
            }
        }
    }

    private static void ThrowIfCompatibilityBootstrapPinsVersion(
        SignedUpdateManifest manifest,
        string version,
        string source)
    {
        if (manifest.Payload.SchemaVersion != 4
            || !manifest.Payload.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Нельзя снять версию {version}: корневой schema 4 bootstrap ({source}) всё ещё выдаёт её старым лаунчерам. " +
            "Сначала опубликуйте и закрепите другой совместимый launcher bootstrap либо оставьте эту версию доступной.");
    }

    private static async Task RemoveStableManifestPointersAsync(
        StableManifestRemovalPlan plan,
        ReleaserMachineSettings machine,
        string trash,
        List<string> moved,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (plan.GoogleDrivePublisher is not null && plan.RemoveGoogleStableManifest)
        {
            progress?.Report("Google Drive: снимаем стабильный manifest.json перед удалением версии…");
            var stableManifestPath = plan.GoogleStableManifestRelativePath
                                     ?? throw new InvalidOperationException(
                                         "Google Drive stable manifest path is missing from the removal plan.");
            await plan.GoogleDrivePublisher.DeleteFileAsync(
                machine,
                stableManifestPath,
                progress,
                cancellationToken);
            moved.Add("google-drive:/manifest.json");
        }

        foreach (var target in plan.Targets)
        {
            var stableManifest = PathSafety.ResolveUnderRoot(
                target.Root,
                plan.StableManifestRelativePath);
            if (!plan.TargetStableManifestsToRemove.Contains(stableManifest))
            {
                continue;
            }

            await MoveFileToTrashAsync(
                stableManifest,
                PathSafety.ResolveUnderRoot(
                    Path.Combine(trash, "published", target.Id),
                    plan.StableManifestRelativePath),
                cancellationToken);
            moved.Add(stableManifest);
        }

        if (plan.RemoveOutputStableManifest)
        {
            await MoveFileToTrashAsync(
                plan.OutputStableManifest,
                PathSafety.ResolveUnderRoot(
                    Path.Combine(trash, "local"),
                    plan.StableManifestRelativePath),
                cancellationToken);
            moved.Add(plan.OutputStableManifest);
        }

        if (plan.RemoveLauncherStableManifest
            && await LauncherUpdateConfigurationPublisher.RemoveLocalManifestAsync(
                machine,
                trash,
                plan.Version,
                cancellationToken))
        {
            moved.Add("manifest.json лаунчера");
        }
    }

    private static async Task RemoveExactVersionPayloadAsync(
        StableManifestRemovalPlan plan,
        ReleaserMachineSettings machine,
        string trash,
        List<string> moved,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (plan.GoogleDrivePublisher is not null)
        {
            await plan.GoogleDrivePublisher.DeleteReleaseVersionAsync(
                machine,
                plan.Version,
                progress,
                cancellationToken);
            moved.Add($"google-drive:/{CombineGoogleDrivePath(machine.GoogleDriveReleasePath, plan.Version)}");
        }

        foreach (var target in plan.Targets)
        {
            var publishedVersion = Path.Combine(target.Root, plan.Version);
            if (!Directory.Exists(publishedVersion))
            {
                continue;
            }

            await MoveDirectoryToTrashAsync(
                publishedVersion,
                Path.Combine(trash, "published", target.Id, plan.Version),
                cancellationToken);
            moved.Add(publishedVersion);
        }

        var localVersion = Path.Combine(plan.OutputRoot, plan.Version);
        if (Directory.Exists(localVersion))
        {
            await MoveDirectoryToTrashAsync(
                localVersion,
                Path.Combine(trash, "local", plan.Version),
                cancellationToken);
            moved.Add(localVersion);
        }
    }

    private static async Task<bool> StableManifestReferencesVersionAsync(
        string manifestPath,
        string version,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        CancellationToken cancellationToken)
    {
        var manifest = await LoadExistingManifestAsync(manifestPath, cancellationToken);
        if (manifest is null)
        {
            return false;
        }

        await PublicationManifestBaseline.ValidateAsync(
            manifest,
            workspace,
            machine,
            requireCurrentVersion: false,
            cancellationToken);
        return manifest.Payload.Version.Equals(version, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ManifestRefreshResult> RefreshManifestAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var versionRoot = Path.Combine(Path.GetFullPath(machine.OutputRoot), workspace.Version.Trim());
        Directory.CreateDirectory(versionRoot);
        var manifestPath = Path.Combine(versionRoot, "manifest.json");
        var baselineManifest = await PublicationManifestBaseline.LoadAsync(
            workspace,
            machine,
            cancellationToken);
        var packages = baselineManifest?.Payload.Packages ?? [];
        var media = await ContentMediaPublisher.PrepareAsync(workspace, machine, versionRoot, progress, cancellationToken);
        var catalog = UnifiedReleaseBuilder.CreateContentCatalog(workspace, media);
        var manifestShape = PublicationManifestBaseline.ResolveShape(baselineManifest, packages);
        var payload = new UpdateManifest(
            manifestShape.SchemaVersion,
            string.IsNullOrWhiteSpace(workspace.Channel) ? "next" : workspace.Channel.Trim().ToLowerInvariant(),
            workspace.Version.Trim(),
            DateTimeOffset.UtcNow,
            manifestShape.MinimumLauncherVersion,
            packages,
            catalog);
        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(await File.ReadAllTextAsync(Path.GetFullPath(machine.PrivateKeyPath), cancellationToken));
        var signed = ManifestSecurity.Sign(payload, privateKey, machine.KeyId.Trim());
        ManifestValidator.ValidateAndThrow(signed);
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(manifestPath, signed, cancellationToken);
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(Path.Combine(versionRoot, "content.json"), catalog, cancellationToken);
        return new ManifestRefreshResult(manifestPath, media.RelativeFiles);
    }

    private static async Task<IReadOnlyList<PackageManifest>> LoadExistingPackagesAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        var existing = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options, cancellationToken)
                       ?? throw new InvalidDataException("Существующий manifest.json повреждён.");
        return existing.Payload.Packages;
    }

    private static async Task<SignedUpdateManifest?> LoadExistingManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(
                       stream,
                       ManifestJson.Options,
                       cancellationToken)
                   ?? throw new InvalidDataException("Существующий manifest.json повреждён.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Существующий manifest.json повреждён: {manifestPath}", exception);
        }
    }

    private static Task<PublicationResult> PublishFilesAsync(
        string versionRoot,
        IReadOnlyCollection<string> relativeFiles,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress,
        CancellationToken cancellationToken) =>
        PublishFilesAsync(
            versionRoot,
            relativeFiles,
            workspace,
            machine,
            progress,
            googleDrivePublisher: null,
            cancellationToken);

    private static async Task<PublicationResult> PublishFilesAsync(
        string versionRoot,
        IReadOnlyCollection<string> relativeFiles,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress,
        GoogleDrivePublisher? googleDrivePublisher,
        CancellationToken cancellationToken,
        string? compatibilityBootstrapManifestPath = null)
    {
        var normalizedVersionRoot = Path.GetFullPath(versionRoot);
        var stableManifestRelativePath = ReleaseChannelLayout.GetStableManifestRelativePath(workspace);
        var stableHistoryRelativePath = ReleaseChannelLayout.GetStableHistoryRelativePath(workspace);
        var normalizedCompatibilityBootstrap = string.IsNullOrWhiteSpace(compatibilityBootstrapManifestPath)
            ? null
            : Path.GetFullPath(compatibilityBootstrapManifestPath);
        if (normalizedCompatibilityBootstrap is not null
            && !ReleaseChannelLayout.UsesDedicatedStableChannel(workspace))
        {
            throw new InvalidOperationException(
                "The schema 4 compatibility bootstrap requires a dedicated stable channel directory.");
        }
        var normalizedRelativeFiles = relativeFiles
            .Select(relative => PathSafety.NormalizeRelativePath(relative.Replace('\\', '/')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var historyRelativePath = await EnsureReleaseHistoryAsync(
            normalizedVersionRoot,
            normalizedRelativeFiles,
            workspace,
            machine,
            cancellationToken);
        if (historyRelativePath is not null
            && !normalizedRelativeFiles.Contains(historyRelativePath, StringComparer.OrdinalIgnoreCase))
        {
            normalizedRelativeFiles.Add(historyRelativePath);
        }
        var targets = GetPublicationTargets(workspace, machine);
        await LauncherUpdateConfigurationPublisher.PrepareAsync(
            workspace,
            machine,
            progress,
            cancellationToken);
        var googleDrive = await PrepareGoogleDrivePublicationAsync(
            normalizedVersionRoot,
            normalizedRelativeFiles,
            workspace,
            machine,
            googleDrivePublisher,
            progress,
            normalizedCompatibilityBootstrap,
            cancellationToken);
        var destinations = new List<string>();
        long bytes = 0;
        var files = 0;
        var manifestRelativePath = normalizedRelativeFiles
            .FirstOrDefault(relative => relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        var manifestPath = manifestRelativePath is null
            ? null
            : PathSafety.ResolveUnderRoot(normalizedVersionRoot, manifestRelativePath);
        var historyPath = historyRelativePath is null
            ? null
            : PathSafety.ResolveUnderRoot(normalizedVersionRoot, historyRelativePath);

        foreach (var target in targets)
        {
            progress?.Report($"Выгрузка в {target.Provider}: {target.Root}…");
            foreach (var relative in normalizedRelativeFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = PathSafety.ResolveUnderRoot(normalizedVersionRoot, relative);
                if (!File.Exists(source))
                {
                    continue;
                }

                var sourceSize = new FileInfo(source).Length;
                if (!UnifiedReleaseBuilder.SupportsArtifact(target.Provider, sourceSize))
                {
                    progress?.Report($"Skipping {Path.GetFileName(source)} for {target.Provider}: the file exceeds the GitHub repository limit.");
                    continue;
                }

                var destination = Path.Combine(target.Root, workspace.Version.Trim(), relative);
                await CopyFileAtomicallyAsync(source, destination, cancellationToken);
                files++;
                bytes += sourceSize;
            }

            // Every publication root also exposes the latest manifest at a stable,
            // version-independent path used by installed launchers.
            if (manifestPath is not null && File.Exists(manifestPath))
            {
                if (historyPath is not null && File.Exists(historyPath))
                {
                    await CopyFileAtomicallyAsync(
                        historyPath,
                        PathSafety.ResolveUnderRoot(target.Root, stableHistoryRelativePath),
                        cancellationToken);
                    files++;
                    bytes += new FileInfo(historyPath).Length;
                }
                await CopyFileAtomicallyAsync(
                    manifestPath,
                    PathSafety.ResolveUnderRoot(target.Root, stableManifestRelativePath),
                    cancellationToken);
                files++;
                bytes += new FileInfo(manifestPath).Length;
            }

            if (normalizedCompatibilityBootstrap is not null
                && File.Exists(normalizedCompatibilityBootstrap))
            {
                await CopyFileAtomicallyAsync(
                    normalizedCompatibilityBootstrap,
                    Path.Combine(target.Root, ReleaseChannelLayout.ManifestFileName),
                    cancellationToken);
                files++;
                bytes += new FileInfo(normalizedCompatibilityBootstrap).Length;
            }

            destinations.Add(Path.Combine(target.Root, workspace.Version.Trim()));
        }

        await SynchronizeGitTargetsAsync(
            workspace,
            machine,
            progress,
            cancellationToken,
            includeCompatibilityBootstrap: normalizedCompatibilityBootstrap is not null);

        // OutputRoot is the source for sync-folder mirrors such as Yandex.Disk.
        // Publish history first and manifest last so a watcher can never observe
        // a newly activated manifest without its matching signed history.
        if (manifestPath is not null && File.Exists(manifestPath))
        {
            var outputRoot = Path.GetFullPath(machine.OutputRoot);
            if (historyPath is not null && File.Exists(historyPath))
            {
                await CopyFileAtomicallyAsync(
                    historyPath,
                    PathSafety.ResolveUnderRoot(outputRoot, stableHistoryRelativePath),
                    cancellationToken);
            }
            await CopyFileAtomicallyAsync(
                manifestPath,
                PathSafety.ResolveUnderRoot(outputRoot, stableManifestRelativePath),
                cancellationToken);
        }

        if (normalizedCompatibilityBootstrap is not null
            && File.Exists(normalizedCompatibilityBootstrap))
        {
            await CopyFileAtomicallyAsync(
                normalizedCompatibilityBootstrap,
                Path.Combine(Path.GetFullPath(machine.OutputRoot), ReleaseChannelLayout.ManifestFileName),
                cancellationToken);
        }

        if (googleDrive is not null)
        {
            var googleResult = await FinalizeGoogleDrivePublicationAsync(
                googleDrive,
                machine,
                progress,
                cancellationToken);
            files += googleResult.Files;
            bytes += googleResult.Bytes;
            destinations.Add(googleResult.Destination);
        }

        if (manifestPath is not null && File.Exists(manifestPath))
        {
            await LauncherUpdateConfigurationPublisher.UpdateLocalReleaseDocumentsAsync(
                manifestPath,
                historyPath,
                machine,
                cancellationToken);
        }

        return new PublicationResult(targets.Length + (googleDrive is null ? 0 : 1), files, bytes, destinations);
    }

    private static async Task<string?> EnsureReleaseHistoryAsync(
        string versionRoot,
        IReadOnlyCollection<string> relativeFiles,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        CancellationToken cancellationToken)
    {
        var manifestRelativePath = relativeFiles.FirstOrDefault(relative =>
            relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        if (manifestRelativePath is null)
        {
            return null;
        }

        var manifestPath = PathSafety.ResolveUnderRoot(versionRoot, manifestRelativePath);
        var signedManifest = await LoadExistingManifestAsync(manifestPath, cancellationToken)
                             ?? throw new InvalidDataException("Release manifest is missing or invalid; signed history cannot be produced.");
        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(await File.ReadAllTextAsync(
            Path.GetFullPath(machine.PrivateKeyPath),
            cancellationToken));
        var publicationRoots = GetPublicationTargets(workspace, machine)
            .Select(target => target.Root)
            .Prepend(machine.OutputRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var stableDirectory = ReleaseChannelLayout.NormalizeStableChannelDirectory(
            workspace.StableChannelDirectory);
        var trustedRoots = stableDirectory.Length == 0
            ? publicationRoots
            : publicationRoots
                .Concat(publicationRoots.Select(root =>
                    PathSafety.ResolveUnderRoot(root, stableDirectory)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var signedHistory = await ReleaseHistoryCatalogBuilder.BuildAsync(
            trustedRoots,
            signedManifest,
            privateKey,
            machine.KeyId.Trim(),
            cancellationToken);
        var historyPath = Path.Combine(versionRoot, ReleaseHistoryCatalogBuilder.FileName);
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(
            historyPath,
            signedHistory,
            cancellationToken);
        return ReleaseHistoryCatalogBuilder.FileName;
    }

    private static async Task<GoogleDrivePublicationPlan?> PrepareGoogleDrivePublicationAsync(
        string versionRoot,
        IReadOnlyList<string> relativeFiles,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        GoogleDrivePublisher? googleDrivePublisher,
        IProgress<string>? progress,
        string? compatibilityBootstrapManifestPath,
        CancellationToken cancellationToken)
    {
        if (!GoogleDrivePublisher.IsConfigured(machine))
        {
            return null;
        }

        var publisher = googleDrivePublisher ?? new GoogleDrivePublisher();
        // Validate the split before creating folders or uploading payloads. The
        // machine path is permanently reserved for the legacy schema 4 bootstrap;
        // the workspace-aware path must resolve to a distinct modern manifest.
        var stableManifestRelativePath = GoogleDrivePublisher.ResolveStableManifestRelativePath(
            machine,
            workspace);
        _ = await publisher.EnsureProjectAsync(machine, progress, cancellationToken);
        var version = workspace.Version.Trim();
        var manifestRelativePath = relativeFiles.FirstOrDefault(path =>
            path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        var contentRelativePath = relativeFiles.FirstOrDefault(path =>
            path.Equals("content.json", StringComparison.OrdinalIgnoreCase));
        var historyRelativePath = relativeFiles.FirstOrDefault(path =>
            path.Equals(ReleaseHistoryCatalogBuilder.FileName, StringComparison.OrdinalIgnoreCase));
        var uploaded = new List<GoogleDriveUploadedSource>();
        var uploadedFiles = 0;
        long uploadedBytes = 0;

        progress?.Report($"Google Drive: загружаем файлы версии {version} напрямую из папки релиза…");
        foreach (var relativePath in relativeFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (relativePath.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                || relativePath.Equals("content.json", StringComparison.OrdinalIgnoreCase)
                || relativePath.Equals(ReleaseHistoryCatalogBuilder.FileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourcePath = PathSafety.ResolveUnderRoot(versionRoot, relativePath);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var remotePath = CombineGoogleDrivePath(
                machine.GoogleDriveReleasePath,
                version,
                relativePath);
            var remoteFile = await publisher.UploadFileAsync(
                machine,
                sourcePath,
                remotePath,
                progress,
                cancellationToken);
            var sourceSize = new FileInfo(sourcePath).Length;
            if (remoteFile.Size != sourceSize)
            {
                throw new InvalidDataException(
                    $"Google Drive вернул неверный размер файла {relativePath}: ожидалось {sourceSize}, получено {remoteFile.Size}.");
            }

            uploaded.Add(new GoogleDriveUploadedSource(relativePath, sourcePath, sourceSize, remoteFile));
            uploadedFiles++;
            uploadedBytes += sourceSize;
        }

        if (manifestRelativePath is not null)
        {
            var manifestPath = PathSafety.ResolveUnderRoot(versionRoot, manifestRelativePath);
            if (File.Exists(manifestPath))
            {
                var contentPath = contentRelativePath is null
                    ? null
                    : PathSafety.ResolveUnderRoot(versionRoot, contentRelativePath);
                await AddExactGoogleDriveMirrorsAsync(
                    manifestPath,
                    contentPath,
                    uploaded,
                    machine,
                    cancellationToken);
            }
        }

        return new GoogleDrivePublicationPlan(
            publisher,
            versionRoot,
            version,
            manifestRelativePath,
            contentRelativePath,
            historyRelativePath,
            stableManifestRelativePath,
            compatibilityBootstrapManifestPath,
            uploadedFiles,
            uploadedBytes,
            $"google-drive:/{CombineGoogleDrivePath(machine.GoogleDriveReleasePath, version)}");
    }

    private static async Task<GoogleDriveFinalizeResult> FinalizeGoogleDrivePublicationAsync(
        GoogleDrivePublicationPlan plan,
        ReleaserMachineSettings machine,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var files = plan.UploadedFiles;
        var bytes = plan.UploadedBytes;
        if (plan.ContentRelativePath is not null)
        {
            var contentPath = PathSafety.ResolveUnderRoot(plan.VersionRoot, plan.ContentRelativePath);
            if (File.Exists(contentPath))
            {
                _ = await plan.Publisher.UploadFileAsync(
                    machine,
                    contentPath,
                    CombineGoogleDrivePath(
                        machine.GoogleDriveReleasePath,
                        plan.Version,
                        plan.ContentRelativePath),
                    progress,
                    cancellationToken);
                files++;
                bytes += new FileInfo(contentPath).Length;
            }
        }

        if (plan.ManifestRelativePath is not null)
        {
            var manifestPath = PathSafety.ResolveUnderRoot(plan.VersionRoot, plan.ManifestRelativePath);
            if (File.Exists(manifestPath))
            {
                if (plan.HistoryRelativePath is not null)
                {
                    var historyPath = PathSafety.ResolveUnderRoot(plan.VersionRoot, plan.HistoryRelativePath);
                    if (File.Exists(historyPath))
                    {
                        _ = await plan.Publisher.UploadFileAsync(
                            machine,
                            historyPath,
                            CombineGoogleDrivePath(
                                machine.GoogleDriveReleasePath,
                                plan.Version,
                                plan.HistoryRelativePath),
                            progress,
                            cancellationToken);
                        files++;
                        bytes += new FileInfo(historyPath).Length;
                    }
                }

                _ = await plan.Publisher.UploadFileAsync(
                    machine,
                    manifestPath,
                    CombineGoogleDrivePath(
                        machine.GoogleDriveReleasePath,
                        plan.Version,
                        plan.ManifestRelativePath),
                    progress,
                    cancellationToken);
                files++;
                bytes += new FileInfo(manifestPath).Length;

                if (plan.HistoryRelativePath is not null)
                {
                    var historyPath = PathSafety.ResolveUnderRoot(plan.VersionRoot, plan.HistoryRelativePath);
                    if (File.Exists(historyPath))
                    {
                        _ = await plan.Publisher.UploadFileAsync(
                            machine,
                            historyPath,
                            CombineGoogleDrivePath(
                                Path.GetDirectoryName(plan.StableManifestRelativePath)
                                    ?.Replace('\\', '/') ?? string.Empty,
                                ReleaseHistoryCatalogBuilder.FileName),
                            progress,
                            cancellationToken);
                        files++;
                        bytes += new FileInfo(historyPath).Length;
                    }
                }

                // This is the activation point for Google Drive. It deliberately
                // remains the final upload after payloads, content, versioned
                // manifest, local mirrors, and the Git push have all succeeded.
                progress?.Report("Google Drive: публикуем стабильный manifest.json последним…");
                _ = await plan.Publisher.UploadFileAsync(
                    machine,
                    manifestPath,
                    plan.StableManifestRelativePath,
                    progress,
                    cancellationToken);
                files++;
                bytes += new FileInfo(manifestPath).Length;

                if (!string.IsNullOrWhiteSpace(plan.CompatibilityBootstrapManifestPath)
                    && File.Exists(plan.CompatibilityBootstrapManifestPath))
                {
                    // The legacy address is the final activation point for old
                    // schema 4 clients. It contains only the launcher package.
                    _ = await plan.Publisher.UploadFileAsync(
                        machine,
                        plan.CompatibilityBootstrapManifestPath,
                        GoogleDrivePublisher.ResolveStableManifestRelativePath(machine),
                        progress,
                        cancellationToken);
                    files++;
                    bytes += new FileInfo(plan.CompatibilityBootstrapManifestPath).Length;
                }
            }
        }

        return new GoogleDriveFinalizeResult(files, bytes, plan.Destination);
    }

    private static async Task AddExactGoogleDriveMirrorsAsync(
        string manifestPath,
        string? contentPath,
        IReadOnlyList<GoogleDriveUploadedSource> uploaded,
        ReleaserMachineSettings machine,
        CancellationToken cancellationToken)
    {
        SignedUpdateManifest signed;
        await using (var stream = new FileStream(
                         manifestPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            signed = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(
                         stream,
                         ManifestJson.Options,
                         cancellationToken)
                     ?? throw new InvalidDataException("Собранный manifest.json повреждён.");
        }
        ManifestValidator.ValidateAndThrow(signed);

        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(await File.ReadAllTextAsync(
            Path.GetFullPath(machine.PrivateKeyPath),
            cancellationToken));
        if (!string.Equals(signed.Signature.KeyId, machine.KeyId.Trim(), StringComparison.Ordinal)
            || !ManifestSecurity.Verify(signed, privateKey))
        {
            throw new CryptographicException(
                "Собранный manifest.json не подписан текущим ключом релизера; добавление Google Drive остановлено.");
        }

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var packages = new List<PackageManifest>(signed.Payload.Packages.Count);
        foreach (var package in signed.Payload.Packages)
        {
            if (package.LooseFiles is not null)
            {
                packages.Add(package);
                continue;
            }

            var names = GetPackageArtifactFileNames(package);
            var match = await FindExactGoogleDriveArtifactAsync(
                uploaded,
                names,
                package.Size,
                package.Sha256,
                preferredRelativePath: null,
                hashes,
                cancellationToken);
            if (match is null)
            {
                packages.Add(package);
                continue;
            }

            var mirrors = WithExactGoogleDriveMirror(
                package.Mirrors,
                match.Remote.ShareUrl,
                machine.GoogleDriveMirrorPriority);
            changed |= !package.Mirrors.SequenceEqual(mirrors);
            packages.Add(package with { Mirrors = mirrors });
        }

        var content = signed.Payload.Content;
        if (content is not null)
        {
            var documents = new List<ContentDocument>(content.Items.Count);
            foreach (var document in content.Items)
            {
                if (document.Download is null)
                {
                    documents.Add(document);
                    continue;
                }

                var download = document.Download;
                var fileName = GetPathFileName(download.FileName);
                var preferredPath = CombineGoogleDrivePath("addons", document.Id, download.FileName);
                var match = await FindExactGoogleDriveArtifactAsync(
                    uploaded,
                    [fileName],
                    download.Size,
                    download.Sha256,
                    preferredPath,
                    hashes,
                    cancellationToken);
                if (match is null)
                {
                    documents.Add(document);
                    continue;
                }

                var mirrors = WithExactGoogleDriveMirror(
                    download.Mirrors,
                    match.Remote.ShareUrl,
                    machine.GoogleDriveMirrorPriority);
                changed |= !download.Mirrors.SequenceEqual(mirrors);
                documents.Add(document with { Download = download with { Mirrors = mirrors } });
            }

            content = content with { Items = documents };
        }

        if (!changed)
        {
            return;
        }

        var payload = signed.Payload with { Packages = packages, Content = content };
        var updated = ManifestSecurity.Sign(payload, privateKey, machine.KeyId.Trim());
        ManifestValidator.ValidateAndThrow(updated);
        if (content is not null && contentPath is not null && File.Exists(contentPath))
        {
            // content.json must describe exactly the same catalog as the final
            // signed manifest, and is committed before manifest.json.
            await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(contentPath, content, cancellationToken);
        }
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(manifestPath, updated, cancellationToken);
    }

    private static async Task<GoogleDriveUploadedSource?> FindExactGoogleDriveArtifactAsync(
        IReadOnlyList<GoogleDriveUploadedSource> uploaded,
        IReadOnlyCollection<string> fileNames,
        long expectedSize,
        string expectedSha256,
        string? preferredRelativePath,
        Dictionary<string, string> hashes,
        CancellationToken cancellationToken)
    {
        var normalizedNames = fileNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(GetPathFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = uploaded
            .Where(item => item.Size == expectedSize && item.Remote.Size == expectedSize)
            .Where(item => normalizedNames.Contains(GetPathFileName(item.RelativePath)))
            .OrderByDescending(item => preferredRelativePath is not null
                                       && item.RelativePath.Equals(preferredRelativePath, StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!hashes.TryGetValue(candidate.SourcePath, out var hash))
            {
                hash = await ArtifactHash.ComputeSha256Async(candidate.SourcePath, cancellationToken);
                hashes[candidate.SourcePath] = hash;
            }
            if (hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
        return null;
    }

    private static HashSet<string> GetPackageArtifactFileNames(PackageManifest package)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var format = package.ArchiveFormat.Trim().TrimStart('.');
        if (format.Length > 0 && !format.Equals("loose", StringComparison.OrdinalIgnoreCase))
        {
            names.Add($"{package.Id}-{package.Version}.{format}");
        }
        foreach (var mirror in package.Mirrors)
        {
            foreach (var candidate in GetMirrorFileNameCandidates(mirror.Url))
            {
                names.Add(candidate);
            }
        }
        return names;
    }

    private static IEnumerable<string> GetMirrorFileNameCandidates(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            yield break;
        }

        var pathName = GetPathFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (pathName.Length > 0)
        {
            yield return pathName;
        }
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator < 0 || separator == part.Length - 1)
            {
                continue;
            }
            var decoded = Uri.UnescapeDataString(part[(separator + 1)..].Replace('+', ' '));
            var queryName = GetPathFileName(decoded);
            if (queryName.Length > 0)
            {
                yield return queryName;
            }
        }
    }

    private static MirrorManifest[] WithExactGoogleDriveMirror(
        IReadOnlyList<MirrorManifest> mirrors,
        string url,
        int priority)
    {
        var result = mirrors
            .Where(mirror => !mirror.Provider.Equals(
                GoogleDrivePublisher.Provider,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        result.Add(new MirrorManifest(GoogleDrivePublisher.Provider, url, priority));
        return result.OrderBy(mirror => mirror.Priority).ToArray();
    }

    private static string CombineGoogleDrivePath(params string[] segments) =>
        string.Join(
            '/',
            segments
                .Select(segment => segment.Trim().Replace('\\', '/').Trim('/'))
                .Where(segment => segment.Length > 0));

    private static string GetPathFileName(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    private sealed record GoogleDriveUploadedSource(
        string RelativePath,
        string SourcePath,
        long Size,
        GoogleDriveRemoteFile Remote);

    private sealed record GoogleDrivePublicationPlan(
        GoogleDrivePublisher Publisher,
        string VersionRoot,
        string Version,
        string? ManifestRelativePath,
        string? ContentRelativePath,
        string? HistoryRelativePath,
        string StableManifestRelativePath,
        string? CompatibilityBootstrapManifestPath,
        int UploadedFiles,
        long UploadedBytes,
        string Destination);

    private sealed record GoogleDriveFinalizeResult(int Files, long Bytes, string Destination);

    private static async Task SynchronizeGitTargetsAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool includeCompatibilityBootstrap = false)
    {
        foreach (var target in GetPublicationTargets(workspace, machine)
                     .Where(target => target.Provider.Equals("github", StringComparison.OrdinalIgnoreCase)))
        {
            await SynchronizeGitTargetAsync(
                target,
                workspace,
                progress,
                includeCompatibilityBootstrap,
                cancellationToken);
        }
    }

    private static async Task SynchronizeGitTargetAsync(
        PublicationTarget target,
        ReleaserWorkspace workspace,
        IProgress<string>? progress,
        bool includeCompatibilityBootstrap,
        CancellationToken cancellationToken)
    {
        var version = workspace.Version.Trim();
        Directory.CreateDirectory(target.Root);
        var repositoryCheck = await RunGitAsync(target.Root, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (repositoryCheck.ExitCode != 0 ||
            !repositoryCheck.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Папка GitHub-источника не является Git-репозиторием: {target.Root}. " +
                "Выберите локальную папку клонированного репозитория.");
        }

        var branch = await RunGitAsync(target.Root, ["branch", "--show-current"], cancellationToken);
        EnsureGitSucceeded(branch, "Не удалось определить ветку GitHub-источника.");
        var branchName = branch.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(branchName))
        {
            throw new InvalidOperationException("GitHub-источник находится в detached HEAD. Переключитесь на рабочую ветку.");
        }

        progress?.Report($"GitHub: подготовка версии {version} в ветке {branchName}…");
        var stableManifestRelativePath = ReleaseChannelLayout.GetStableManifestRelativePath(workspace);
        var stableHistoryRelativePath = ReleaseChannelLayout.GetStableHistoryRelativePath(workspace);
        var publicationPaths = new List<string> { version, stableManifestRelativePath };
        if (File.Exists(PathSafety.ResolveUnderRoot(target.Root, stableHistoryRelativePath)))
        {
            publicationPaths.Add(stableHistoryRelativePath);
        }
        if (includeCompatibilityBootstrap
            && !stableManifestRelativePath.Equals(
                ReleaseChannelLayout.ManifestFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            publicationPaths.Add(ReleaseChannelLayout.ManifestFileName);
        }
        publicationPaths = publicationPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var addArguments = new List<string> { "add", "-A", "--" };
        addArguments.AddRange(publicationPaths);
        var add = await RunGitAsync(
            target.Root,
            addArguments,
            cancellationToken);
        EnsureGitSucceeded(add, "Не удалось подготовить файлы публикации для GitHub.");

        var diffArguments = new List<string> { "diff", "--cached", "--quiet", "--" };
        diffArguments.AddRange(publicationPaths);
        var diff = await RunGitAsync(
            target.Root,
            diffArguments,
            cancellationToken);
        if (diff.ExitCode is not (0 or 1))
        {
            EnsureGitSucceeded(diff, "Не удалось проверить изменения перед публикацией в GitHub.");
        }

        if (diff.ExitCode == 1)
        {
            var commitArguments = new List<string> { "commit", "-m", $"Publish Anthology {version}", "--" };
            commitArguments.AddRange(publicationPaths);
            var commit = await RunGitAsync(
                target.Root,
                commitArguments,
                cancellationToken);
            EnsureGitSucceeded(commit, "Не удалось создать коммит публикации для GitHub.");
        }

        progress?.Report($"GitHub: отправка версии {version} в origin/{branchName}…");
        var push = await RunGitAsync(
            target.Root,
            ["push", "origin", $"HEAD:{branchName}"],
            cancellationToken);
        EnsureGitSucceeded(push, "Не удалось отправить публикацию в GitHub.");
        progress?.Report($"GitHub: версия {version} опубликована в ветке {branchName}.");
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repositoryRoot);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Не удалось запустить Git.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new GitCommandResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private static void EnsureGitSucceeded(GitCommandResult result, string message)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var details = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(details) ? message : $"{message} {details}");
    }

    private static PublicationTarget[] GetPublicationTargets(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine)
    {
        _ = ReleaserMachinePathNormalizer.Normalize(machine);
        var outputRoot = string.IsNullOrWhiteSpace(machine.OutputRoot) ? null : Path.GetFullPath(machine.OutputRoot);
        return workspace.Mirrors
            .Where(mirror => machine.PublicationRoots.TryGetValue(mirror.Id, out var root) && !string.IsNullOrWhiteSpace(root))
            .Select(mirror => new PublicationTarget(mirror.Id, UnifiedReleaseBuilder.NormalizeProvider(mirror.Provider), Path.GetFullPath(machine.PublicationRoots[mirror.Id])))
            .Where(target => outputRoot is null || !PathsEqual(target.Root, outputRoot))
            .GroupBy(target => target.Root, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static async Task CopyFileAtomicallyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(source);
        var destinationPath = Path.GetFullPath(destination);
        if (PathsEqual(sourcePath, destinationPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporary = destinationPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destinationPath, true);
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Staging cleanup must never turn a completed immutable artifact into
            // a failed launcher publication.
        }
    }

    private static async Task MoveDirectoryToTrashAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException)
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                await CopyFileAtomicallyAsync(file, Path.Combine(destination, relative), cancellationToken);
            }

            Directory.Delete(source, true);
        }
    }

    private static async Task MoveFileToTrashAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (!File.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            File.Move(source, destination, true);
        }
        catch (IOException)
        {
            await CopyFileAtomicallyAsync(source, destination, cancellationToken);
            File.Delete(source);
        }
    }

    private static async Task MoveContentMediaToTrashAsync(
        ContentDraft content,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        CancellationToken cancellationToken)
    {
        var id = NormalizeId(content.Id);
        var version = workspace.Version.Trim();
        var outputRoot = Path.GetFullPath(machine.OutputRoot);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var trash = Path.Combine(outputRoot, ".releaser-trash", stamp);
        var localMedia = Path.Combine(outputRoot, version, "addons", id, "media");
        if (Directory.Exists(localMedia))
        {
            await MoveDirectoryToTrashAsync(
                localMedia,
                Path.Combine(trash, "local", version, "addons", id, "media"),
                cancellationToken);
        }
        foreach (var target in GetPublicationTargets(workspace, machine))
        {
            var publishedMedia = Path.Combine(target.Root, version, "addons", id, "media");
            if (!Directory.Exists(publishedMedia))
            {
                continue;
            }
            await MoveDirectoryToTrashAsync(
                publishedMedia,
                Path.Combine(trash, "published", target.Id, version, "addons", id, "media"),
                cancellationToken);
        }
    }

    private static bool TryGetSelectedAddonArchive(
        ReleaserMachineSettings machine,
        string contentId,
        string normalizedId,
        out string archivePath)
    {
        if ((machine.ContentArchivePaths.TryGetValue(contentId, out var selected)
             || machine.ContentArchivePaths.TryGetValue(normalizedId, out selected))
            && !string.IsNullOrWhiteSpace(selected))
        {
            archivePath = selected;
            return true;
        }

        archivePath = string.Empty;
        return false;
    }

    private static async Task<PreparedAddonArtifact> PrepareAddonArtifactAsync(
        ContentDraft addon,
        string id,
        string selectedArchive,
        ReleaserWorkspace workspace,
        string versionRoot,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(selectedArchive);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Выбранный архив аддона больше не существует.", source);
        }
        if (!SupportedArchives.Contains(Path.GetExtension(source)))
        {
            throw new InvalidDataException("Аддон должен быть готовым архивом ZIP, 7Z или RAR.");
        }

        var fileName = Path.GetFileName(source);
        var relativeArtifact = NormalizePublicationRelativePath(Path.Combine("addons", id, fileName));
        var artifact = Path.Combine(versionRoot, relativeArtifact.Replace('/', Path.DirectorySeparatorChar));
        progress?.Report($"Подготовка аддона {addon.Title}…");
        await CopyFileAtomicallyAsync(source, artifact, cancellationToken);

        var artifactSize = new FileInfo(artifact).Length;
        addon.DownloadFileName = fileName;
        addon.DownloadSize = artifactSize;
        addon.DownloadSha256 = await ArtifactHash.ComputeSha256Async(artifact, cancellationToken);
        addon.IsPublished = true;
        var configuredMirrors = string.Join(Environment.NewLine, workspace.Mirrors
            .Where(mirror => !string.IsNullOrWhiteSpace(mirror.ContentUrl))
            .Where(mirror => UnifiedReleaseBuilder.SupportsArtifact(mirror.Provider, artifactSize))
            .OrderBy(mirror => mirror.Priority)
            .Select(mirror => $"{UnifiedReleaseBuilder.NormalizeProvider(mirror.Provider)} | {mirror.ContentUrl.Trim()}"));
        if (!string.IsNullOrWhiteSpace(configuredMirrors))
        {
            // A newly selected archive is a new immutable payload. Recreate its
            // mirror list so an exact URL retained from an older addon revision
            // cannot serve bytes with the wrong hash.
            addon.DownloadMirrors = configuredMirrors;
        }
        if (string.IsNullOrWhiteSpace(addon.DownloadMirrors))
        {
            addon.DownloadMirrors = $"local-file | {new Uri(artifact).AbsoluteUri}";
        }

        return new PreparedAddonArtifact(artifact, relativeArtifact);
    }

    private static void RestoreAddonDownloadFromExistingManifest(
        ContentDraft addon,
        SignedUpdateManifest? existingManifest)
    {
        var existing = existingManifest?.Payload.Content?.Items.FirstOrDefault(item =>
            item.Kind == ContentKind.Mod
            && item.Id.Equals(addon.Id, StringComparison.OrdinalIgnoreCase));
        var download = existing?.Download;
        if (download is null)
        {
            return;
        }

        // The previous catalog contains fully expanded immutable URLs. Reusing
        // those URLs is what lets a content-only version retain an already
        // published addon without copying its archive into the new version.
        addon.DownloadFileName = download.FileName;
        addon.DownloadSize = download.Size;
        addon.DownloadSha256 = download.Sha256;
        addon.DownloadMirrors = string.Join(Environment.NewLine, download.Mirrors
            .OrderBy(mirror => mirror.Priority)
            .Select(mirror => $"{mirror.Provider} | {mirror.Url}"));
        addon.InstallFolderName = download.InstallName ?? string.Empty;
    }

    private static bool TryResolveLocalAddonArtifact(
        string versionRoot,
        ContentDraft addon,
        string id,
        out string artifactPath,
        out string relativePath)
    {
        var fileName = addon.DownloadFileName.Trim();
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            artifactPath = string.Empty;
            relativePath = string.Empty;
            return false;
        }

        relativePath = NormalizePublicationRelativePath(Path.Combine("addons", id, fileName));
        artifactPath = Path.Combine(versionRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(artifactPath))
        {
            return true;
        }

        artifactPath = string.Empty;
        relativePath = string.Empty;
        return false;
    }

    private static PreparedQuickSelection PrepareQuickSelection(
        ReleaserMachineSettings machine,
        bool allowEmpty)
    {
        var selectedFiles = (machine.QuickReleaseFiles ?? [])
            .Select(item => new QuickReleaseFileDraft
            {
                Id = item.Id,
                SourcePath = Path.GetFullPath(item.SourcePath),
                InstallRoot = NormalizeInstallRoot(item.InstallRoot),
                RelativePath = QuickReleaseDestinationMapper.NormalizeFileDestination(
                    item.InstallRoot,
                    machine.Mo2SourceRoot,
                    item.SourcePath,
                    item.RelativePath),
            })
            .ToArray();
        foreach (var addition in selectedFiles)
        {
            if (!File.Exists(addition.SourcePath))
            {
                throw new FileNotFoundException("Выбранный для публикации файл больше не найден.", addition.SourcePath);
            }
        }

        var selectedFolders = (machine.QuickReleaseFolders ?? [])
            .Select(item => new QuickReleaseFolderDraft
            {
                Id = item.Id,
                SourcePath = Path.GetFullPath(item.SourcePath),
                InstallRoot = NormalizeInstallRoot(item.InstallRoot),
                RelativePath = QuickReleaseDestinationMapper.NormalizeFolderDestination(
                    item.InstallRoot,
                    machine.Mo2SourceRoot,
                    item.SourcePath,
                    item.RelativePath),
            })
            .ToArray();
        foreach (var folder in selectedFolders)
        {
            if (!Directory.Exists(folder.SourcePath))
            {
                throw new DirectoryNotFoundException($"Выбранная для публикации папка больше не найдена: {folder.SourcePath}");
            }
        }

        var additions = selectedFiles
            .Concat(selectedFolders.SelectMany(ExpandQuickFolder))
            .ToArray();
        var deletions = (machine.QuickDeleteFiles ?? [])
            .Select(item => new QuickDeleteFileDraft
            {
                Id = item.Id,
                InstallRoot = NormalizeInstallRoot(item.InstallRoot),
                RelativePath = PathSafety.NormalizeRelativePath(item.RelativePath),
            })
            .ToArray();
        var directoryDeletions = (machine.QuickDeleteFolders ?? [])
            .Select(item => new QuickDeleteFolderDraft
            {
                Id = item.Id,
                InstallRoot = NormalizeInstallRoot(item.InstallRoot),
                RelativePath = PathSafety.NormalizeRelativePath(item.RelativePath),
            })
            .ToArray();

        ValidateQuickModpackScope(
            additions.Select(item => (item.InstallRoot, item.RelativePath, PathKind: "файл"))
                .Concat(deletions.Select(item => (item.InstallRoot, item.RelativePath, PathKind: "удаляемый файл")))
                .Concat(directoryDeletions.Select(item => (item.InstallRoot, item.RelativePath, PathKind: "удаляемая папка"))));

        if (!allowEmpty && additions.Length == 0 && deletions.Length == 0 && directoryDeletions.Length == 0)
        {
            throw new InvalidOperationException("Добавьте хотя бы один файл или папку для загрузки либо удаления.");
        }
        var duplicateAddition = additions
            .GroupBy(item => $"{item.InstallRoot}|{item.RelativePath}", StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateAddition is not null)
        {
            throw new InvalidDataException($"Один путь добавлен несколько раз: {duplicateAddition.First().RelativePath}");
        }

        return new PreparedQuickSelection(
            additions,
            deletions,
            directoryDeletions,
            selectedFolders.Length);
    }

    private static string NormalizePublicationRelativePath(string path) =>
        PathSafety.NormalizeRelativePath(path.Replace('\\', '/'));

    private static async Task CreateMappedArchiveAsync(
        string artifactPath,
        IReadOnlyList<QuickReleaseFileDraft> files,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        var temporary = artifactPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var file in files.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(
                    file.RelativePath.Replace('\\', '/'),
                    SelectQuickArchiveCompression(file.SourcePath));
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                await using var entryStream = entry.Open();
                await using var sourceStream = new FileStream(
                    file.SourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await sourceStream.CopyToAsync(entryStream, cancellationToken);
            }
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
            throw;
        }
        File.Move(temporary, artifactPath, true);
    }

    private static void ValidateQuickModpackScope(
        IEnumerable<(string InstallRoot, string RelativePath, string PathKind)> targets)
    {
        foreach (var target in targets)
        {
            if (!target.InstallRoot.Equals("modpack", StringComparison.OrdinalIgnoreCase)
                || PackageInstallScopePolicy.IsAllowedMo2ModsPath(target.RelativePath))
            {
                continue;
            }

            throw new InvalidDataException(
                $"Быстрый пакет MO2 может изменять только 'mods/**': {target.PathKind} '{target.RelativePath}'. "
                + "Выберите объект заново или укажите путь с префиксом 'mods/'.");
        }
    }

    private static CompressionLevel SelectQuickArchiveCompression(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            return CompressionLevel.NoCompression;
        }

        // XRay DB volumes are already packed and are commonly several gigabytes each.
        // Deflate's SmallestSize mode spends minutes per volume for only a marginal
        // reduction, so large payloads and .xdb* volumes use the fast deterministic path.
        var isXrayDatabase = extension.StartsWith(".xdb", StringComparison.OrdinalIgnoreCase);
        return isXrayDatabase || new FileInfo(sourcePath).Length >= 128L * 1024 * 1024
            ? CompressionLevel.Fastest
            : CompressionLevel.SmallestSize;
    }

    private static IEnumerable<QuickReleaseFileDraft> ExpandQuickFolder(QuickReleaseFolderDraft folder)
    {
        var root = Path.GetFullPath(folder.SourcePath);
        foreach (var sourcePath in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = false,
                     AttributesToSkip = FileAttributes.ReparsePoint,
                 }))
        {
            var childPath = PathSafety.NormalizeRelativePath(Path.GetRelativePath(root, sourcePath));
            yield return new QuickReleaseFileDraft
            {
                Id = $"{folder.Id}-{Guid.NewGuid():N}",
                SourcePath = Path.GetFullPath(sourcePath),
                InstallRoot = folder.InstallRoot,
                RelativePath = CombineRelativePath(folder.RelativePath, childPath),
            };
        }
    }

    private static IEnumerable<string> EnumerateLauncherUpdateFiles(string launcherRoot)
    {
        var root = Path.GetFullPath(launcherRoot);
        return Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            .Where(path =>
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                var fileName = Path.GetFileName(path);
                if (relative.StartsWith("App/wwwroot/", StringComparison.OrdinalIgnoreCase)
                    || relative.Equals("App/TrustedKeys/anthology.public.pem", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (relative.StartsWith("App/", StringComparison.OrdinalIgnoreCase))
                {
                    return fileName.StartsWith("Anthology.", StringComparison.OrdinalIgnoreCase)
                           || fileName.StartsWith("AnthologyLauncher.", StringComparison.OrdinalIgnoreCase)
                           || fileName.Equals("System.Management.dll", StringComparison.OrdinalIgnoreCase);
                }

                return relative.StartsWith("Services/CommunityApi/", StringComparison.OrdinalIgnoreCase)
                       && (fileName.StartsWith("Anthology.", StringComparison.OrdinalIgnoreCase)
                           || fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase));
            })
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveLauncherVersion(string launcherAssembly)
    {
        var productVersion = FileVersionInfo.GetVersionInfo(launcherAssembly).ProductVersion;
        if (string.IsNullOrWhiteSpace(productVersion))
        {
            return "unknown";
        }

        var metadata = productVersion.IndexOf('+');
        return (metadata > 0 ? productVersion[..metadata] : productVersion).Trim();
    }

    private static string NormalizeFolderBase(string relativePath) =>
        string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : PathSafety.NormalizeRelativePath(relativePath);

    private static string CombineRelativePath(string basePath, string childPath) =>
        string.IsNullOrWhiteSpace(basePath)
            ? PathSafety.NormalizeRelativePath(childPath)
            : PathSafety.NormalizeRelativePath($"{basePath.TrimEnd('/', '\\')}/{childPath}");

    private static string NormalizeInstallRoot(string installRoot) =>
        installRoot.Trim().ToLowerInvariant() switch
        {
            "game" => "game",
            "modpack" or "mo2" => "modpack",
            _ => throw new InvalidDataException("Назначение файла должно быть «Корень игры» или «MO2»."),
        };

    private static string NormalizeId(string id)
    {
        var normalized = id.Trim().ToLowerInvariant();
        if (!SafeIdRegex().IsMatch(normalized))
        {
            throw new InvalidDataException("ID аддона: 2–80 символов; разрешены латинские буквы, цифры, точка, дефис и подчёркивание.");
        }

        return normalized;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdRegex();

    private sealed record ManifestRefreshResult(string ManifestPath, IReadOnlyList<string> MediaFiles);

    private sealed record PreparedAddonArtifact(
        string ArtifactPath,
        string RelativePath);

    private sealed record PreparedQuickSelection(
        IReadOnlyList<QuickReleaseFileDraft> Additions,
        IReadOnlyList<QuickDeleteFileDraft> Deletions,
        IReadOnlyList<QuickDeleteFolderDraft> DirectoryDeletions,
        int SelectedFolders);

    private sealed record LauncherPendingUpdate(
        int SchemaVersion,
        string LauncherVersion,
        string ReleaseVersion,
        string PayloadFile,
        string Sha256);

    private sealed record PublicationTarget(string Id, string Provider, string Root);

    private sealed record StableManifestRemovalPlan(
        string Version,
        string OutputRoot,
        string StableManifestRelativePath,
        PublicationTarget[] Targets,
        HashSet<string> TargetStableManifestsToRemove,
        string OutputStableManifest,
        bool RemoveOutputStableManifest,
        bool RemoveLauncherStableManifest,
        GoogleDrivePublisher? GoogleDrivePublisher,
        string? GoogleStableManifestRelativePath,
        bool RemoveGoogleStableManifest,
        int TargetCount);

    private sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);
}
