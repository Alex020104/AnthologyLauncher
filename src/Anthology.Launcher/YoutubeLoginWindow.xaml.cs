using Microsoft.Web.WebView2.Core;
using System.Runtime.InteropServices;
using System.Windows;

namespace Anthology.Launcher;

public partial class YoutubeLoginWindow : Window
{
    private const string YoutubeLoginUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F";
    private readonly CoreWebView2Environment _environment;
    private bool _initialized;

    public YoutubeLoginWindow(CoreWebView2Environment environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        InitializeComponent();
        Loaded += YoutubeLoginWindow_OnLoaded;
    }

    private async void YoutubeLoginWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            await YoutubeWebView.EnsureCoreWebView2Async(_environment);
            YoutubeWebView.CoreWebView2.NavigationCompleted += YoutubeWebView_OnNavigationCompleted;
            YoutubeWebView.CoreWebView2.NewWindowRequested += YoutubeWebView_OnNewWindowRequested;
            YoutubeWebView.CoreWebView2.Navigate(YoutubeLoginUrl);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            LoginStatus.Text = $"Не удалось открыть страницу входа: {exception.Message}";
        }
    }

    private async void YoutubeWebView_OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            LoginStatus.Text = $"Ошибка загрузки страницы: {e.WebErrorStatus}";
            return;
        }

        try
        {
            var cookies = await YoutubeWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.youtube.com");
            var signedIn = cookies.Any(cookie => cookie.Name is "SAPISID" or "__Secure-3PAPISID" or "LOGIN_INFO" or "SID");
            CompleteButton.IsEnabled = signedIn;
            LoginStatus.Text = signedIn
                ? "Профиль YouTube обнаружен — вход сохранён"
                : "Войдите в Google, затем нажмите кнопку внизу";
            LoginStatus.Foreground = signedIn
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.Gray;
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            LoginStatus.Text = $"Страница открыта; проверка профиля недоступна: {exception.Message}";
        }
    }

    private void YoutubeWebView_OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        YoutubeWebView.CoreWebView2.Navigate(e.Uri);
    }

    private void Back_OnClick(object sender, RoutedEventArgs e)
    {
        if (YoutubeWebView.CanGoBack)
        {
            YoutubeWebView.GoBack();
        }
    }

    private void Forward_OnClick(object sender, RoutedEventArgs e)
    {
        if (YoutubeWebView.CanGoForward)
        {
            YoutubeWebView.GoForward();
        }
    }

    private void Youtube_OnClick(object sender, RoutedEventArgs e) =>
        YoutubeWebView.CoreWebView2?.Navigate("https://www.youtube.com/");

    private void Complete_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (YoutubeWebView.CoreWebView2 is not null)
        {
            YoutubeWebView.CoreWebView2.NavigationCompleted -= YoutubeWebView_OnNavigationCompleted;
            YoutubeWebView.CoreWebView2.NewWindowRequested -= YoutubeWebView_OnNewWindowRequested;
        }
        YoutubeWebView.Dispose();
        base.OnClosed(e);
    }
}
