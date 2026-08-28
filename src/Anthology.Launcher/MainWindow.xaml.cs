using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Anthology.Launcher;

public partial class MainWindow : Window
{
    private const int GwlStyle = -16;
    private const long WsChild = 0x40000000L;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsSystemMenu = 0x00080000L;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpFrameChanged = 0x0020;
    private const int SwRestore = 9;

    private readonly EmbeddedMo2HostService _mo2HostService;
    private Process? _embeddedMo2Process;
    private EmbeddedMo2Request? _lastMo2Request;
    private string? _embeddedMo2Root;
    private IntPtr _embeddedMo2Window;
    private IntPtr _originalMo2Style;
    private WindowPlacement _originalMo2Placement;
    private bool _hasOriginalMo2Placement;

    public MainWindow(EmbeddedMo2HostService mo2HostService)
    {
        _mo2HostService = mo2HostService;
        InitializeComponent();
        _mo2HostService.OpenHandler = OpenEmbeddedMo2Async;
        _mo2HostService.HideHandler = HideEmbeddedMo2;
        Mo2NativePanel.BackColor = System.Drawing.Color.FromArgb(9, 12, 15);
        Mo2NativePanel.Resize += (_, _) => ResizeEmbeddedMo2();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Closing += (_, _) => DetachEmbeddedMo2();
        UpdateWindowStateVisuals();
        UpdateResponsiveLayout();
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
        UpdateResponsiveLayout();
    }

    private async Task<LauncherActionResult> OpenEmbeddedMo2Async(EmbeddedMo2Request request)
    {
        if (!Dispatcher.CheckAccess())
        {
            return await Dispatcher.InvokeAsync(() => OpenEmbeddedMo2Async(request)).Task.Unwrap();
        }

        _lastMo2Request = request;
        var executable = Path.Combine(request.Root, "ModOrganizer.exe");
        if (!File.Exists(executable))
        {
            return new LauncherActionResult(false, $"ModOrganizer.exe не найден: {executable}");
        }

        if (_embeddedMo2Window != IntPtr.Zero && IsWindow(_embeddedMo2Window))
        {
            if (!string.Equals(_embeddedMo2Root, request.Root, StringComparison.OrdinalIgnoreCase))
            {
                return new LauncherActionResult(false, "Сначала закройте уже встроенный MO2 перед подключением другой сборки");
            }

            Mo2HostOverlay.Visibility = Visibility.Visible;
            UpdateResponsiveLayout();
            ResizeEmbeddedMo2();
            SetMo2HostStatus($"MO2 ВСТРОЕН · {request.Profile ?? "ТЕКУЩИЙ ПРОФИЛЬ"}", true);
            return new LauncherActionResult(true, "Полный интерфейс MO2 снова открыт внутри лаунчера");
        }

        Mo2HostOverlay.Visibility = Visibility.Visible;
        SetMo2HostStatus("ПОДКЛЮЧЕНИЕ ПОЛНОГО MO2", false);
        UpdateResponsiveLayout();
        Mo2NativePanel.CreateControl();

        try
        {
            var process = GetAttachedOrRunningOrganizer(executable);
            if (process is null)
            {
                var startInfo = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = request.Root,
                    UseShellExecute = false,
                };
                if (!string.IsNullOrWhiteSpace(request.Profile))
                {
                    startInfo.ArgumentList.Add("-p");
                    startInfo.ArgumentList.Add(request.Profile);
                }

                process = Process.Start(startInfo);
            }

            if (process is null)
            {
                throw new InvalidOperationException("Не удалось запустить Mod Organizer 2.");
            }

            _embeddedMo2Process = process;
            process.EnableRaisingEvents = true;
            process.Exited -= EmbeddedMo2Process_OnExited;
            process.Exited += EmbeddedMo2Process_OnExited;

            var window = await WaitForMainWindowAsync(process);
            if (window == IntPtr.Zero)
            {
                throw new InvalidOperationException("MO2 запущен, но его главное окно не появилось за 20 секунд.");
            }

            AttachMo2Window(window);
            SetMo2HostStatus($"MO2 ВСТРОЕН · {request.Profile ?? "ТЕКУЩИЙ ПРОФИЛЬ"}", true);
            return new LauncherActionResult(true, "Полный интерфейс MO2 подключён внутри лаунчера");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
        {
            SetMo2HostStatus("НЕ УДАЛОСЬ ПОДКЛЮЧИТЬ MO2", false);
            return new LauncherActionResult(false, exception.Message);
        }
    }

    private Process? GetAttachedOrRunningOrganizer(string executable)
    {
        if (_embeddedMo2Process is { HasExited: false })
        {
            return _embeddedMo2Process;
        }

        var expected = Path.GetFullPath(executable);
        foreach (var process in Process.GetProcessesByName("ModOrganizer"))
        {
            try
            {
                var actual = process.MainModule?.FileName;
                if (actual is not null
                    && string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return process;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                               or System.ComponentModel.Win32Exception
                                               or NotSupportedException)
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static async Task<IntPtr> WaitForMainWindowAsync(Process process)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (process.HasExited)
            {
                return IntPtr.Zero;
            }

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }

            await Task.Delay(100);
        }

        return IntPtr.Zero;
    }

