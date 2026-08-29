using System.Diagnostics;
using System.IO;
using Anthology.Mo2.Core;

namespace Anthology.Launcher;

public sealed record Mo2WorkspaceSnapshot(
    Mo2InstanceSnapshot Instance,
    string? SelectedProfile,
    string? SelectedExecutable,
    Mo2ProfileSnapshot? Profile,
    bool RuntimeBusy);

public sealed class Mo2IntegrationService(
    LauncherSettingsStore settingsStore,
    LauncherBridge launcherBridge) : IDisposable
{
    private readonly SemaphoreSlim _contentGate = new(1, 1);
    private string? _contentKey;
    private Mo2ContentIndex? _contentIndex;

    public Mo2WorkspaceSnapshot GetWorkspace()
    {
        var instance = Mo2ProfileManager.Detect(settingsStore.Current.ModpackRoot);
        if (!instance.Available)
        {
            return new Mo2WorkspaceSnapshot(instance, null, null, null, IsRuntimeBusy());
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
            profileSnapshot = Mo2ProfileManager.ReadProfile(instance.Root, profile);
        }

        return new Mo2WorkspaceSnapshot(instance, profile, executable, profileSnapshot, IsRuntimeBusy());
    }

    public async Task<LauncherActionResult> SaveSelectionAsync(
        string? profile,
        string? executable,
        CancellationToken cancellationToken = default)
    {
        var instance = Mo2ProfileManager.Detect(settingsStore.Current.ModpackRoot);
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

        if (IsRuntimeBusy())
        {
            return new LauncherActionResult(false, "Нельзя менять профиль, пока MO2 или Anomaly запущены");
        }

        try
        {
            Mo2ProfileManager.SetSelectedProfile(instance.Root, profile!);
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

    public async Task<LauncherActionResult> InstallArchiveAsync(
        string archivePath,
        string? installName = null,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        var workspace = GetWorkspace();
        if (!workspace.Instance.Available || workspace.SelectedProfile is null)
        {
            return new LauncherActionResult(false, "Сначала выберите профиль MO2");
        }

        if (workspace.RuntimeBusy)
        {
            return new LauncherActionResult(false, "Нельзя устанавливать мод, пока MO2 или Anomaly запущены");
        }

        try
        {
            var result = await Task.Run(
                () => Mo2ArchiveInstaller.Install(
                    workspace.Instance.Root,
                    workspace.SelectedProfile,
                    archivePath,
                    installName,
                    replaceExisting,
                    cancellationToken),
                cancellationToken);
            if (result.Success)
            {
                InvalidateContent();
            }
            return new LauncherActionResult(result.Success, result.Message);
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    public async Task<LauncherActionResult> CreateProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default)
    {
        var workspace = GetWorkspace();
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
            Mo2ProfileManager.CreateProfile(workspace.Instance.Root, profileName, workspace.SelectedProfile);
            Mo2ProfileManager.SetSelectedProfile(workspace.Instance.Root, profileName);
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
        return index.Browse(relativePath);
    }

    public async Task<IReadOnlyList<Mo2ConflictEntry>> GetConflictsAsync(
        string modName,
        CancellationToken cancellationToken = default)
    {
        var index = await GetContentIndexAsync(false, cancellationToken);
        return index.GetConflicts(modName);
    }

    public async Task<LauncherActionResult> LaunchSelectedAsync(CancellationToken cancellationToken = default)
    {
        var workspace = GetWorkspace();
        if (!workspace.Instance.Available || workspace.SelectedProfile is null || workspace.SelectedExecutable is null)
        {
            return new LauncherActionResult(false, "Сначала назначьте профиль и запуск в разделе MO2");
        }

        if (workspace.RuntimeBusy)
        {
            return new LauncherActionResult(false, "MO2 или Anomaly уже запущены. Закройте текущую сессию перед запуском другого профиля.");
        }

        var prepared = await launcherBridge.PrepareModpackRuntimeAsync(cancellationToken);
        if (!prepared.Success)
        {
            return prepared;
        }

        try
        {
            var organizer = Path.Combine(workspace.Instance.Root, "ModOrganizer.exe");
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
            startInfo.ArgumentList.Add(launcherBridge.GetGameArguments());
            Process.Start(startInfo);
            return new LauncherActionResult(
                true,
                $"Запущена {workspace.SelectedProfile} через скрытый runtime MO2");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
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
        var workspace = GetWorkspace();
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
            _contentIndex = index;
            _contentKey = key;
            return index;
        }
        finally
        {
            _contentGate.Release();
        }
    }

    private void InvalidateContent()
    {
        _contentKey = null;
        _contentIndex = null;
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

    public void Dispose() => _contentGate.Dispose();
}
