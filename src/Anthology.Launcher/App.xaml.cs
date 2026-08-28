using System.Net.Http;
using System.Windows;
using Anthology.Mo2.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Anthology.Launcher;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        services.AddSingleton<LauncherBridge>();
        services.AddSingleton<Mo2IntegrationService>();
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
        base.OnExit(e);
    }
}
