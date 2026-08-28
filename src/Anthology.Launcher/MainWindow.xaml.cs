using System.Windows;
using System.Windows.Input;

namespace Anthology.Launcher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
