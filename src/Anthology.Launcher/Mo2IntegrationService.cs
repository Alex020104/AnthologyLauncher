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
    LauncherBridge launcherBridge)
{
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

        var settings = settingsStore.Current.Copy();
        settings.SelectedMo2Profile = profile;
        settings.SelectedMo2Executable = executable;
        await settingsStore.SaveAsync(settings, cancellationToken);
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
            var startInfo = new ProcessStartInfo(organizer)
            {
                WorkingDirectory = workspace.Instance.Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(workspace.SelectedProfile);
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(workspace.SelectedExecutable);
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
}
