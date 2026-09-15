using Aether.Models;

namespace Aether.Services.Dialogs;

/// <summary>Réponse à la confirmation de désactivation d'un service.</summary>
public readonly record struct ServiceDialogResult(bool Confirmed, bool DontAskAgain);

/// <summary>Choix faits sur l'écran d'accueil.</summary>
public sealed record OnboardingChoices(bool CloseToTray, bool AlertsEnabled, bool CreateRestorePoint, bool CheckForUpdates);

/// <summary>
/// Confirmations demandées à l'utilisateur. Les ViewModels ne connaissent que cette interface :
/// ils n'ouvrent plus eux-mêmes de fenêtre (MessageBox, vues), ce qui les rend testables et
/// garde la dépendance ViewModel → Vue hors de la couche logique.
/// </summary>
public interface IDialogService
{
    /// <summary>Question OK / Annuler. Vrai si l'utilisateur confirme.</summary>
    bool Confirm(string title, string message);

    ServiceDialogResult ConfirmServiceDisable(WindowsServiceInfo service);

    bool ConfirmProfile(string profileName, string summary, IReadOnlyList<string> consequences, ImpactLevel worstLevel);

    /// <summary>Programmes de démarrage à désactiver ; null si l'utilisateur annule.</summary>
    IReadOnlyList<string>? ChooseStartupEntries(IReadOnlyList<string> entries);

    /// <summary>Chemin d'enregistrement choisi ; null si l'utilisateur annule.</summary>
    string? AskSavePath(string title, string defaultFileName, string filter);

    /// <summary>Écran d'accueil ; null si l'utilisateur le reporte (« Plus tard »).</summary>
    OnboardingChoices? ShowOnboarding(OnboardingChoices defaults);
}