    private void AttachMo2Window(IntPtr window)
    {
        _embeddedMo2Window = window;
        _embeddedMo2Root = _lastMo2Request?.Root;
        _originalMo2Style = GetWindowLongPointer(window, GwlStyle);
        _originalMo2Placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        _hasOriginalMo2Placement = GetWindowPlacement(window, ref _originalMo2Placement);
        ShowWindow(window, SwRestore);
        var style = _originalMo2Style.ToInt64();
        style &= ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSystemMenu);
        style |= WsChild;

        SetParent(window, Mo2NativePanel.Handle);
        SetWindowLongPointer(window, GwlStyle, new IntPtr(style));
        SetWindowPos(window, IntPtr.Zero, 0, 0, Mo2NativePanel.ClientSize.Width, Mo2NativePanel.ClientSize.Height, SwpNoZOrder | SwpFrameChanged);
        ResizeEmbeddedMo2();
    }

    private void ResizeEmbeddedMo2()
    {
        if (_embeddedMo2Window == IntPtr.Zero || !IsWindow(_embeddedMo2Window))
        {
            return;
        }

        MoveWindow(
            _embeddedMo2Window,
            0,
            0,
            Math.Max(1, Mo2NativePanel.ClientSize.Width),
            Math.Max(1, Mo2NativePanel.ClientSize.Height),
            true);
    }

    private void UpdateResponsiveLayout()
    {
        if (Mo2HostOverlay is null)
        {
            return;
        }

        var sidebarWidth = ActualWidth <= 900 ? 72 : ActualWidth <= 1240 ? 220 : 248;
        var headerHeight = ActualHeight <= 760 ? 70 : 82;
        Mo2HostOverlay.Margin = new Thickness(sidebarWidth, headerHeight, 25, 0);
        ResizeEmbeddedMo2();
    }

    private void HideEmbeddedMo2()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(HideEmbeddedMo2);
            return;
        }

        Mo2HostOverlay.Visibility = Visibility.Collapsed;
    }

    private async void ReconnectMo2_OnClick(object sender, RoutedEventArgs e)
    {
        if (_lastMo2Request is null)
        {
            SetMo2HostStatus("СНАЧАЛА ОТКРОЙТЕ РАЗДЕЛ MO2", false);
            return;
        }

        var result = await OpenEmbeddedMo2Async(_lastMo2Request);
        if (!result.Success)
        {
            SetMo2HostStatus(result.Message.ToUpperInvariant(), false);
        }
    }

    private void ShowQuickMo2_OnClick(object sender, RoutedEventArgs e) => HideEmbeddedMo2();

    private void EmbeddedMo2Process_OnExited(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            _embeddedMo2Window = IntPtr.Zero;
            _embeddedMo2Process?.Dispose();
            _embeddedMo2Process = null;
            _embeddedMo2Root = null;
            SetMo2HostStatus("MO2 ЗАВЕРШЁН · МОЖНО ПЕРЕПОДКЛЮЧИТЬ", false);
        });
    }

    private void SetMo2HostStatus(string text, bool ready)
    {
        Mo2HostStatusText.Text = text;
        Mo2HostStatusDot.Fill = new SolidColorBrush(ready
            ? System.Windows.Media.Color.FromRgb(109, 161, 116)
            : System.Windows.Media.Color.FromRgb(179, 142, 76));
    }

    private void DetachEmbeddedMo2()
    {
        _mo2HostService.OpenHandler = null;
        _mo2HostService.HideHandler = null;
        if (_embeddedMo2Process is not null)
        {
            _embeddedMo2Process.Exited -= EmbeddedMo2Process_OnExited;
            _embeddedMo2Process.Dispose();
            _embeddedMo2Process = null;
        }

        if (_embeddedMo2Window == IntPtr.Zero || !IsWindow(_embeddedMo2Window))
        {
            return;
        }

        SetParent(_embeddedMo2Window, IntPtr.Zero);
        SetWindowLongPointer(_embeddedMo2Window, GwlStyle, _originalMo2Style);
        SetWindowPos(_embeddedMo2Window, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
        if (_hasOriginalMo2Placement)
        {
            SetWindowPlacement(_embeddedMo2Window, ref _originalMo2Placement);
        }
        else
        {
            ShowWindow(_embeddedMo2Window, SwRestore);
        }
        _embeddedMo2Window = IntPtr.Zero;
        _embeddedMo2Root = null;
        _hasOriginalMo2Placement = false;
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(IntPtr window, ref WindowPlacement placement);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPointer64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPointer32(IntPtr window, int index);

    private static IntPtr GetWindowLongPointer(IntPtr window, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPointer64(window, index) : GetWindowLongPointer32(window, index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPointer64(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPointer32(IntPtr window, int index, IntPtr value);

    private static IntPtr SetWindowLongPointer(IntPtr window, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPointer64(window, index, value)
            : SetWindowLongPointer32(window, index, value);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
