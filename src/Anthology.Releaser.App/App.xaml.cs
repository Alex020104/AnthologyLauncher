using System.Windows;
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
