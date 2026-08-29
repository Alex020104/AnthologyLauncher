using System.Windows;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Anthology.Releaser.App;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddSingleton<ReleaserStateStore>();
        services.AddSingleton<ReleaserBridge>();
        services.AddSingleton(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(45),
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        });
        services.AddSingleton<BugReportDeveloperClient>();
        _services = services.BuildServiceProvider();
        Resources["services"] = _services;
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
