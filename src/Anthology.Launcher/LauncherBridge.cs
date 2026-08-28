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

public sealed class LauncherBridge
{
    private const string ModpackFolder = "SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT";
    private readonly string? _configuredGameRoot = Environment.GetEnvironmentVariable("ANTHOLOGY_GAME_ROOT");

    public InstallationStatus DetectInstallation()
    {
        var gameRoot = FindGameRoot();
        if (gameRoot is null)
        {
            return new InstallationStatus(false, false, null, null, "Укажите папку Anthology в настройках");
        }

        var modpackRoot = Path.Combine(Directory.GetParent(gameRoot)?.FullName ?? gameRoot, ModpackFolder);
        var mo2 = Path.Combine(modpackRoot, "ModOrganizer.exe");
        var mo2Found = File.Exists(mo2);
        return new InstallationStatus(
            true,
            mo2Found,
            gameRoot,
            mo2Found ? modpackRoot : null,
            mo2Found ? "Сборка готова к запуску" : "Игра найдена, Mod Organizer 2 отсутствует");
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
        if (!string.IsNullOrWhiteSpace(_configuredGameRoot))
        {
            var configuredFullPath = Path.GetFullPath(_configuredGameRoot);
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

    private static bool IsGameRoot(string path) =>
        File.Exists(Path.Combine(path, "fsgame.ltx"))
        && Directory.Exists(Path.Combine(path, "bin"));
}
