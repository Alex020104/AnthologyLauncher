using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Anthology.Mo2.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Anthology.Launcher;

public partial class App : System.Windows.Application
{
    private static readonly string SingleInstanceMutexName = CreateSingleInstanceMutexName();
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
        services.AddSingleton<SaveProvenanceService>();
        services.AddSingleton<LauncherBridge>();
        services.AddSingleton<Mo2IntegrationService>();
        services.AddSingleton<AnomalyConfigurationService>();
        services.AddSingleton<AnomalySaveCatalogService>();
        services.AddSingleton<BugReportDiagnosticBundleService>();
        services.AddSingleton<LauncherOperationGate>();
        services.AddSingleton<LauncherReleaseHistoryStore>();
        services.AddSingleton<LauncherUpdateService>();
        services.AddSingleton<BundledInstallerService>();
        services.AddSingleton<SetupLauncherService>();

        _serviceProvider = services.BuildServiceProvider();
        Resources["services"] = _serviceProvider;
        var window = new MainWindow(
            _serviceProvider.GetRequiredService<LauncherOperationGate>(),
            _serviceProvider.GetRequiredService<LauncherSettingsStore>());
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
        using var current = Process.GetCurrentProcess();
        var currentExecutablePath = GetCurrentExecutablePath(current);
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id)
                {
                    continue;
                }

                try
                {
                    var windowHandle = process.MainWindowHandle;
                    var candidateExecutablePath = process.MainModule?.FileName;
                    if (windowHandle == IntPtr.Zero
                        || string.IsNullOrWhiteSpace(candidateExecutablePath)
                        || !Path.GetFullPath(candidateExecutablePath).Equals(
                            currentExecutablePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ShowWindowAsync(windowHandle, 9);
                    SetForegroundWindow(windowHandle);
                    break;
                }
                catch (Exception exception) when (exception is ArgumentException
                                                    or InvalidOperationException
                                                    or System.ComponentModel.Win32Exception
                                                    or NotSupportedException
                                                    or System.Security.SecurityException
                                                    or UnauthorizedAccessException)
                {
                    // The process can exit or become inaccessible while it is inspected.
                }
            }
        }
    }

    private static string CreateSingleInstanceMutexName()
    {
        var launcherDirectory = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
        var deploymentRoot = launcherDirectory.Parent?.FullName ?? launcherDirectory.FullName;
        var normalizedRoot = Path.GetFullPath(deploymentRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot)));
        return $@"Local\AnthologyLauncherNext_{identity[..24]}";
    }

    private static string GetCurrentExecutablePath(Process current)
    {
        var executablePath = Environment.ProcessPath ?? current.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Cannot resolve the launcher executable path.");
        }

        return Path.GetFullPath(executablePath);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
