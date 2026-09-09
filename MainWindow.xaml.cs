using System.Windows;
using System.Windows.Input;
using Aether.ViewModels;

namespace Aether;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as MainViewModel)?.Shutdown();
        base.OnClosed(e);
    }

    private void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWin(object sender, RoutedEventArgs e) => Close();
}
