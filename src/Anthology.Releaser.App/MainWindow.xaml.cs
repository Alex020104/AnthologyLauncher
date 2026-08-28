using System.Windows;
using System.Windows.Input;

namespace Anthology.Releaser.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateVisuals();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleWindow(); return; }
        if (e.LeftButton == MouseButtonState.Pressed) { DragMove(); }
    }

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeRestore_OnClick(object sender, RoutedEventArgs e) => ToggleWindow();
    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
    private void MainWindow_OnStateChanged(object? sender, EventArgs e) => UpdateVisuals();
    private void MainWindow_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.F11) { ToggleWindow(); e.Handled = true; } }
    private void ToggleWindow() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateVisuals()
    {
        if (MaximizeButton is null) { return; }
        var full = WindowState == WindowState.Maximized;
        MaximizeButton.Content = full ? "❐" : "□";
        WindowFrame.CornerRadius = full ? new CornerRadius(0) : new CornerRadius(10);
        TitleBarFrame.CornerRadius = full ? new CornerRadius(0) : new CornerRadius(10, 10, 0, 0);
    }
}
