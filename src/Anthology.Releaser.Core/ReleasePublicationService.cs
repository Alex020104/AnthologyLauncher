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

    public static async Task<PublicationResult> PublishReleaseAsync(
        UnifiedReleaseResult release,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        var versionRoot = Path.GetDirectoryName(Path.GetFullPath(release.ManifestPath))
                          ?? throw new InvalidOperationException("Не удалось определить папку собранного релиза.");
        var relativeFiles = Directory.EnumerateFiles(versionRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(versionRoot, path))
            .Where(path => !path.Equals("release-workspace.json", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.StartsWith(".releaser-trash" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return await PublishFilesAsync(versionRoot, relativeFiles, workspace, machine, progress, cancellationToken);
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

        var deliveryName = $"anthology-launcher-{workspace.Version.Trim()}.zip";
        var deliveryPath = Path.Combine(versionRoot, deliveryName);
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
        await CreateMappedArchiveAsync(deliveryPath, deliveredFiles, cancellationToken);

        var mirrors = workspace.Mirrors
            .Where(mirror => !string.IsNullOrWhiteSpace(mirror.GameUrl))
            .Select(mirror => new MirrorManifest(
                UnifiedReleaseBuilder.NormalizeProvider(mirror.Provider),
                UnifiedReleaseBuilder.ExpandUrl(mirror.GameUrl.Trim(), workspace.Version, "anthology-launcher", deliveryName),
                mirror.Priority))
            .OrderBy(mirror => mirror.Priority)
            .ToArray();
        if (mirrors.Length == 0)
        {
            mirrors = [new MirrorManifest("local-file", new Uri(deliveryPath).AbsoluteUri, 1000)];
        }

        var manifestPath = Path.Combine(versionRoot, "manifest.json");
        var packages = (await LoadExistingPackagesAsync(manifestPath, cancellationToken))
            .Where(package => !string.Equals(package.Id, "anthology-launcher", StringComparison.OrdinalIgnoreCase))
            .ToList();
        packages.Add(new PackageManifest(
            "anthology-launcher",
            $"A.N.T.H.O.L.O.G.Y Launcher {launcherVersion}",
            launcherVersion,
            PackageKind.Launcher,
            "game",
            "zip",
            new FileInfo(deliveryPath).Length,
            await ArtifactHash.ComputeSha256Async(deliveryPath, cancellationToken),
            mirrors,
            deliveredFiles.Select(file => file.RelativePath).Order(StringComparer.Ordinal).ToArray(),
            PackageUpdateMode.Merge));

        var media = await ContentMediaPublisher.PrepareAsync(workspace, machine, versionRoot, progress, cancellationToken);
        var catalog = UnifiedReleaseBuilder.CreateContentCatalog(workspace, media);
        var updateManifest = new UpdateManifest(
            4,
            string.IsNullOrWhiteSpace(workspace.Channel) ? "next" : workspace.Channel.Trim().ToLowerInvariant(),
            workspace.Version.Trim(),
            DateTimeOffset.UtcNow,
            null,
            packages,
            catalog);
        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(await File.ReadAllTextAsync(Path.GetFullPath(machine.PrivateKeyPath), cancellationToken));
        var signed = ManifestSecurity.Sign(updateManifest, privateKey, machine.KeyId.Trim());
        ManifestValidator.ValidateAndThrow(signed);
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(manifestPath, signed, cancellationToken);
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(Path.Combine(versionRoot, "content.json"), catalog, cancellationToken);

        var relativeFiles = new List<string> { deliveryName, "manifest.json", "content.json" };
        relativeFiles.AddRange(media.RelativeFiles);
        var publication = await PublishFilesAsync(versionRoot, relativeFiles, workspace, machine, progress, cancellationToken);
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

    public static async Task<PublicationResult> UnpublishContentAsync(
        ContentDraft content,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
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
        content.IsPublished = false;
        var versionRoot = Path.Combine(Path.GetFullPath(machine.OutputRoot), workspace.Version.Trim());
        await MoveContentMediaToTrashAsync(content, workspace, machine, cancellationToken);
        var catalog = UnifiedReleaseBuilder.CreateContentCatalog(workspace);
        var packages = await LoadExistingPackagesAsync(Path.Combine(versionRoot, "manifest.json"), cancellationToken);
        progress?.Report($"Снятие материала {content.Title}…");
        if (packages.Count > 0 || catalog.Items.Count > 0)
        {
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
        foreach (var fileName in new[] { "manifest.json", "content.json" })
        {
            var local = Path.Combine(versionRoot, fileName);
            if (File.Exists(local))
            {
                await MoveFileToTrashAsync(local, Path.Combine(trash, "local", workspace.Version, fileName), cancellationToken);
                removed.Add(local);
            }
            foreach (var target in GetPublicationTargets(workspace, machine))
            {
                var published = Path.Combine(target.Root, workspace.Version, fileName);
                if (!File.Exists(published))
                {
                    continue;
                }
                await MoveFileToTrashAsync(published, Path.Combine(trash, "published", target.Id, workspace.Version, fileName), cancellationToken);
                removed.Add(published);
            }

            if (fileName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var target in GetPublicationTargets(workspace, machine))
                {
                    var stableManifest = Path.Combine(target.Root, "manifest.json");
                    if (!File.Exists(stableManifest))
                    {
                        continue;
                    }

                    await MoveFileToTrashAsync(
                        stableManifest,
                        Path.Combine(trash, "published", target.Id, "manifest.json"),
                        cancellationToken);
                    removed.Add(stableManifest);
                }

                await LauncherUpdateConfigurationPublisher.RemoveLocalManifestAsync(machine, trash, cancellationToken);
            }
        }

        await SynchronizeGitTargetsAsync(workspace, machine, progress, cancellationToken);

        return new PublicationResult(GetPublicationTargets(workspace, machine).Length, removed.Count, 0, removed);
    }

    public static async Task<PublicationResult> UnpublishAddonAsync(
        ContentDraft addon,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
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

        progress?.Report($"Снятие аддона {addon.Title} с публикации…");
        var localAddon = Path.Combine(versionRoot, "addons", id);
        if (Directory.Exists(localAddon))
        {
            await MoveDirectoryToTrashAsync(localAddon, Path.Combine(trash, "local", workspace.Version, "addons", id), cancellationToken);
            removed.Add(localAddon);
        }

        foreach (var target in GetPublicationTargets(workspace, machine))
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

        addon.IsPublished = false;
        var catalog = UnifiedReleaseBuilder.CreateContentCatalog(workspace);
        var packages = await LoadExistingPackagesAsync(Path.Combine(versionRoot, "manifest.json"), cancellationToken);
        if (packages.Count == 0 && catalog.Items.Count == 0)
        {
            await MoveFileToTrashAsync(Path.Combine(versionRoot, "manifest.json"), Path.Combine(trash, "local", workspace.Version, "manifest.json"), cancellationToken);
            await MoveFileToTrashAsync(Path.Combine(versionRoot, "content.json"), Path.Combine(trash, "local", workspace.Version, "content.json"), cancellationToken);
            foreach (var target in GetPublicationTargets(workspace, machine))
            {
                await MoveFileToTrashAsync(Path.Combine(target.Root, workspace.Version, "manifest.json"), Path.Combine(trash, "published", target.Id, workspace.Version, "manifest.json"), cancellationToken);
                await MoveFileToTrashAsync(Path.Combine(target.Root, workspace.Version, "content.json"), Path.Combine(trash, "published", target.Id, workspace.Version, "content.json"), cancellationToken);
                await MoveFileToTrashAsync(Path.Combine(target.Root, "manifest.json"), Path.Combine(trash, "published", target.Id, "manifest.json"), cancellationToken);
            }
            await LauncherUpdateConfigurationPublisher.RemoveLocalManifestAsync(machine, trash, cancellationToken);
            await SynchronizeGitTargetsAsync(workspace, machine, progress, cancellationToken);
        }
        else
        {
            var refresh = await RefreshManifestAsync(workspace, machine, progress, cancellationToken);
            var relativeFiles = new List<string> { "manifest.json", "content.json" };
            relativeFiles.AddRange(refresh.MediaFiles);
            await PublishFilesAsync(versionRoot, relativeFiles, workspace, machine, progress, cancellationToken);
        }

        progress?.Report($"Аддон {addon.Title} снят с публикации; резервная копия сохранена.");
        return new PublicationResult(GetPublicationTargets(workspace, machine).Length, removed.Count, 0, removed);
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
                RelativePath = PathSafety.NormalizeRelativePath(item.RelativePath),
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
                RelativePath = NormalizeFolderBase(item.RelativePath),
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
        var existingPackages = await LoadExistingPackagesAsync(manifestPath, cancellationToken);
        var packages = existingPackages
            .Where(package => !package.Id.StartsWith("anthology-files-", StringComparison.OrdinalIgnoreCase))
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
                    Url = (installRoot == "game" ? mirror.GameUrl : mirror.Mo2Url).Trim(),
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
        var payload = new UpdateManifest(
            4,
            string.IsNullOrWhiteSpace(workspace.Channel) ? "next" : workspace.Channel.Trim().ToLowerInvariant(),
            workspace.Version.Trim(),
            DateTimeOffset.UtcNow,
            null,
            packages,
            catalog);
        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(await File.ReadAllTextAsync(Path.GetFullPath(machine.PrivateKeyPath), cancellationToken));
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

    public static async Task<PublicationResult> UnpublishVersionAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
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
        foreach (var target in targets)
        {
            var publishedVersion = Path.Combine(target.Root, version);
            if (Directory.Exists(publishedVersion))
            {
                await MoveDirectoryToTrashAsync(publishedVersion, Path.Combine(trash, "published", target.Id, version), cancellationToken);
                moved.Add(publishedVersion);
            }

            var stableManifest = Path.Combine(target.Root, "manifest.json");
            if (File.Exists(stableManifest))
            {
                await MoveFileToTrashAsync(stableManifest, Path.Combine(trash, "published", target.Id, "manifest.json"), cancellationToken);
                moved.Add(stableManifest);
            }
        }

        await SynchronizeGitTargetsAsync(workspace, machine, progress, cancellationToken);

        var localVersion = Path.Combine(outputRoot, version);
        if (Directory.Exists(localVersion))
        {
            await MoveDirectoryToTrashAsync(localVersion, Path.Combine(trash, "local", version), cancellationToken);
            moved.Add(localVersion);
        }

        if (await LauncherUpdateConfigurationPublisher.RemoveLocalManifestAsync(machine, trash, cancellationToken))
        {
            moved.Add("manifest.json лаунчера");
        }

        progress?.Report($"Версия {version} снята с публикации; резервная копия сохранена в {trash}.");
        return new PublicationResult(targets.Length, moved.Count, 0, moved);
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
        var packages = await LoadExistingPackagesAsync(manifestPath, cancellationToken);
        var media = await ContentMediaPublisher.PrepareAsync(workspace, machine, versionRoot, progress, cancellationToken);
        var catalog = UnifiedReleaseBuilder.CreateContentCatalog(workspace, media);
        var payload = new UpdateManifest(
            4,
            string.IsNullOrWhiteSpace(workspace.Channel) ? "next" : workspace.Channel.Trim().ToLowerInvariant(),
            workspace.Version.Trim(),
            DateTimeOffset.UtcNow,
            null,
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

    private static async Task<PublicationResult> PublishFilesAsync(
        string versionRoot,
        IReadOnlyCollection<string> relativeFiles,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var targets = GetPublicationTargets(workspace, machine);
        await LauncherUpdateConfigurationPublisher.PrepareAsync(
            workspace,
            machine,
            progress,
            cancellationToken);
        var destinations = new List<string>();
        long bytes = 0;
        var files = 0;
        var manifestRelativePath = relativeFiles
            .FirstOrDefault(relative => relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        var manifestPath = manifestRelativePath is null
            ? null
            : Path.GetFullPath(Path.Combine(versionRoot, manifestRelativePath));
        foreach (var target in targets)
        {
            progress?.Report($"Выгрузка в {target.Provider}: {target.Root}…");
            foreach (var relative in relativeFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.GetFullPath(Path.Combine(versionRoot, relative));
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
                await CopyFileAtomicallyAsync(
                    manifestPath,
                    Path.Combine(target.Root, "manifest.json"),
                    cancellationToken);
                files++;
                bytes += new FileInfo(manifestPath).Length;
            }

            destinations.Add(Path.Combine(target.Root, workspace.Version.Trim()));
        }

        await SynchronizeGitTargetsAsync(workspace, machine, progress, cancellationToken);

        if (manifestPath is not null && File.Exists(manifestPath))
        {
            await LauncherUpdateConfigurationPublisher.UpdateLocalManifestAsync(
                manifestPath,
                machine,
                cancellationToken);
        }

        return new PublicationResult(targets.Length, files, bytes, destinations);
    }

    private static async Task SynchronizeGitTargetsAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var target in GetPublicationTargets(workspace, machine)
                     .Where(target => target.Provider.Equals("github", StringComparison.OrdinalIgnoreCase)))
        {
            await SynchronizeGitTargetAsync(
                target,
                workspace.Version.Trim(),
                progress,
                cancellationToken);
        }
    }

    private static async Task SynchronizeGitTargetAsync(
        PublicationTarget target,
        string version,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
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
        var add = await RunGitAsync(
            target.Root,
            ["add", "-A", "--", version, "manifest.json"],
            cancellationToken);
        EnsureGitSucceeded(add, "Не удалось подготовить файлы публикации для GitHub.");

        var diff = await RunGitAsync(
            target.Root,
            ["diff", "--cached", "--quiet", "--", version, "manifest.json"],
            cancellationToken);
        if (diff.ExitCode is not (0 or 1))
        {
            EnsureGitSucceeded(diff, "Не удалось проверить изменения перед публикацией в GitHub.");
        }

        if (diff.ExitCode == 1)
        {
            var commit = await RunGitAsync(
                target.Root,
                ["commit", "-m", $"Publish Anthology {version}", "--", version, "manifest.json"],
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
                var entry = archive.CreateEntry(file.RelativePath.Replace('\\', '/'), CompressionLevel.SmallestSize);
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

    private sealed record LauncherPendingUpdate(
        int SchemaVersion,
        string LauncherVersion,
        string ReleaseVersion,
        string PayloadFile,
        string Sha256);

    private sealed record PublicationTarget(string Id, string Provider, string Root);

    private sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);
}
