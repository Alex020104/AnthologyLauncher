using System.Diagnostics;
using System.Buffers;
using System.IO;
using Anthology.Mo2.Core;
using SharpCompress.Common;

namespace Anthology.Launcher;

public sealed record Mo2WorkspaceSnapshot(
    Mo2InstanceSnapshot Instance,
    string? SelectedProfile,
    string? SelectedExecutable,
    Mo2ProfileSnapshot? Profile,
    bool RuntimeBusy);

public sealed record Mo2ArchivePreparation(
    bool Success,
    bool IsFomod,
    string Message,
    string? ProfileName = null,
    FomodPackage? Package = null,
    FomodDependencyContext? DependencyContext = null,
    Mo2ManualArchivePackage? ManualPackage = null);

public sealed record FomodImageAssetResult(
    bool Success,
    string Message,
    string? DataUrl = null);

public sealed record FomodImageAssetsResult(
    bool Success,
    string Message,
    IReadOnlyDictionary<string, string> DataUrls);

public sealed record FomodInstallActionResult(
    bool Success,
    bool Canceled,
    string Message);

public sealed class Mo2IntegrationService(
    LauncherSettingsStore settingsStore,
    LauncherBridge launcherBridge,
    SaveProvenanceService saveProvenance) : IDisposable
{
    private static readonly SearchValues<char> InvalidSaveNameCharacters =
        SearchValues.Create("/\\:*?\"<>|^()[]%");
    private const int MaxFomodImageBytes = 4 * 1024 * 1024;
    private const long MaxFomodImageBatchBytes = 18L * 1024 * 1024;
    private readonly SemaphoreSlim _contentGate = new(1, 1);
    private readonly SemaphoreSlim _archiveInstallGate = new(1, 1);
    private string? _contentKey;
    private string? _contentModListPath;
    private long _contentModListStamp;
    private Mo2ContentIndex? _contentIndex;

#pragma warning disable CA1822 // Exposed through the injected service so the UI can monitor runtime state.
    public bool RuntimeBusy => IsRuntimeBusy();
#pragma warning restore CA1822

    public Mo2WorkspaceSnapshot GetWorkspace()
    {
        var runtimeBusy = IsRuntimeBusy();
        var instance = Mo2ProfileManager.Detect(settingsStore.Current.ModpackRoot);
        if (!instance.Available)
        {
            return new Mo2WorkspaceSnapshot(instance, null, null, null, runtimeBusy);
        }

        if (!string.IsNullOrWhiteSpace(settingsStore.Current.GameRoot)
            && Directory.Exists(settingsStore.Current.GameRoot))
        {
            instance = instance with { GamePath = Path.GetFullPath(settingsStore.Current.GameRoot) };
        }

        var profile = instance.Profiles.Contains(settingsStore.Current.SelectedMo2Profile, StringComparer.OrdinalIgnoreCase)
            ? settingsStore.Current.SelectedMo2Profile
            : instance.SelectedProfile;
        var executable = instance.Executables.Any(item => string.Equals(item.Title, settingsStore.Current.SelectedMo2Executable, StringComparison.OrdinalIgnoreCase))
            ? settingsStore.Current.SelectedMo2Executable
            : ChooseDefaultExecutable(instance.Executables, settingsStore.Current);
        Mo2ProfileSnapshot? profileSnapshot = null;
        if (profile is not null)
        {
            if (runtimeBusy)
            {
                profileSnapshot = Mo2ProfileManager.ReadProfile(instance.Root, profile);
            }
            else
            {
                var reconciliation = Mo2ProfileManager.ReconcileProfile(instance.Root, profile);
                profileSnapshot = reconciliation.Profile;
                if (reconciliation.Changed)
                {
                    InvalidateContent();
                    instance = instance with
                    {
                        StatusText = $"{instance.StatusText} · modlist очищен: удалено {reconciliation.RemovedMissingMods.Count}, добавлено выключенными {reconciliation.AddedDisabledMods.Count}",
                    };
                }
            }
        }

        return new Mo2WorkspaceSnapshot(instance, profile, executable, profileSnapshot, runtimeBusy);
    }

    public async Task<LauncherActionResult> SaveSelectionAsync(
        string? profile,
        string? executable,
        CancellationToken cancellationToken = default)
    {
        var instance = await Task.Run(
            () => Mo2ProfileManager.Detect(settingsStore.Current.ModpackRoot),
            cancellationToken);
        if (!instance.Available)
        {
            return new LauncherActionResult(false, instance.StatusText);
        }

        if (!instance.Profiles.Contains(profile, StringComparer.OrdinalIgnoreCase))
        {
            return new LauncherActionResult(false, "Выбранный профиль отсутствует в MO2");
        }

        if (!instance.Executables.Any(item => string.Equals(item.Title, executable, StringComparison.OrdinalIgnoreCase)))
        {
            return new LauncherActionResult(false, "Выбранный запуск отсутствует в ModOrganizer.ini");
        }

        if (await Task.Run(IsRuntimeBusy, cancellationToken))
        {
            return new LauncherActionResult(false, "Нельзя менять профиль, пока MO2 или Anomaly запущены");
        }

        try
        {
            await Task.Run(
                () => Mo2ProfileManager.SetSelectedProfile(instance.Root, profile!),
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException)
        {
            return new LauncherActionResult(false, exception.Message);
        }

        var settings = settingsStore.Current.Copy();
        settings.SelectedMo2Profile = profile;
        settings.SelectedMo2Executable = executable;
        await settingsStore.SaveAsync(settings, cancellationToken);
        InvalidateContent();
        return new LauncherActionResult(true, $"Назначена сборка: {profile} · {executable}");
    }

    public LauncherActionResult SetEnabled(string profile, string modName, bool enabled)
    {
        if (IsRuntimeBusy())
        {
            return new LauncherActionResult(false, "Профиль заблокирован, пока MO2 или Anomaly запущены");
        }

        try
        {
            Mo2ProfileManager.SetEnabled(RequireRoot(), profile, modName, enabled);
            InvalidateContent();
            return new LauncherActionResult(true, enabled ? $"Включён: {modName}" : $"Отключён: {modName}");
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or InvalidOperationException
                                           or UnauthorizedAccessException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    public LauncherActionResult Move(string profile, string modName, int direction)
    {
        if (IsRuntimeBusy())
        {
            return new LauncherActionResult(false, "Профиль заблокирован, пока MO2 или Anomaly запущены");
        }

        try
        {
            Mo2ProfileManager.Move(RequireRoot(), profile, modName, direction);
            InvalidateContent();
            return new LauncherActionResult(true, "Приоритет изменён; резервная копия modlist.txt сохранена");
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or InvalidOperationException
                                           or UnauthorizedAccessException
                                           or ArgumentOutOfRangeException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    public LauncherActionResult MoveTo(string profile, string modName, string targetModName)
    {
        if (IsRuntimeBusy())
        {
            return new LauncherActionResult(false, "Профиль заблокирован, пока MO2 или Anomaly запущены");
        }

        try
        {
            Mo2ProfileManager.MoveTo(RequireRoot(), profile, modName, targetModName);
            InvalidateContent();
            return new LauncherActionResult(true, "Порядок модов сохранён; резервная копия modlist.txt создана");
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or InvalidOperationException
                                           or UnauthorizedAccessException
                                           or ArgumentOutOfRangeException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    public LauncherActionResult OpenWorkspaceFolder(string folder)
    {
        try
        {
            var root = RequireRoot();
            var path = folder switch
            {
                "root" => root,
                "mods" => Path.Combine(root, "mods"),
                "downloads" => Path.Combine(root, "downloads"),
                "overwrite" => Path.Combine(root, "overwrite"),
                "profiles" => Path.Combine(root, "profiles"),
                _ => throw new ArgumentOutOfRangeException(nameof(folder), "Неизвестный каталог MO2."),
            };

            return OpenDirectory(path);
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidOperationException
                                           or UnauthorizedAccessException
                                           or ArgumentOutOfRangeException
                                           or System.ComponentModel.Win32Exception)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    public LauncherActionResult OpenModFolder(Mo2ModEntry mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        try
        {
            var root = Path.GetFullPath(Path.Combine(RequireRoot(), "mods"));
            var path = Path.GetFullPath(mod.DirectoryPath);
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Каталог мода находится вне папки mods.");
            }

            return OpenDirectory(path);
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or InvalidOperationException
                                           or UnauthorizedAccessException
                                           or System.ComponentModel.Win32Exception)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    public LauncherActionResult OpenFileLocation(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return new LauncherActionResult(false, $"Путь не найден: {path}");
            }

            var workspace = GetWorkspace();
            var fullPath = Path.GetFullPath(path);
            var allowedRoots = new[] { workspace.Instance.Root, workspace.Instance.GamePath }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFullPath(value!))
                .ToArray();
            if (!allowedRoots.Any(root => fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
                                          || fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                return new LauncherActionResult(false, "Путь находится вне подключённой игры и MO2");
            }

            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            if (File.Exists(path))
            {
                startInfo.ArgumentList.Add("/select,");
            }
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
            return new LauncherActionResult(true, "Путь открыт в Проводнике");
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidOperationException
                                           or UnauthorizedAccessException
                                           or System.ComponentModel.Win32Exception)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    public async Task<Mo2ArchivePreparation> InspectArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        FomodPackage? packageToDispose = null;
        try
        {
            var workspace = await Task.Run(GetWorkspace, cancellationToken);
            if (!workspace.Instance.Available || workspace.SelectedProfile is null)
            {
                return new Mo2ArchivePreparation(false, false, "Сначала выберите профиль MO2");
            }
            if (workspace.RuntimeBusy)
            {
                return new Mo2ArchivePreparation(
                    false,
                    false,
                    "Нельзя устанавливать мод, пока MO2 или Anomaly запущены");
            }
            if (!File.Exists(archivePath))
            {
                return new Mo2ArchivePreparation(false, false, $"Архив не найден: {archivePath}");
            }

            var inspection = await Task.Run(
                () => Mo2ArchiveInstaller.InspectFomod(archivePath, cancellationToken),
                cancellationToken);
            if (!inspection.IsFomod)
            {
                var manualPackage = await Task.Run(
                    () => Mo2ArchiveInstaller.InspectManualArchive(archivePath, cancellationToken),
                    cancellationToken);
                return new Mo2ArchivePreparation(
                    true,
                    false,
                    "Обычный архив готов к выбору корневой папки.",
                    workspace.SelectedProfile,
                    ManualPackage: manualPackage);
            }
            if (!inspection.Success || inspection.Package is null)
            {
                return new Mo2ArchivePreparation(
                    false,
                    true,
                    inspection.Message,
                    workspace.SelectedProfile);
            }

            packageToDispose = inspection.Package;
            var dependencyContext = await Task.Run(
                () => CreateFomodDependencyContext(workspace, inspection.Package, cancellationToken),
                cancellationToken);
            var preparation = new Mo2ArchivePreparation(
                true,
                true,
                inspection.Message,
                workspace.SelectedProfile,
                inspection.Package,
                dependencyContext);
            packageToDispose = null;
            return preparation;
        }
        catch (OperationCanceledException)
        {
            return new Mo2ArchivePreparation(false, false, "Проверка архива отменена.");
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException
                                           or NotSupportedException
                                           or ArgumentException
                                           or AggregateException
                                           or SharpCompressException)
        {
            return new Mo2ArchivePreparation(false, false, exception.Message);
        }
        finally
        {
            packageToDispose?.Dispose();
        }
    }

#pragma warning disable CA1822 // Kept on the injected service as part of the archive-wizard API.
    public async Task<FomodImageAssetsResult> ReadFomodImagesAsync(
        FomodPackage package,
        IEnumerable<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(relativePaths);
        var requestedPaths = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/').TrimStart('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
        if (requestedPaths.Length == 0)
        {
            return new FomodImageAssetsResult(
                false,
                "Пути к изображениям FOMOD не указаны.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        try
        {
            var assets = await Task.Run(
                () => FomodArchiveReader.ReadAssets(
                    package,
                    requestedPaths,
                    MaxFomodImageBytes,
                    MaxFomodImageBatchBytes,
                    cancellationToken),
                cancellationToken);
            var dataUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets)
            {
                var mimeType = DetectSafeImageMime(asset.Value);
                if (mimeType is not null)
                {
                    dataUrls[asset.Key] = $"data:{mimeType};base64,{Convert.ToBase64String(asset.Value)}";
                }
            }

            return new FomodImageAssetsResult(
                dataUrls.Count > 0,
                dataUrls.Count > 0
                    ? "Изображения FOMOD загружены."
                    : "Изображения FOMOD отсутствуют либо имеют неподдерживаемый формат.",
                dataUrls);
        }
        catch (OperationCanceledException)
        {
            return new FomodImageAssetsResult(
                false,
                "Загрузка изображений отменена.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException
                                           or NotSupportedException
                                           or ArgumentException
                                           or AggregateException
                                           or SharpCompressException)
        {
            return new FomodImageAssetsResult(
                false,
                exception.Message,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    public async Task<FomodImageAssetResult> ReadFomodImageAsync(
        FomodPackage package,
        string? relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return new FomodImageAssetResult(false, "Путь к изображению FOMOD не указан.");
        }

        var normalizedPath = relativePath.Replace('\\', '/').TrimStart('/');
        var result = await ReadFomodImagesAsync(package, [normalizedPath], cancellationToken);
        return result.DataUrls.TryGetValue(normalizedPath, out var dataUrl)
            ? new FomodImageAssetResult(true, result.Message, dataUrl)
            : new FomodImageAssetResult(false, result.Message);
    }
#pragma warning restore CA1822

    public async Task<LauncherActionResult> InstallArchiveAsync(
        string archivePath,
        string? installName = null,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        var workspace = await Task.Run(GetWorkspace);
        if (!workspace.Instance.Available || workspace.SelectedProfile is null)
        {
            return new LauncherActionResult(false, "Сначала выберите профиль MO2");
        }

        if (workspace.RuntimeBusy)
        {
            return new LauncherActionResult(false, "Нельзя устанавливать мод, пока MO2 или Anomaly запущены");
        }

        var gateEntered = false;
        try
        {
            await _archiveInstallGate.WaitAsync(cancellationToken);
            gateEntered = true;
            var result = await Task.Run(
                () => Mo2ArchiveInstaller.Install(
                    workspace.Instance.Root,
                    workspace.SelectedProfile,
                    archivePath,
                    installName,
                    replaceExisting,
                    cancellationToken: cancellationToken),
                cancellationToken);
            if (result.Success)
            {
                InvalidateContent();
            }
            return new LauncherActionResult(result.Success, result.Message);
        }
        catch (OperationCanceledException)
        {
            return new LauncherActionResult(false, "Установка архива отменена.");
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException
                                           or NotSupportedException
                                           or ArgumentException
                                           or AggregateException
                                           or SharpCompressException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
        finally
        {
            if (gateEntered)
            {
                _archiveInstallGate.Release();
            }
        }
    }

    public async Task<LauncherActionResult> InstallManualArchiveAsync(
        Mo2ManualArchivePackage package,
        string selectedRoot,
        string profileName,
        string? installName = null,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var gateEntered = false;
        try
        {
            var workspace = await Task.Run(GetWorkspace, cancellationToken);
            if (!workspace.Instance.Available
                || !workspace.Instance.Profiles.Contains(profileName, StringComparer.OrdinalIgnoreCase))
            {
                return new LauncherActionResult(false, "Профиль MO2, выбранный для архива, больше недоступен");
            }
            if (workspace.RuntimeBusy)
            {
                return new LauncherActionResult(false, "Нельзя устанавливать мод, пока MO2 или Anomaly запущены");
            }

            await _archiveInstallGate.WaitAsync(cancellationToken);
            gateEntered = true;
            var result = await Task.Run(
                () => Mo2ArchiveInstaller.Install(
                    workspace.Instance.Root,
                    profileName,
                    package,
                    selectedRoot,
                    installName,
                    replaceExisting,
                    cancellationToken: cancellationToken),
                cancellationToken);
            if (result.Success)
            {
                InvalidateContent();
            }
            return new LauncherActionResult(result.Success, result.Message);
        }
        catch (OperationCanceledException)
        {
            return new LauncherActionResult(false, "Ручная установка архива отменена.");
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException
                                           or NotSupportedException
                                           or ArgumentException
                                           or AggregateException
                                           or SharpCompressException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
        finally
        {
            if (gateEntered)
            {
                _archiveInstallGate.Release();
            }
        }
    }

    public async Task<FomodInstallActionResult> InstallFomodArchiveAsync(
        FomodPackage package,
        FomodInstallPlan plan,
        string profileName,
        string? installName = null,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(plan);
        var gateEntered = false;
        try
        {
            var workspace = await Task.Run(GetWorkspace, cancellationToken);
            if (!workspace.Instance.Available
                || !workspace.Instance.Profiles.Contains(profileName, StringComparer.OrdinalIgnoreCase))
            {
                return new FomodInstallActionResult(false, false, "Профиль MO2, выбранный для FOMOD, больше недоступен");
            }
            if (workspace.RuntimeBusy)
            {
                return new FomodInstallActionResult(false, false, "Нельзя устанавливать мод, пока MO2 или Anomaly запущены");
            }

            await _archiveInstallGate.WaitAsync(cancellationToken);
            gateEntered = true;
            var result = await Task.Run(
                () => Mo2ArchiveInstaller.InstallFomod(
                    workspace.Instance.Root,
                    profileName,
                    package,
                    plan,
                    installName,
                    replaceExisting,
                    cancellationToken: cancellationToken),
                cancellationToken);
            if (result.Success)
            {
                InvalidateContent();
            }
            return new FomodInstallActionResult(result.Success, false, result.Message);
        }
        catch (OperationCanceledException)
        {
            return new FomodInstallActionResult(false, true, "Установка FOMOD отменена.");
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException
                                           or NotSupportedException
                                           or ArgumentException
                                           or AggregateException
                                           or SharpCompressException)
        {
            return new FomodInstallActionResult(false, false, exception.Message);
        }
        finally
        {
            if (gateEntered)
            {
                _archiveInstallGate.Release();
            }
        }
    }

    public async Task<LauncherActionResult> CreateProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default)
    {
        var workspace = await Task.Run(GetWorkspace, cancellationToken);
        if (!workspace.Instance.Available)
        {
            return new LauncherActionResult(false, workspace.Instance.StatusText);
        }
        if (workspace.RuntimeBusy)
        {
            return new LauncherActionResult(false, "Нельзя создавать профиль, пока MO2 или Anomaly запущены");
        }

        try
        {
            await Task.Run(
                () =>
                {
                    Mo2ProfileManager.CreateProfile(workspace.Instance.Root, profileName, workspace.SelectedProfile);
                    Mo2ProfileManager.SetSelectedProfile(workspace.Instance.Root, profileName);
                },
                cancellationToken);
            var settings = settingsStore.Current.Copy();
            settings.SelectedMo2Profile = profileName;
            await settingsStore.SaveAsync(settings, cancellationToken);
            InvalidateContent();
            return new LauncherActionResult(true, $"Создана копия профиля: {profileName}");
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    public LauncherActionResult AddSeparator(string displayName)
    {
        var workspace = GetWorkspace();
        if (!workspace.Instance.Available || workspace.SelectedProfile is null)
        {
            return new LauncherActionResult(false, "Сначала выберите профиль MO2");
        }
        if (workspace.RuntimeBusy)
        {
            return new LauncherActionResult(false, "Нельзя менять профиль, пока MO2 или Anomaly запущены");
        }

        try
        {
            Mo2ProfileManager.AddSeparator(workspace.Instance.Root, workspace.SelectedProfile, displayName);
            InvalidateContent();
            return new LauncherActionResult(true, $"Разделитель добавлен: {displayName}");
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    public async Task<Mo2ContentOverview> GetContentOverviewAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var index = await GetContentIndexAsync(forceRefresh, cancellationToken);
        return index.Overview;
    }

    public async Task<IReadOnlyList<Mo2VirtualEntry>> BrowseDataAsync(
        string? relativePath,
        CancellationToken cancellationToken = default)
    {
        var index = await GetContentIndexAsync(false, cancellationToken);
        return await Task.Run(() => index.Browse(relativePath), cancellationToken);
    }

    public async Task<IReadOnlyList<Mo2VirtualEntry>> SearchDataAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var index = await GetContentIndexAsync(false, cancellationToken);
        return await Task.Run(() => index.Search(query, cancellationToken: cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<Mo2ConflictEntry>> GetConflictsAsync(
        string modName,
        CancellationToken cancellationToken = default)
    {
        var index = await GetContentIndexAsync(false, cancellationToken);
        return await Task.Run(() => index.GetConflicts(modName), cancellationToken);
    }

    public Task<LauncherActionResult> LaunchSelectedAsync(CancellationToken cancellationToken = default) =>
        LaunchSelectedCoreAsync(null, cancellationToken);

    public Task<LauncherActionResult> LaunchSaveAsync(
        string savePath,
        CancellationToken cancellationToken = default) =>
        LaunchSelectedCoreAsync(savePath, cancellationToken);

    private async Task<LauncherActionResult> LaunchSelectedCoreAsync(
        string? savePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = settingsStore.Current;
            if (string.IsNullOrWhiteSpace(settings.ModpackRoot)
                || string.IsNullOrWhiteSpace(settings.GameRoot))
            {
                return new LauncherActionResult(false, "Сначала подключите папки игры и Mod Organizer 2.");
            }

            Mo2ProfileManager.EnsurePortableConfiguration(
                settings.ModpackRoot,
                settings.GameRoot,
                settings.SelectedMo2Profile);
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or InvalidOperationException
                                           or UnauthorizedAccessException)
        {
            return new LauncherActionResult(false, $"Не удалось подготовить portable MO2: {exception.Message}");
        }

        var workspace = await Task.Run(GetWorkspace, cancellationToken);
        if (!workspace.Instance.Available || workspace.SelectedProfile is null || workspace.SelectedExecutable is null)
        {
            return new LauncherActionResult(false, "Сначала назначьте профиль и запуск в разделе MO2");
        }

        if (workspace.RuntimeBusy)
        {
            return new LauncherActionResult(false, "MO2 или Anomaly уже запущены. Закройте текущую сессию перед запуском другого профиля.");
        }

        string? saveName = null;
        if (!string.IsNullOrWhiteSpace(savePath))
        {
            try
            {
                saveName = ValidateSaveForLaunch(workspace.Instance, workspace.SelectedProfile, savePath);
            }
            catch (Exception exception) when (exception is IOException
                                               or InvalidDataException
                                               or InvalidOperationException
                                               or UnauthorizedAccessException)
            {
                return new LauncherActionResult(false, exception.Message);
            }
        }

        var prepared = await launcherBridge.PrepareModpackRuntimeAsync(cancellationToken);
        if (!prepared.Success)
        {
            return prepared;
        }

        try
        {
            var organizer = Path.Combine(workspace.Instance.Root, "ModOrganizer.exe");
            if (!string.IsNullOrWhiteSpace(workspace.Instance.GamePath))
            {
                Mo2ProfileManager.RebaseGamePaths(workspace.Instance.Root, workspace.Instance.GamePath);
            }
            Mo2ProfileManager.SetSelectedProfile(workspace.Instance.Root, workspace.SelectedProfile);
            var startInfo = new ProcessStartInfo(organizer)
            {
                WorkingDirectory = workspace.Instance.Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(workspace.SelectedExecutable);
            startInfo.ArgumentList.Add("-a");
            var gameArguments = launcherBridge.GetGameArguments();
            if (saveName is not null)
            {
                gameArguments = AnomalyLaunchArguments.AppendStartSave(gameArguments, saveName);
            }
            startInfo.ArgumentList.Add(gameArguments);
            Process.Start(startInfo);
            saveProvenance.BeginSession(
                workspace.Instance.GamePath ?? settingsStore.Current.GameRoot!,
                SaveRuntimeOrigin.ModOrganizer,
                workspace.SelectedProfile);
            return new LauncherActionResult(
                true,
                saveName is null
                    ? $"Запущена {workspace.SelectedProfile} через скрытый runtime MO2"
                    : $"Запущено сохранение «{saveName}» через профиль {workspace.SelectedProfile}");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    private static string ValidateSaveForLaunch(Mo2InstanceSnapshot instance, string selectedProfile, string savePath)
    {
        if (string.IsNullOrWhiteSpace(instance.GamePath))
        {
            throw new InvalidOperationException("MO2 не сообщает корень игры для проверки сохранения");
        }

        var fullPath = Path.GetFullPath(savePath);
        if (!File.Exists(fullPath))
        {
            throw new IOException($"Файл сохранения не найден: {Path.GetFileName(fullPath)}");
        }

        var gameRoot = Path.GetFullPath(instance.GamePath);
        var allowedDirectories = new[]
        {
            Path.Combine(gameRoot, "appdata", "savedgames"),
            Path.Combine(gameRoot, "_appdata_", "savedgames"),
            Path.Combine(gameRoot, "savedgames"),
            Path.Combine(instance.Root, "profiles", selectedProfile, "saves"),
        };
        if (!allowedDirectories.Any(directory => IsInsideDirectory(fullPath, directory)))
        {
            throw new InvalidOperationException("Запуск разрешён только для сохранений подключённой игры");
        }

        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".scop", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Выбранный файл не является сохранением Anomaly");
        }

        var saveName = Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrWhiteSpace(saveName)
            || saveName.AsSpan().IndexOfAny(InvalidSaveNameCharacters) >= 0)
        {
            throw new InvalidDataException("Имя сохранения содержит недопустимые для X-Ray символы");
        }

        return saveName;
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), path);
        return !Path.IsPathRooted(relative)
               && !relative.Equals("..", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private string RequireRoot()
    {
        var instance = Mo2ProfileManager.Detect(settingsStore.Current.ModpackRoot);
        if (!instance.Available)
        {
            throw new InvalidOperationException(instance.StatusText);
        }

        return instance.Root;
    }

    private async Task<Mo2ContentIndex> GetContentIndexAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var cachedIndex = _contentIndex;
        var cachedModListPath = _contentModListPath;
        if (!forceRefresh
            && cachedIndex is not null
            && !string.IsNullOrWhiteSpace(cachedModListPath)
            && File.Exists(cachedModListPath)
            && File.GetLastWriteTimeUtc(cachedModListPath).Ticks == _contentModListStamp)
        {
            return cachedIndex;
        }

        var workspace = await Task.Run(GetWorkspace, cancellationToken);
        if (!workspace.Instance.Available || workspace.Profile is null || workspace.SelectedProfile is null)
        {
            throw new InvalidOperationException("Профиль MO2 не выбран");
        }

        var stamp = File.GetLastWriteTimeUtc(workspace.Profile.ModListPath).Ticks;
        var key = $"{workspace.Instance.Root}|{workspace.SelectedProfile}|{stamp}";
        if (!forceRefresh && _contentIndex is not null && string.Equals(_contentKey, key, StringComparison.OrdinalIgnoreCase))
        {
            return _contentIndex;
        }

        await _contentGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _contentIndex is not null && string.Equals(_contentKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return _contentIndex;
            }

            var index = await Task.Run(
                () => Mo2ContentIndex.Build(workspace.Instance, workspace.Profile, cancellationToken),
                cancellationToken);
            _contentKey = key;
            _contentModListPath = workspace.Profile.ModListPath;
            _contentModListStamp = stamp;
            _contentIndex = index;
            return index;
        }
        finally
        {
            _contentGate.Release();
        }
    }

    private static FomodDependencyContext CreateFomodDependencyContext(
        Mo2WorkspaceSnapshot workspace,
        FomodPackage package,
        CancellationToken cancellationToken)
    {
        var dependencyFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddDependencyFiles(package.Module.Dependencies, dependencyFiles);
        foreach (var step in package.Module.Steps)
        {
            AddDependencyFiles(step.Visibility, dependencyFiles);
            foreach (var plugin in step.Groups.SelectMany(group => group.Plugins))
            {
                foreach (var pattern in plugin.TypeDescriptor.Patterns)
                {
                    AddDependencyFiles(pattern.Dependencies, dependencyFiles);
                }
            }
        }
        foreach (var conditional in package.Module.ConditionalInstalls)
        {
            AddDependencyFiles(conditional.Dependencies, dependencyFiles);
        }

        var states = new Dictionary<string, FomodFileState>(StringComparer.OrdinalIgnoreCase);
        var mods = workspace.Profile?.Mods
            .Where(mod => !mod.IsSeparator && Directory.Exists(mod.DirectoryPath))
            .ToArray() ?? [];
        foreach (var dependencyFile in dependencyFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeDependencyPath(dependencyFile);
            if (relativePath is null)
            {
                states[dependencyFile] = FomodFileState.Missing;
                continue;
            }

            if (ContainsDependencyFile(workspace.Instance.GamePath, relativePath))
            {
                states[dependencyFile] = FomodFileState.Active;
                continue;
            }

            if (mods.Any(mod => mod.Enabled && ContainsDependencyFile(mod.DirectoryPath, relativePath)))
            {
                states[dependencyFile] = FomodFileState.Active;
            }
            else if (mods.Any(mod => !mod.Enabled && ContainsDependencyFile(mod.DirectoryPath, relativePath)))
            {
                states[dependencyFile] = FomodFileState.Inactive;
            }
            else
            {
                states[dependencyFile] = FomodFileState.Missing;
            }
        }

        return new FomodDependencyContext(
            states,
            GameVersion: "1.5.3",
            FomodVersion: "0.13.21");
    }

    private static void AddDependencyFiles(
        FomodDependency? dependency,
        ISet<string> destination)
    {
        switch (dependency)
        {
            case FomodFileDependency file when !string.IsNullOrWhiteSpace(file.File):
                destination.Add(file.File.Trim());
                break;
            case FomodCompositeDependency composite:
                foreach (var child in composite.Dependencies)
                {
                    AddDependencyFiles(child, destination);
                }
                break;
        }
    }

    private static string? NormalizeDependencyPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return null;
        }
        var normalized = path.Replace('\\', '/').Trim();
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or ".." || segment.Contains(':')))
        {
            return null;
        }
        return string.Join('/', segments);
    }

    private static bool ContainsDependencyFile(string? root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return false;
        }

        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { relativePath };
        if (relativePath.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
        {
            variants.Add(relativePath[5..]);
        }
        if (relativePath.StartsWith("gamedata/", StringComparison.OrdinalIgnoreCase))
        {
            variants.Add(relativePath[9..]);
        }
        else
        {
            variants.Add("gamedata/" + relativePath);
        }

        var fullRoot = Path.GetFullPath(root);
        foreach (var variant in variants)
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(
                    fullRoot,
                    variant.Replace('/', Path.DirectorySeparatorChar)));
                if (candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(candidate))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or ArgumentException
                                               or NotSupportedException)
            {
                // Invalid dependency paths are simply reported to FOMOD as missing.
            }
        }
        return false;
    }

    private static string? DetectSafeImageMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8
            && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            return "image/png";
        }
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
        {
            return "image/jpeg";
        }
        if (bytes.Length >= 6
            && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            return "image/gif";
        }
        if (bytes.Length >= 12
            && bytes[..4].SequenceEqual("RIFF"u8)
            && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        return null;
    }

    private void InvalidateContent()
    {
        _contentIndex = null;
        _contentKey = null;
        _contentModListPath = null;
        _contentModListStamp = 0;
    }

    private static LauncherActionResult OpenDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return new LauncherActionResult(false, $"Каталог не найден: {path}");
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return new LauncherActionResult(true, $"Открыт каталог: {Path.GetFileName(path)}");
    }

    private static string? ChooseDefaultExecutable(
        IReadOnlyList<Mo2ExecutableDefinition> executables,
        LauncherSettings settings)
    {
        var renderer = settings.Renderer.ToUpperInvariant();
        var avx = settings.UseAvx ? "-AVX" : string.Empty;
        var exact = $"Anomaly ({renderer}{avx})";
        return executables.FirstOrDefault(item => string.Equals(item.Title, exact, StringComparison.OrdinalIgnoreCase))?.Title
               ?? executables.FirstOrDefault(item => item.Title.StartsWith($"Anomaly ({renderer}", StringComparison.OrdinalIgnoreCase))?.Title
               ?? (executables.Count > 0 ? executables[0].Title : null);
    }

    private static bool IsRuntimeBusy()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName.Equals("ModOrganizer", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.StartsWith("AnomalyDX", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Процесс успел завершиться между перечислением и чтением имени.
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        _contentGate.Dispose();
        _archiveInstallGate.Dispose();
    }
}
