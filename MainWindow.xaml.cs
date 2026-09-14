using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Aether.Services;
using Aether.ViewModels;

namespace Aether;

public partial class MainWindow : Window
{
    private TrayService? _tray;
    private AlertMonitor? _alerts;

    /// <summary>Vrai quand l'utilisateur quitte vraiment (menu de l'icône) : la fermeture n'est plus interceptée.</summary>
    private bool _quitting;

    /// <summary>La première mise en arrière-plan est expliquée une fois, pas à chaque fermeture.</summary>
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        StateChanged += OnWindowStateChanged;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        _tray = new TrayService(vm.Settings.AlertsEnabled);
        _tray.OpenRequested += ShowFromTray;
        _tray.QuitRequested += Quit;
        _alerts = new AlertMonitor(vm.Hw, vm.NetSvc, vm.Settings, _tray);

        if (vm.Settings.StartMinimized)
        {
            // Avec la zone de notification, « démarrer réduit » veut dire invisible :
            // un bouton de barre des tâches inutile n'apporterait rien.
            if (vm.Settings.CloseToTray) { _trayHintShown = true; Hide(); }
            else WindowState = WindowState.Minimized;
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Quit()
    {
        _quitting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting && Vm?.Settings.CloseToTray == true && _tray != null)
        {
            e.Cancel = true;
            Hide();

            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.Notify("AETHER continue en arrière-plan",
                    "La surveillance et les alertes restent actives. Clic droit sur l'icône → Quitter pour fermer.",
                    warning: false);
            }
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// Fenêtre réduite : rien n'est visible, donc rien n'a besoin d'être échantillonné.
    /// Exception : si l'alerte de température est active, les sondes doivent continuer,
    /// sinon elle ne pourrait jamais se déclencher.
    /// </summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (Vm is not { } vm || !vm.Settings.PauseWhenMinimized) return;
        if (vm.Settings.AlertsEnabled && vm.Settings.AlertTemperature) return;

        if (WindowState == WindowState.Minimized) vm.Hw.Stop();
        else vm.Hw.Resume();
    }

    protected override void OnClosed(EventArgs e)
    {
        _alerts?.Dispose();
        _tray?.Dispose();
        Vm?.Shutdown();
        base.OnClosed(e);
    }

    private void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWin(object sender, RoutedEventArgs e) => Close();
}
