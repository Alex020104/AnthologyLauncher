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

public sealed class LauncherBridge(LauncherSettingsStore settingsStore, RelayChatClient relayChat)
{
    private static readonly string[] ModpackFolders =
    [
        "Modpack-1.5.3- Anthology 2.1",
        "SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT",
    ];
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
        var relayChatFound = Directory.Exists(Path.Combine(gameRoot, "gamedata", "configs"));
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

    public async Task<InstallationStatus> AutoConfigureInstallationAsync(CancellationToken cancellationToken = default)
    {
        var detected = DetectInstallation();
        var settings = settingsStore.Current.Copy();
        var changed = false;

        if (detected.GameRoot is not null
            && (!settings.GameRootManualOverride || !IsGameRoot(settings.GameRoot)))
        {
            changed |= !PathEquals(settings.GameRoot, detected.GameRoot) || settings.GameRootManualOverride;
            settings.GameRoot = detected.GameRoot;
            settings.GameRootManualOverride = false;
        }

        if (detected.ModpackRoot is not null
            && (!settings.ModpackRootManualOverride || !IsModpackRoot(settings.ModpackRoot)))
        {
            changed |= !PathEquals(settings.ModpackRoot, detected.ModpackRoot) || settings.ModpackRootManualOverride;
            settings.ModpackRoot = detected.ModpackRoot;
            settings.ModpackRootManualOverride = false;
        }

        if (changed)
        {
            await settingsStore.SaveAsync(settings, cancellationToken);
            detected = DetectInstallation();
        }

        return detected;
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
        settings.GameRootManualOverride = true;
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
        settings.ModpackRootManualOverride = true;
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
            await StartRelayChatIfEnabledAsync(status.GameRoot!, cancellationToken);
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
            await StartRelayChatIfEnabledAsync(status.GameRoot, cancellationToken);
            Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = true,
                Arguments = BuildGameArguments(settingsStore.Current),
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
            await StartRelayChatIfEnabledAsync(status.GameRoot, cancellationToken);
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

    public async Task<LauncherActionResult> LaunchRelayChatAsync(CancellationToken cancellationToken = default)
    {
        var status = DetectInstallation();
        if (!status.GameFound || status.GameRoot is null)
        {
            return new LauncherActionResult(false, status.StatusText);
        }

        return await relayChat.EnsureStartedAsync(status.GameRoot, settingsStore.Current, cancellationToken);
    }

    public async Task<LauncherActionResult> EnsureRelayChatStartedAsync(CancellationToken cancellationToken = default)
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

        return await relayChat.EnsureStartedAsync(status.GameRoot, settingsStore.Current, cancellationToken);
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
            await StartRelayChatIfEnabledAsync(status.GameRoot, cancellationToken);
            return new LauncherActionResult(true, "Параметры Anomaly подготовлены");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    public string GetGameArguments() => BuildGameArguments(settingsStore.Current);

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

    public static LauncherActionResult OpenExternalUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return new LauncherActionResult(false, "Некорректная внешняя ссылка");
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return new LauncherActionResult(true, "Ссылка открыта в браузере");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new LauncherActionResult(false, $"Не удалось открыть браузер: {exception.Message}");
        }
    }

    public static Task<LauncherActionResult> OpenYoutubeLoginAsync()
    {
        try
        {
            var application = System.Windows.Application.Current;
            if (application?.MainWindow is not MainWindow window)
            {
                return Task.FromResult(new LauncherActionResult(false, "Главное окно лаунчера недоступно."));
            }

            return application.Dispatcher.CheckAccess()
                ? window.OpenYoutubeLoginAsync()
                : application.Dispatcher.Invoke(window.OpenYoutubeLoginAsync);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or System.Runtime.InteropServices.COMException)
        {
            return Task.FromResult(new LauncherActionResult(false, $"Не удалось открыть вход в YouTube: {exception.Message}"));
        }
    }

    private string? FindGameRoot()
    {
        var environmentRoot = Environment.GetEnvironmentVariable("ANTHOLOGY_GAME_ROOT");
        if (IsGameRoot(environmentRoot))
        {
            return Path.GetFullPath(environmentRoot!);
        }

        var configured = settingsStore.Current.GameRoot;
        if (settingsStore.Current.GameRootManualOverride && IsGameRoot(configured))
        {
            return Path.GetFullPath(configured!);
        }

        var packaged = FindNearbyRoot(AppContext.BaseDirectory, IsGameRoot, includeSiblingFolders: true);
        if (packaged is not null)
        {
            return packaged;
        }

        return IsGameRoot(configured) ? Path.GetFullPath(configured!) : null;
    }

    private string? FindModpackRoot(string gameRoot)
    {
        var environmentRoot = Environment.GetEnvironmentVariable("ANTHOLOGY_MO2_ROOT");
        if (IsModpackRoot(environmentRoot))
        {
            return Path.GetFullPath(environmentRoot!);
        }

        var configuredRoot = settingsStore.Current.ModpackRoot;
        if (settingsStore.Current.ModpackRootManualOverride && IsModpackRoot(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot!);
        }

        var parent = Directory.GetParent(gameRoot)?.FullName ?? gameRoot;
        foreach (var folder in ModpackFolders)
        {
            var sibling = Path.Combine(parent, folder);
            if (IsModpackRoot(sibling))
            {
                return sibling;
            }
        }

        foreach (var anchor in new[] { gameRoot, AppContext.BaseDirectory })
        {
            var packaged = FindNearbyRoot(anchor, IsModpackRoot, includeSiblingFolders: true);
            if (packaged is not null)
            {
                return packaged;
            }
        }

        return IsModpackRoot(configuredRoot) ? Path.GetFullPath(configuredRoot!) : null;
    }

    private static string? FindNearbyRoot(
        string anchor,
        Func<string?, bool> predicate,
        bool includeSiblingFolders)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DirectoryInfo? current;
        try { current = new DirectoryInfo(Path.GetFullPath(anchor)); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }

        for (var level = 0; current is not null && level < 8; level++, current = current.Parent)
        {
            if (visited.Add(current.FullName) && predicate(current.FullName))
            {
                return current.FullName;
            }

            if (!includeSiblingFolders)
            {
                continue;
            }

            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(current.FullName).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var match = directories
                .Where(path => visited.Add(path) && predicate(path))
                .OrderByDescending(PackagedRootScore)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static int PackagedRootScore(string path)
    {
        var name = Path.GetFileName(path);
        var score = 0;
        if (name.Contains("anthology", StringComparison.OrdinalIgnoreCase)) score += 4;
        if (name.Contains("modpack", StringComparison.OrdinalIgnoreCase)) score += 3;
        if (name.Contains("mo2", StringComparison.OrdinalIgnoreCase)) score += 2;
        if (name.Contains("anomaly", StringComparison.OrdinalIgnoreCase)) score += 1;
        return score;
    }

    private static bool IsGameRoot(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && File.Exists(Path.Combine(path, "fsgame.ltx"))
        && Directory.Exists(Path.Combine(path, "bin"));

    private static bool IsModpackRoot(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && File.Exists(Path.Combine(path, "ModOrganizer.exe"));

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

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
        await File.WriteAllTextAsync(
            Path.Combine(gameRoot, "commandline.txt"),
            BuildGameArguments(settings) + Environment.NewLine,
            cancellationToken);

        ApplySoundWorkaround(gameRoot, settings.SoundWorkaround);
        if (settings.ResetUserLtx)
        {
            ResetUserConfiguration(gameRoot);
            var updated = settings.Copy();
            updated.ResetUserLtx = false;
            await settingsStore.SaveAsync(updated, cancellationToken);
        }
    }

    private static string BuildGameArguments(LauncherSettings settings)
    {
        var shadowMap = ShadowMapSizes.Contains(settings.ShadowMapSize) ? settings.ShadowMapSize : 1536;
        var arguments = new List<string> { $"-smap{shadowMap}" };
        if (settings.DebugMode)
        {
            arguments.Add("-dbg");
        }
        if (settings.PrefetchSounds)
        {
            arguments.Add("-prefetch_sounds");
        }
        return string.Join(' ', arguments);
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

    private async Task StartRelayChatIfEnabledAsync(string gameRoot, CancellationToken cancellationToken)
    {
        if (settingsStore.Current.RelayChatAlways)
        {
            await relayChat.EnsureStartedAsync(gameRoot, settingsStore.Current, cancellationToken);
        }
    }
}
