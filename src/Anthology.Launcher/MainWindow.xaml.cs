using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Anthology.Launcher;

public partial class MainWindow : Window
{
    private readonly EmbeddedMo2HostService _mo2HostService;

    public MainWindow(EmbeddedMo2HostService mo2HostService)
    {
        _mo2HostService = mo2HostService;
        InitializeComponent();

        // MO2 2.5.0 is a Qt application. Reparenting its native window with
        // SetParent makes this particular portable build crash with 0xc0000005.
        // Keep the complete MO2 interface in its own process and window; the
        // integrated profile/mod manager remains available in the MO2 section.
        _mo2HostService.OpenHandler = OpenMo2WindowAsync;
        _mo2HostService.HideHandler = static () => { };

        Closing += (_, _) =>
        {
            _mo2HostService.OpenHandler = null;
            _mo2HostService.HideHandler = null;
        };

        UpdateWindowStateVisuals();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_OnClick(object sender, RoutedEventArgs e) => ToggleMaximizeRestore();

    private void MainWindow_OnStateChanged(object? sender, EventArgs e) => UpdateWindowStateVisuals();

    private void MainWindow_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.F11)
        {
            return;
        }

        ToggleMaximizeRestore();
        e.Handled = true;
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void UpdateWindowStateVisuals()
    {
        if (MaximizeButton is null || WindowFrame is null || TitleBarFrame is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "❐" : "□";
        MaximizeButton.ToolTip = maximized ? "Восстановить" : "Развернуть";
        WindowFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(10);
        TitleBarFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(10, 10, 0, 0);
    }

    private static async Task<LauncherActionResult> OpenMo2WindowAsync(EmbeddedMo2Request request)
    {
        var executable = Path.Combine(request.Root, "ModOrganizer.exe");
        if (!File.Exists(executable))
        {
            return new LauncherActionResult(false, $"ModOrganizer.exe не найден: {executable}");
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(request.Profile)
                && !IsOrganizerRunning())
            {
                Anthology.Mo2.Core.Mo2ProfileManager.SetSelectedProfile(request.Root, request.Profile);
            }

            var startInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = request.Root,
                UseShellExecute = false,
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new LauncherActionResult(false, "Не удалось запустить Mod Organizer 2.");
            }

            // Detect immediate startup failures without ever waiting on the WPF UI thread.
            await Task.Delay(3000).ConfigureAwait(false);
            if (process.HasExited && process.ExitCode != 0)
            {
                return new LauncherActionResult(
                    false,
                    $"Mod Organizer 2 аварийно завершился при запуске (код 0x{process.ExitCode:X8}).");
            }

            return new LauncherActionResult(
                true,
                "Полный Mod Organizer 2 открыт в безопасном отдельном окне; лаунчер остаётся доступен");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
        {
            return new LauncherActionResult(false, exception.Message);
        }
    }

    private static bool IsOrganizerRunning()
    {
        foreach (var process in Process.GetProcessesByName("ModOrganizer"))
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

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
