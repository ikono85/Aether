using System.Windows;
using System.Windows.Input;
using Aether.Services.Dialogs;

namespace Aether.Views;

/// <summary>Écran du premier lancement : ce que fait AETHER, ce qu'il envoie, et les choix associés.</summary>
public partial class OnboardingWindow : Window
{
    public bool CloseToTray { get; set; }
    public bool AlertsEnabled { get; set; }
    public bool CreateRestorePoint { get; set; }
    public bool CheckForUpdates { get; set; }

    private OnboardingWindow(OnboardingChoices defaults)
    {
        CloseToTray = defaults.CloseToTray;
        AlertsEnabled = defaults.AlertsEnabled;
        CreateRestorePoint = defaults.CreateRestorePoint;
        CheckForUpdates = defaults.CheckForUpdates;

        InitializeComponent();
        DataContext = this;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    public static OnboardingChoices? Ask(OnboardingChoices defaults, Window? owner)
    {
        var window = new OnboardingWindow(defaults) { Owner = owner };
        return window.ShowDialog() == true
            ? new OnboardingChoices(window.CloseToTray, window.AlertsEnabled, window.CreateRestorePoint, window.CheckForUpdates)
            : null;
    }

    private void OnStart(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void OnLater(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
