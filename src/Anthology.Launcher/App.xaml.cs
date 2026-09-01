using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using Anthology.Mo2.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Anthology.Launcher;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\AnthologyLauncherNext";
    private ServiceProvider? _serviceProvider;
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            ActivateExistingWindow();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddSingleton(new HttpClient
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        });
        services.AddSingleton<LauncherSettingsStore>();
        services.AddSingleton<CommunityClient>();
        services.AddSingleton<RelayChatClient>();
        services.AddSingleton<LauncherBridge>();
        services.AddSingleton<Mo2IntegrationService>();
        services.AddSingleton<AnomalyConfigurationService>();
        services.AddSingleton<AnomalySaveCatalogService>();
        services.AddSingleton<LauncherUpdateService>();
        services.AddSingleton<BundledInstallerService>();
        services.AddSingleton<SetupLauncherService>();

        _serviceProvider = services.BuildServiceProvider();
        Resources["services"] = _serviceProvider;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        base.OnExit(e);
    }

    private static void ActivateExistingWindow()
    {
        var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id || process.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                ShowWindowAsync(process.MainWindowHandle, 9);
                SetForegroundWindow(process.MainWindowHandle);
                break;
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
