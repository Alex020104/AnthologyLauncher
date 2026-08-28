using System.Diagnostics;
using System.IO;

namespace Anthology.Launcher;

public sealed record SetupLauncherStatus(
    bool Available,
    string SetupRoot,
    string? ExecutablePath,
    bool BinaryPayloadFound,
    string StatusText);

public sealed class SetupLauncherService(LauncherSettingsStore settingsStore)
{
    private static readonly string[] PreferredNames =
    [
        "AnthologySetup.exe",
        "Setup.exe",
        "setup.exe",
        "AnthologySetup.msi",
    ];

    private readonly string _setupRoot = Path.Combine(AppContext.BaseDirectory, "Setup");

    public SetupLauncherStatus Detect()
    {
        var configured = settingsStore.Current.SetupExecutable;
        var executable = IsSupportedInstaller(configured) && File.Exists(configured)
            ? Path.GetFullPath(configured!)
            : FindBundledSetup();
        var payloadFound = Directory.Exists(Path.Combine(_setupRoot, "bin"))
                           || Directory.Exists(Path.Combine(_setupRoot, "Payload", "bin"));
        return new SetupLauncherStatus(
            executable is not null,
            _setupRoot,
            executable,
            payloadFound,
            executable is null
                ? "Положите Setup.exe в папку Setup рядом с лаунчером или выберите установщик вручную"
                : payloadFound
                    ? "Setup и комплект bin найдены"
                    : "Setup найден; bin может быть встроен в установщик или добавлен в Setup\\Payload\\bin");
    }

    public async Task<LauncherActionResult> SelectAsync(CancellationToken cancellationToken = default)
    {
        var selected = LauncherDialogService.SelectFile(
            "Выберите установщик Anthology",
            "Установщик (*.exe;*.msi)|*.exe;*.msi",
            settingsStore.Current.SetupExecutable ?? _setupRoot);
        if (selected is null)
        {
            return new LauncherActionResult(false, "Выбор установщика отменён");
        }

        if (!IsSupportedInstaller(selected))
        {
            return new LauncherActionResult(false, "Разрешены только установщики EXE и MSI");
        }

        var settings = settingsStore.Current.Copy();
        settings.SetupExecutable = Path.GetFullPath(selected);
        await settingsStore.SaveAsync(settings, cancellationToken);
        return new LauncherActionResult(true, "Путь к Setup сохранён");
    }

    public LauncherActionResult Launch()
    {
        var status = Detect();
        if (!status.Available || status.ExecutablePath is null)
        {
            return new LauncherActionResult(false, status.StatusText);
        }

        try
        {
            var extension = Path.GetExtension(status.ExecutablePath);
            ProcessStartInfo startInfo;
            if (string.Equals(extension, ".msi", StringComparison.OrdinalIgnoreCase))
            {
                startInfo = new ProcessStartInfo("msiexec.exe")
                {
                    WorkingDirectory = Path.GetDirectoryName(status.ExecutablePath)!,
                    UseShellExecute = false,
                };
                startInfo.ArgumentList.Add("/i");
                startInfo.ArgumentList.Add(status.ExecutablePath);
            }
            else
            {
                startInfo = new ProcessStartInfo(status.ExecutablePath)
                {
                    WorkingDirectory = Path.GetDirectoryName(status.ExecutablePath)!,
                    UseShellExecute = true,
                };
            }

            Process.Start(startInfo);
            return new LauncherActionResult(true, "Обычный установщик Anthology запущен");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    private string? FindBundledSetup()
    {
        foreach (var name in PreferredNames)
        {
            var candidate = Path.Combine(_setupRoot, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (!Directory.Exists(_setupRoot))
        {
            return null;
        }

        return Directory.GetFiles(_setupRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedInstaller)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsSupportedInstaller(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".msi";
}
