using System.Diagnostics;
using System.IO;

namespace Anthology.Launcher;

public sealed record InstallationStatus(
    bool GameFound,
    bool ModOrganizerFound,
    string? GameRoot,
    string? ModpackRoot,
    string StatusText,
    bool OriginalGameFound = false,
    bool OnlineModeFound = false,
    bool RelayChatFound = false);

public sealed record LauncherActionResult(bool Success, string Message);

public sealed class LauncherBridge(LauncherSettingsStore settingsStore)
{
    private const string ModpackFolder = "SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT";
    private const string RelayChatExecutable = "Chernobyl Relay Chat.exe";
    private static readonly int[] ShadowMapSizes = [1536, 2048, 2560, 3072, 4096];

    public InstallationStatus DetectInstallation()
    {
        var gameRoot = FindGameRoot();
        if (gameRoot is null)
        {
            return new InstallationStatus(false, false, null, null, "Укажите папку Anthology в разделе «Установка»");
        }

        var modpackRoot = FindModpackRoot(gameRoot);
        var mo2Found = modpackRoot is not null && File.Exists(Path.Combine(modpackRoot, "ModOrganizer.exe"));
        var originalFound = File.Exists(GetSelectedGameExecutable(gameRoot, settingsStore.Current));
        var onlineFound = File.Exists(Path.Combine(gameRoot, "Lan_anthology.bat"));
        var relayChatFound = File.Exists(Path.Combine(gameRoot, RelayChatExecutable));
        return new InstallationStatus(
            true,
            mo2Found,
            gameRoot,
            mo2Found ? modpackRoot : null,
            mo2Found ? "Сборка готова к запуску" : "Игра найдена, Mod Organizer 2 отсутствует",
            originalFound,
            onlineFound,
            relayChatFound);
    }

    public async Task<LauncherActionResult> SelectGameRootAsync(CancellationToken cancellationToken = default)
    {
        var selected = LauncherDialogService.SelectFolder("Выберите корневую папку Anomaly с fsgame.ltx", settingsStore.Current.GameRoot);
        if (selected is null)
        {
            return new LauncherActionResult(false, "Выбор папки отменён");
        }

        var fullPath = Path.GetFullPath(selected);
        if (!IsGameRoot(fullPath))
        {
            return new LauncherActionResult(false, "В выбранной папке нет fsgame.ltx и каталога bin");
        }

        var settings = settingsStore.Current.Copy();
        settings.GameRoot = fullPath;
        await settingsStore.SaveAsync(settings, cancellationToken);
        return new LauncherActionResult(true, "Папка игры сохранена");
    }

    public async Task<LauncherActionResult> SelectModpackRootAsync(CancellationToken cancellationToken = default)
    {
        var selected = LauncherDialogService.SelectFolder("Выберите папку Mod Organizer 2", settingsStore.Current.ModpackRoot);
        if (selected is null)
        {
            return new LauncherActionResult(false, "Выбор папки отменён");
        }

        var fullPath = Path.GetFullPath(selected);
        if (!File.Exists(Path.Combine(fullPath, "ModOrganizer.exe")))
        {
            return new LauncherActionResult(false, "В выбранной папке нет ModOrganizer.exe");
        }

        var settings = settingsStore.Current.Copy();
        settings.ModpackRoot = fullPath;
        await settingsStore.SaveAsync(settings, cancellationToken);
        return new LauncherActionResult(true, "Папка Mod Organizer 2 сохранена");
    }

