using System.Diagnostics;
using System.IO;

namespace Anthology.Launcher;

public sealed record InstallationStatus(
    bool GameFound,
    bool ModOrganizerFound,
    string? GameRoot,
    string? ModpackRoot,
    string StatusText);

public sealed record LauncherActionResult(bool Success, string Message);

public sealed class LauncherBridge(LauncherSettingsStore settingsStore)
{
    private const string ModpackFolder = "SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT";

    public InstallationStatus DetectInstallation()
    {
        var gameRoot = FindGameRoot();
        if (gameRoot is null)
        {
            return new InstallationStatus(false, false, null, null, "Укажите папку Anthology в настройках");
        }

        var modpackRoot = FindModpackRoot(gameRoot);
        if (modpackRoot is null)
        {
            return new InstallationStatus(
                true,
                false,
                gameRoot,
                null,
                "Игра найдена — укажите папку Mod Organizer 2");
        }

        var mo2 = Path.Combine(modpackRoot, "ModOrganizer.exe");
        var mo2Found = File.Exists(mo2);
        return new InstallationStatus(
            true,
            mo2Found,
            gameRoot,
            mo2Found ? modpackRoot : null,
            mo2Found ? "Сборка готова к запуску" : "Игра найдена, Mod Organizer 2 отсутствует");
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

    public Task<LauncherActionResult> LaunchModpackAsync()
    {
        var status = DetectInstallation();
        if (!status.ModOrganizerFound || status.ModpackRoot is null)
        {
            return Task.FromResult(new LauncherActionResult(false, status.StatusText));
        }

        var executable = Path.Combine(status.ModpackRoot, "ModOrganizer.exe");
        Process.Start(new ProcessStartInfo(executable)
        {
            WorkingDirectory = status.ModpackRoot,
            UseShellExecute = true,
        });
        return Task.FromResult(new LauncherActionResult(true, "Mod Organizer 2 запущен"));
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
}
