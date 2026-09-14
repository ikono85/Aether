using System.Windows;
using System.Windows.Input;
using Aether.ViewModels;

namespace Aether;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        StateChanged += OnWindowStateChanged;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm?.Settings.StartMinimized == true) WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// Fenêtre réduite : rien n'est visible, donc rien n'a besoin d'être échantillonné.
    /// Suspendre les sondes rend au système le CPU que coûte la lecture des capteurs LHM.
    /// </summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (Vm is not { } vm || !vm.Settings.PauseWhenMinimized) return;

        if (WindowState == WindowState.Minimized) vm.Hw.Stop();
        else vm.Hw.Resume();
    }

    protected override void OnClosed(EventArgs e)
    {
        Vm?.Shutdown();
        base.OnClosed(e);
    }

    private void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWin(object sender, RoutedEventArgs e) => Close();
}