    public async Task<LauncherActionResult> LaunchModpackAsync(CancellationToken cancellationToken = default)
    {
        var status = DetectInstallation();
        if (!status.ModOrganizerFound || status.ModpackRoot is null)
        {
            return new LauncherActionResult(false, status.StatusText);
        }

        try
        {
            await PrepareGameLaunchAsync(status.GameRoot!, cancellationToken);
            StartRelayChatIfEnabled(status.GameRoot!);
            var executable = Path.Combine(status.ModpackRoot, "ModOrganizer.exe");
            Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = status.ModpackRoot,
                UseShellExecute = true,
            });
            return new LauncherActionResult(true, "Mod Organizer 2 запущен");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    public async Task<LauncherActionResult> LaunchOriginalAsync(CancellationToken cancellationToken = default)
    {
        var status = DetectInstallation();
        if (!status.GameFound || status.GameRoot is null)
        {
            return new LauncherActionResult(false, status.StatusText);
        }

        var executable = GetSelectedGameExecutable(status.GameRoot, settingsStore.Current);
        if (!File.Exists(executable))
        {
            return new LauncherActionResult(false, $"Исполняемый файл выбранного рендера не найден: {executable}");
        }

        try
        {
            await PrepareGameLaunchAsync(status.GameRoot, cancellationToken);
            StartRelayChatIfEnabled(status.GameRoot);
            Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = true,
            });
            return new LauncherActionResult(true, "Оригинальная Anomaly запущена");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    public async Task<LauncherActionResult> LaunchOnlineAsync(CancellationToken cancellationToken = default)
    {
        var status = DetectInstallation();
        if (!status.GameFound || status.GameRoot is null)
        {
            return new LauncherActionResult(false, status.StatusText);
        }

        var launcher = Path.Combine(status.GameRoot, "Lan_anthology.bat");
        if (!File.Exists(launcher))
        {
            return new LauncherActionResult(false, "Файл онлайн-режима Lan_anthology.bat не найден в корне игры");
        }

        try
        {
            await PrepareGameLaunchAsync(status.GameRoot, cancellationToken);
            StartRelayChatIfEnabled(status.GameRoot);
            Process.Start(new ProcessStartInfo(launcher)
            {
                WorkingDirectory = status.GameRoot,
                UseShellExecute = true,
            });
            return new LauncherActionResult(true, "Онлайн-режим Anthology запущен");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    public LauncherActionResult LaunchRelayChat()
    {
        var status = DetectInstallation();
        if (!status.GameFound || status.GameRoot is null)
        {
            return new LauncherActionResult(false, status.StatusText);
        }

        return StartRelayChat(status.GameRoot, showAlreadyRunning: true);
    }

    public LauncherActionResult EnsureRelayChatStarted()
    {
        if (!settingsStore.Current.RelayChatAlways)
        {
            return new LauncherActionResult(true, string.Empty);
        }

        var status = DetectInstallation();
        if (!status.GameFound || status.GameRoot is null)
        {
            return new LauncherActionResult(false, status.StatusText);
        }

        return StartRelayChat(status.GameRoot, showAlreadyRunning: false);
    }

    public static bool IsRelayChatRunning()
    {
        foreach (var process in Process.GetProcessesByName("Chernobyl Relay Chat"))
        {
            using (process)
            {
                if (!process.HasExited)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public async Task<LauncherActionResult> PrepareModpackRuntimeAsync(CancellationToken cancellationToken = default)
    {
        var status = DetectInstallation();
        if (!status.ModOrganizerFound || status.GameRoot is null)
        {
            return new LauncherActionResult(false, status.StatusText);
        }

        try
        {
            await PrepareGameLaunchAsync(status.GameRoot, cancellationToken);
            StartRelayChatIfEnabled(status.GameRoot);
            return new LauncherActionResult(true, "Параметры Anomaly подготовлены");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    public LauncherActionResult OpenGameFolder()
    {
        var status = DetectInstallation();
        if (!status.GameFound || status.GameRoot is null)
        {
            return new LauncherActionResult(false, status.StatusText);
        }

        Process.Start(new ProcessStartInfo(status.GameRoot) { UseShellExecute = true });
        return new LauncherActionResult(true, "Папка игры открыта");
    }

    private string? FindGameRoot()
    {
        var configured = settingsStore.Current.GameRoot;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable("ANTHOLOGY_GAME_ROOT");
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredFullPath = Path.GetFullPath(configured);
            if (IsGameRoot(configuredFullPath))
            {
                return configuredFullPath;
            }
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; current is not null && level < 8; level++, current = current.Parent)
        {
            if (IsGameRoot(current.FullName))
            {
                return current.FullName;
            }
        }

        return null;
    }

    private string? FindModpackRoot(string gameRoot)
    {
        if (!string.IsNullOrWhiteSpace(settingsStore.Current.ModpackRoot))
        {
            var configured = Path.GetFullPath(settingsStore.Current.ModpackRoot);
            if (File.Exists(Path.Combine(configured, "ModOrganizer.exe")))
            {
                return configured;
            }
        }

        var sibling = Path.Combine(Directory.GetParent(gameRoot)?.FullName ?? gameRoot, ModpackFolder);
        return File.Exists(Path.Combine(sibling, "ModOrganizer.exe")) ? sibling : null;
    }

    private static bool IsGameRoot(string path) =>
        File.Exists(Path.Combine(path, "fsgame.ltx"))
        && Directory.Exists(Path.Combine(path, "bin"));

    private async Task PrepareGameLaunchAsync(string gameRoot, CancellationToken cancellationToken)
    {
        var settings = settingsStore.Current;
        var shadowMap = ShadowMapSizes.Contains(settings.ShadowMapSize) ? settings.ShadowMapSize : 1536;
        var configLines = new[]
        {
            settings.Renderer,
            settings.DebugMode ? "DBG" : "NODBG",
            Array.IndexOf(ShadowMapSizes, shadowMap).ToString(System.Globalization.CultureInfo.InvariantCulture),
            settings.SoundWorkaround ? "SNDFIX" : "NOSNDFIX",
            settings.PrefetchSounds ? "SNDPREFETCH" : "NOSNDPREFETCH",
            "RU",
            settings.UseAvx ? "AVX" : "NOAVX",
            settings.RelayChatAlways ? "CHATRELAYALWAYS" : "NOCHATRELAYALWAYS",
        };
        await File.WriteAllLinesAsync(Path.Combine(gameRoot, "AnomalyLauncher.cfg"), configLines, cancellationToken);

        var commandLine = new List<string> { $"-smap{shadowMap}" };
        if (settings.DebugMode)
        {
            commandLine.Add("-dbg");
        }

        if (settings.PrefetchSounds)
        {
            commandLine.Add("-prefetch_sounds");
        }

        await File.WriteAllLinesAsync(Path.Combine(gameRoot, "commandline.txt"), commandLine, cancellationToken);
        ApplySoundWorkaround(gameRoot, settings.SoundWorkaround);
        if (settings.ResetUserLtx)
        {
            ResetUserConfiguration(gameRoot);
            var updated = settings.Copy();
            updated.ResetUserLtx = false;
            await settingsStore.SaveAsync(updated, cancellationToken);
        }
    }

    private static string GetSelectedGameExecutable(string gameRoot, LauncherSettings settings)
    {
        var renderer = settings.Renderer.ToUpperInvariant() is "DX11" or "DX10" or "DX9" or "DX8"
            ? settings.Renderer.ToUpperInvariant()
            : "DX11";
        var suffix = settings.UseAvx ? "AVX" : string.Empty;
        return Path.Combine(gameRoot, "bin", $"Anomaly{renderer}{suffix}.exe");
    }

    private static void ApplySoundWorkaround(string gameRoot, bool enabled)
    {
        var active = Path.Combine(gameRoot, "bin", "alsoft.ini");
        var backup = active + ".bak";
        if (enabled && File.Exists(active))
        {
            File.Move(active, backup, true);
        }
        else if (!enabled && !File.Exists(active) && File.Exists(backup))
        {
            File.Move(backup, active);
        }
    }

    private static void ResetUserConfiguration(string gameRoot)
    {
        var appData = Path.Combine(gameRoot, "appdata");
        Directory.CreateDirectory(appData);
        var user = Path.Combine(appData, "user.ltx");
        if (File.Exists(user))
        {
            File.Move(user, user + $".backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}", true);
        }

        var bundledDefault = Path.Combine(AppContext.BaseDirectory, "Defaults", "user.ltx");
        if (File.Exists(bundledDefault))
        {
            File.Copy(bundledDefault, user, true);
        }
    }

    private void StartRelayChatIfEnabled(string gameRoot)
    {
        if (settingsStore.Current.RelayChatAlways)
        {
            StartRelayChat(gameRoot, showAlreadyRunning: false);
        }
    }

    private static LauncherActionResult StartRelayChat(string gameRoot, bool showAlreadyRunning)
    {
        if (IsRelayChatRunning())
        {
            return new LauncherActionResult(true, showAlreadyRunning ? "Реальный Чат уже запущен" : string.Empty);
        }

        var executable = Path.Combine(gameRoot, RelayChatExecutable);
        if (!File.Exists(executable))
        {
            return new LauncherActionResult(false, $"Реальный Чат не найден в корне игры: {executable}");
        }

        try
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = gameRoot,
                UseShellExecute = true,
            });
            return new LauncherActionResult(true, "Реальный Чат запущен из корня игры");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }
}
