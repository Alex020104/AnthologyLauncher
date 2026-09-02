using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Anthology.Launcher;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const string YoutubeClientReferrer = "https://github.com/Alex020104/AnthologyLauncher";
    private static readonly string WebViewUserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "A.N.T.H.O.L.O.G.Y",
        "LauncherNext",
        "WebView2");
    private CoreWebView2? _webViewCore;
    private readonly LauncherOperationGate _operationGate;

    public MainWindow(LauncherOperationGate operationGate)
    {
        _operationGate = operationGate;
        InitializeComponent();
        UpdateWindowStateVisuals();
    }

    private void LauncherWebView_OnInitializing(object? sender, BlazorWebViewInitializingEventArgs e)
    {
        Directory.CreateDirectory(WebViewUserDataFolder);
        e.UserDataFolder = WebViewUserDataFolder;
    }

    private void LauncherWebView_OnInitialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
        _webViewCore = e.WebView.CoreWebView2;
        _webViewCore.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Basic;
        _webViewCore.AddWebResourceRequestedFilter(
            "https://www.youtube.com/embed/*",
            CoreWebView2WebResourceContext.Document);
        _webViewCore.AddWebResourceRequestedFilter(
            "https://www.youtube-nocookie.com/embed/*",
            CoreWebView2WebResourceContext.Document);
        _webViewCore.WebResourceRequested += YoutubePlayerResourceRequested;
        _webViewCore.NewWindowRequested += OpenExternalLink;
    }

    internal Task<LauncherActionResult> OpenYoutubeLoginAsync()
    {
        if (_webViewCore is null)
        {
            return Task.FromResult(new LauncherActionResult(
                false,
                "Веб-профиль лаунчера ещё не готов. Повторите через несколько секунд."));
        }

        var completion = new TaskCompletionSource<LauncherActionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            var loginWindow = new YoutubeLoginWindow(_webViewCore.Environment)
            {
                Owner = this,
                ShowInTaskbar = false,
            };
            loginWindow.Closed += (_, _) =>
            {
                completion.TrySetResult(loginWindow.LoginCompleted
                    ? new LauncherActionResult(true, "Вход в YouTube сохранён. Плеер перезагружен с этим профилем.")
                    : new LauncherActionResult(false, "Вход в YouTube закрыт без подтверждения."));
            };
            loginWindow.Show();
        });
        return completion.Task;
    }

    private static void YoutubePlayerResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
            || !IsYoutubePlayerHost(uri.Host))
        {
            return;
        }

        // YouTube requires desktop WebView clients to identify the embedding app
        // with an HTTP Referer. This prevents the missing-client-identity error;
        // YouTube can still independently demand account verification.
        e.Request.Headers.SetHeader("Referer", YoutubeClientReferrer);
    }

    private static bool IsYoutubePlayerHost(string host) =>
        host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("www.youtube-nocookie.com", StringComparison.OrdinalIgnoreCase);

    private static void OpenExternalLink(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A missing system browser must not terminate the launcher window.
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowMessageHook);
    }

    private static IntPtr WindowMessageHook(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return IntPtr.Zero;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var workArea = monitorInfo.WorkArea;
        var monitorArea = monitorInfo.MonitorArea;
        minMaxInfo.MaxPosition.X = workArea.Left - monitorArea.Left;
        minMaxInfo.MaxPosition.Y = workArea.Top - monitorArea.Top;
        minMaxInfo.MaxSize.X = workArea.Right - workArea.Left;
        minMaxInfo.MaxSize.Y = workArea.Bottom - workArea.Top;
        minMaxInfo.MaxTrackSize = minMaxInfo.MaxSize;
        Marshal.StructureToPtr(minMaxInfo, lParam, false);
        handled = true;
        return IntPtr.Zero;
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

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_operationGate.ShouldBlockWindowClose)
        {
            e.Cancel = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_webViewCore is not null)
        {
            _webViewCore.WebResourceRequested -= YoutubePlayerResourceRequested;
            _webViewCore = null;
        }

        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
