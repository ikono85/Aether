using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnWindowStateChanged;
        DpiChanged += (_, _) => ApplyMaximizedMargin();
        // Masquée dans la zone de notification = aussi invisible que réduite.
        IsVisibleChanged += (_, _) => ApplySamplingPolicy();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ------------------------------------------------------------------ Fenêtre

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CXPADDEDBORDER = 92;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Coins arrondis natifs (Windows 11) : la fenêtre n'est plus transparente, ce sont les
        // coins de Windows qui arrondissent le cadre. Sans effet sur Windows 10.
        try
        {
            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,
                DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch { /* Windows 10 : coins droits */ }

        FitToWorkArea();
    }

    /// <summary>
    /// Sur un petit écran (1366×768 à 125 %), la taille minimale dépassait la zone de travail :
    /// la fenêtre sortait de l'écran. Elle est ramenée à ce qui est réellement affichable.
    /// </summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        if (MinWidth > area.Width) MinWidth = area.Width;
        if (MinHeight > area.Height) MinHeight = area.Height;
        if (Width > area.Width) Width = area.Width;
        if (Height > area.Height) Height = area.Height;
    }

    /// <summary>
    /// Une fenêtre sans bordure maximisée déborde de l'écran de l'épaisseur du cadre de
    /// redimensionnement : sans cette marge, le contenu est rogné sur les bords.
    /// </summary>
    private void ApplyMaximizedMargin()
    {
        if (WindowState != WindowState.Maximized) { Root.Margin = new Thickness(0); return; }

        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            uint ppi = (uint)dpi.PixelsPerInchX;
            int pixels = GetSystemMetricsForDpi(SM_CXSIZEFRAME, ppi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, ppi);
            Root.Margin = new Thickness(pixels / dpi.DpiScaleX);
        }
        catch { Root.Margin = new Thickness(7); }
    }

    // ------------------------------------------------------------------ Cycle de vie

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        _tray = new TrayService(vm.Settings.AlertsEnabled);
        _tray.OpenRequested += ShowFromTray;
        _tray.QuitRequested += Quit;
        _alerts = new AlertMonitor(vm.Hw, vm.NetSvc, vm.Settings, _tray);

        // Premier lancement : expliquer ce que fait AETHER avant qu'il ne tourne en arrière-plan.
        vm.Settings.RunOnboardingIfNeeded();

        if (App.PreviousSessionIssue is { } issue)
        {
            App.PreviousSessionIssue = null;
            vm.Settings.OfferDiagnosticAfterCrash(issue);
        }

        _ = CheckForUpdatesAsync(vm);

        if (vm.Settings.StartMinimized && vm.Settings.OnboardingCompleted)
        {
            // Avec la zone de notification, « démarrer réduit » veut dire invisible :
            // un bouton de barre des tâches inutile n'apporterait rien.
            if (vm.Settings.CloseToTray) { _trayHintShown = true; Hide(); }
            else WindowState = WindowState.Minimized;
        }
    }

    /// <summary>Vérification différée (pas pendant le démarrage), notifiée par l'icône si une version est disponible.</summary>
    private async Task CheckForUpdatesAsync(MainViewModel vm)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            var update = await vm.Settings.CheckForUpdatesAutoAsync();
            if (update != null)
                _tray?.Notify($"AETHER {update.Version.ToString(3)} disponible",
                    "Paramètres → Mises à jour pour consulter la nouvelle version.", warning: false);
        }
        catch (Exception ex) { Aether.Services.Infrastructure.Log.Warn("Vérification des mises à jour interrompue.", ex); }
    }

    /// <summary>Rappel de la fenêtre par une seconde instance lancée entre-temps.</summary>
    public void BringToFront() => ShowFromTray();

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

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        ApplyMaximizedMargin();
        ApplySamplingPolicy();
    }

    /// <summary>
    /// Fenêtre réduite ou masquée dans la zone de notification : rien n'est visible, donc rien
    /// n'a besoin d'être échantillonné. Exception : si l'alerte de température est active, les
    /// sondes doivent continuer, sinon elle ne pourrait jamais se déclencher.
    /// </summary>
    private void ApplySamplingPolicy()
    {
        if (Vm is not { } vm) return;

        bool hidden = !IsVisible || WindowState == WindowState.Minimized;
        bool pause = hidden && vm.Settings.PauseWhenMinimized &&
                     !(vm.Settings.AlertsEnabled && vm.Settings.AlertTemperature);

        if (pause) vm.Hw.Stop();
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
