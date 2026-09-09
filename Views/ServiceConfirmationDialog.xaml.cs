using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Aether.Models;
using Aether.Services.WindowsServices;

namespace Aether.Views;

/// <summary>
/// Confirmation avant une action destructive sur les services. Réutilisable pour un
/// service isolé (<see cref="ForService"/>) comme pour un profil complet
/// (<see cref="ForProfile"/>) : dans les deux cas, l'utilisateur voit ce qui va cesser
/// de fonctionner avant que quoi que ce soit ne soit modifié.
/// </summary>
public partial class ServiceConfirmationDialog : Window
{
    public string DialogTitle { get; private set; } = "";
    public string Body { get; private set; } = "";
    public string DetailsHeader { get; private set; } = "";
    public IReadOnlyList<string> Details { get; private set; } = Array.Empty<string>();
    public bool HasDetails => Details.Count > 0;

    public string LevelIcon { get; private set; } = "ℹ️";
    public string LevelLabel { get; private set; } = "";
    public Brush LevelBrush { get; private set; } = Brushes.Gray;

    public string ConfirmLabel { get; private set; } = "Confirmer";
    public Brush ConfirmBrush { get; private set; } = Brushes.Gray;

    /// <summary>Visible uniquement pour un service sans risque.</summary>
    public bool ShowSkipOption { get; private set; }

    /// <summary>Coché par l'utilisateur : ne plus confirmer les services sans risque.</summary>
    public bool DontAskAgain { get; set; }

    private ServiceConfirmationDialog()
    {
        InitializeComponent();
        DataContext = this;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    /// <summary>Confirmation de la désactivation d'un service.</summary>
    public static ServiceConfirmationDialog ForService(WindowsServiceInfo service, Window? owner)
    {
        var dialog = new ServiceConfirmationDialog
        {
            Owner = owner,
            DialogTitle = $"Désactiver {service.DisplayName} ?",
            Body = service.ConsequenceText,
            DetailsHeader = "CONDITION D'USAGE",
            Details = new[] { service.Condition, $"Service technique : {service.ResolvedName}" },
            ShowSkipOption = service.ImpactLevel == ImpactLevel.Safe,
            ConfirmLabel = "Désactiver quand même"
        };

        dialog.ApplyLevel(service.ImpactLevel);
        return dialog;
    }

    /// <summary>Récapitulatif avant l'application d'un profil complet.</summary>
    public static ServiceConfirmationDialog ForProfile(string profileName, string summary,
                                                       IReadOnlyList<string> consequences,
                                                       ImpactLevel worstLevel, Window? owner)
    {
        var dialog = new ServiceConfirmationDialog
        {
            Owner = owner,
            DialogTitle = $"Appliquer le profil « {profileName} » ?",
            Body = summary,
            DetailsHeader = "CE QUI VA CESSER DE FONCTIONNER",
            Details = consequences,
            ShowSkipOption = false,
            ConfirmLabel = "Appliquer le profil"
        };

        dialog.ApplyLevel(worstLevel);
        return dialog;
    }

    private void ApplyLevel(ImpactLevel level)
    {
        LevelLabel = WindowsServiceCatalog.ImpactLabel(level);

        (LevelIcon, var key) = level switch
        {
            ImpactLevel.Safe => ("ℹ️", "AccentOptimalBrush"),
            ImpactLevel.Moderate => ("⚠️", "AccentWarnBrush"),
            _ => ("⛔", "AccentDangerBrush")
        };

        LevelBrush = (Brush)Application.Current.Resources[key];

        // Bouton de confirmation destructif dès que l'impact dépasse « sans risque ».
        ConfirmBrush = level == ImpactLevel.Safe
            ? (Brush)Application.Current.Resources["AccentOptimalBrush"]
            : LevelBrush;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
