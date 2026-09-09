using System.ServiceProcess;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aether.Models;

/// <summary>Famille fonctionnelle d'un service, utilisée pour regrouper l'affichage.</summary>
public enum WindowsServiceCategory
{
    Xbox,
    Printing,
    HyperV,
    Sensors,
    Bluetooth,
    Geolocation,
    Phone,
    Diagnostics,
    Telemetry,
    WindowsSearch,
    NetworkSharing,
    RemoteDesktop,
    Miscellaneous
}

/// <summary>Risque encouru en désactivant le service.</summary>
public enum ImpactLevel
{
    /// <summary>Sans risque si la condition d'usage est remplie.</summary>
    Safe,
    /// <summary>Casse une fonctionnalité identifiable : à désactiver en connaissance de cause.</summary>
    Moderate,
    /// <summary>Déconseillé : impact système large ou gain quasi nul.</summary>
    NotRecommended
}

/// <summary>
/// Un service Windows présenté dans le module « Services Windows ».
/// L'état (<see cref="CurrentStatus"/>, <see cref="StartType"/>) est lu sur la machine ;
/// le reste décrit le service et les conséquences de sa désactivation.
/// </summary>
public partial class WindowsServiceInfo : ObservableObject
{
    /// <summary>Nom technique, ex. « Spooler ». Pour un service utilisateur, le préfixe sans suffixe.</summary>
    public string ServiceName { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public WindowsServiceCategory Category { get; init; }

    public ImpactLevel ImpactLevel { get; init; }

    /// <summary>Condition d'usage qui rend la désactivation légitime.</summary>
    public string Condition { get; init; } = "";

    /// <summary>Ce qui cesse concrètement de fonctionner, affiché dans la confirmation.</summary>
    public string ConsequenceText { get; init; } = "";

    /// <summary>Type de démarrage d'origine de Windows, cible du « Restaurer par défaut ».</summary>
    public ServiceStartMode DefaultStartType { get; init; } = ServiceStartMode.Manual;

    /// <summary>
    /// Vrai pour les services par utilisateur, dont le nom réel porte un suffixe
    /// dynamique (« BcastDVRUserService_4a1b2 »). Le nom réel est résolu à l'exécution.
    /// </summary>
    public bool IsPerUserService { get; init; }

    // ---- état lu sur la machine -------------------------------------------------

    /// <summary>Nom réel résolu sur cette machine (identique à ServiceName hors service utilisateur).</summary>
    [ObservableProperty] private string _resolvedName = "";

    [ObservableProperty] private ServiceControllerStatus _currentStatus = ServiceControllerStatus.Stopped;

    [ObservableProperty] private ServiceStartMode _startType = ServiceStartMode.Manual;

    /// <summary>Faux si le service n'existe pas sur cette édition de Windows : la ligne est masquée.</summary>
    [ObservableProperty] private bool _isInstalled;

    /// <summary>Vrai pendant l'application d'un changement (verrouille la ligne).</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Dernier message d'erreur, affiché sous la ligne le cas échéant.</summary>
    [ObservableProperty] private string _error = "";

    /// <summary>Le service est actif : le toggle est « allumé » pour Automatic comme pour Manual.</summary>
    public bool IsEnabled => StartType != ServiceStartMode.Disabled;

    /// <summary>Vrai si le type de démarrage diffère de celui d'origine (badge « modifié »).</summary>
    public bool IsModified => StartType != DefaultStartType;

    public string StatusText => CurrentStatus switch
    {
        ServiceControllerStatus.Running => "en cours",
        ServiceControllerStatus.Stopped => "arrêté",
        ServiceControllerStatus.Paused => "en pause",
        ServiceControllerStatus.StartPending => "démarrage…",
        ServiceControllerStatus.StopPending => "arrêt…",
        _ => "transition…"
    };

    public string StartTypeText => StartType switch
    {
        ServiceStartMode.Automatic => "Automatique",
        ServiceStartMode.Manual => "Manuel",
        ServiceStartMode.Disabled => "Désactivé",
        ServiceStartMode.Boot => "Démarrage noyau",
        _ => "Système"
    };

    public bool HasError => Error.Length > 0;

    partial void OnStartTypeChanged(ServiceStartMode value)
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(StartTypeText));
    }

    partial void OnCurrentStatusChanged(ServiceControllerStatus value) => OnPropertyChanged(nameof(StatusText));

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
}
