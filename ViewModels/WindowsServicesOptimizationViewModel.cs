using System.Collections.ObjectModel;
using System.ServiceProcess;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aether.Models;
using Aether.Services.WindowsServices;
using Aether.Views;

namespace Aether.ViewModels;

/// <summary>Une catégorie pliable regroupant les services d'une même famille.</summary>
public partial class ServiceCategoryGroup : ObservableObject
{
    public WindowsServiceCategory Category { get; init; }
    public string Label { get; init; } = "";
    public string Icon { get; init; } = "";
    public ObservableCollection<WindowsServiceInfo> Services { get; } = new();

    [ObservableProperty] private bool _isExpanded;

    public int Count => Services.Count;
}

/// <summary>Un jeu de services à désactiver en une fois, pour un usage donné.</summary>
public record ServiceProfile(string Name, string Description, string[] ServiceNames);

/// <summary>
/// Module « Services Windows » : liste l'état réel des services, permet de les
/// désactiver un par un ou par profil, et conserve de quoi tout remettre en place.
/// </summary>
public partial class WindowsServicesOptimizationViewModel : ObservableObject
{
    private readonly WindowsServiceManager _manager;
    private readonly ServiceChangeLog _log = new();
    private readonly ServicePreferences _prefs = ServicePreferences.Load();

    public ObservableCollection<ServiceCategoryGroup> Categories { get; } = new();

    public IReadOnlyList<ServiceProfile> Profiles { get; } = new[]
    {
        new ServiceProfile("PC gaming sans Xbox",
            "Coupe l'écosystème Xbox et les captures de jeu, en gardant manettes et réseau intacts.",
            new[] { "XblAuthManager", "XblGameSave", "XboxNetApiSvc", "BcastDVRUserService", "WMPNetworkSvc" }),

        new ServiceProfile("Sans virtualisation",
            "Coupe les services d'intégration Hyper-V sur un PC qui n'héberge aucune machine virtuelle.",
            new[] { "vmickvpexchange", "vmicheartbeat", "vmicvss", "vmicshutdown", "vmicrdv",
                    "vmicguestinterface", "vmictimesync" }),

        new ServiceProfile("Poste isolé sans réseau",
            "Coupe le partage réseau, la découverte d'appareils et l'accès distant sur un poste autonome.",
            new[] { "FDResPub", "SSDPSRV", "upnphost", "lmhosts", "RemoteRegistry",
                    "TermService", "SessionEnv", "UmRdpService" }),
    };

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Lecture de l'état des services…";

    public bool IsElevated => _manager.IsElevated;
    public bool NeedsElevation => !_manager.IsElevated;
    public string ElevationMessage => WindowsServiceManager.ElevationMessage;

    /// <summary>Rappel affiché en permanence : ces désactivations ne sont pas un gain de performance.</summary>
    public string InfoBanner =>
        "Sur un PC moderne équipé d'un SSD, désactiver ces services n'apporte qu'un gain de performance " +
        "marginal : l'intérêt principal est de réduire la surface d'attaque et les tâches de fond inutiles. " +
        "Pour la vie privée, les paramètres de confidentialité de Windows sont plus efficaces que la " +
        "désactivation du service de télémétrie.";

    public bool CanRestoreAll => _log.ChangedServices().Count > 0;

    public WindowsServicesOptimizationViewModel() : this(new WindowsServiceManager()) { }

    public WindowsServicesOptimizationViewModel(WindowsServiceManager manager)
    {
        _manager = manager;
        _ = LoadAsync();
    }

    /// <summary>Lit l'état réel de la machine et ne conserve que les services réellement installés.</summary>
    private async Task LoadAsync()
    {
        IsLoading = true;

        var catalog = WindowsServiceCatalog.All;
        try
        {
            await _manager.RefreshAllAsync(catalog);
        }
        catch (Exception ex)
        {
            Status = $"Lecture des services impossible : {ex.Message}";
            IsLoading = false;
            return;
        }

        Categories.Clear();

        foreach (var category in Enum.GetValues<WindowsServiceCategory>())
        {
            var installed = catalog
                .Where(s => s.Category == category && s.IsInstalled)
                .OrderBy(s => s.DisplayName)
                .ToList();

            if (installed.Count == 0) continue;   // ex. Hyper-V absent d'une édition Famille

            var group = new ServiceCategoryGroup
            {
                Category = category,
                Label = WindowsServiceCatalog.CategoryLabel(category),
                Icon = WindowsServiceCatalog.CategoryIcon(category),
            };
            foreach (var s in installed) group.Services.Add(s);
            Categories.Add(group);
        }

        int total = Categories.Sum(c => c.Count);
        int hidden = catalog.Count - total;

        Status = hidden > 0
            ? $"{total} service(s) présent(s) sur ce PC · {hidden} absent(s) de cette édition de Windows."
            : $"{total} service(s) présent(s) sur ce PC.";

        IsLoading = false;
        OnPropertyChanged(nameof(CanRestoreAll));
    }

    [RelayCommand]
    private async Task Refresh() => await LoadAsync();

    // ------------------------------------------------------------------- toggle

    /// <summary>
    /// Bascule un service. La commande est explicite (et non un binding IsChecked
    /// bidirectionnel) pour pouvoir laisser le toggle dans son état d'origine quand
    /// l'utilisateur refuse la confirmation.
    /// </summary>
    [RelayCommand]
    private async Task ToggleService(WindowsServiceInfo? service)
    {
        if (service is null || IsBusy || service.IsBusy) return;

        if (!_manager.IsElevated)
        {
            service.Error = "Droits administrateur requis.";
            return;
        }

        bool disabling = service.IsEnabled;

        if (disabling && !await ConfirmDisable(service)) return;   // toggle inchangé

        var target = disabling
            ? ServiceStartMode.Disabled
            : _log.OriginalStartType(service.ResolvedName) ?? service.DefaultStartType;

        await ApplyStartType(service, target, stopAfter: disabling);
    }

    /// <summary>Ouvre la confirmation, sauf pour un service sans risque déjà acquitté.</summary>
    private async Task<bool> ConfirmDisable(WindowsServiceInfo service)
    {
        if (service.ImpactLevel == ImpactLevel.Safe && _prefs.SkipConfirmationForSafe) return true;

        var dialog = ServiceConfirmationDialog.ForService(service, Application.Current?.MainWindow);
        bool confirmed = dialog.ShowDialog() == true;

        if (confirmed && dialog.DontAskAgain)
        {
            _prefs.SkipConfirmationForSafe = true;
            _prefs.Save();
        }

        await Task.CompletedTask;
        return confirmed;
    }

    /// <summary>Applique un type de démarrage, journalise le changement et rafraîchit la ligne.</summary>
    private async Task ApplyStartType(WindowsServiceInfo service, ServiceStartMode target, bool stopAfter)
    {
        service.IsBusy = true;
        service.Error = "";
        var previous = service.StartType;

        try
        {
            var result = await _manager.SetStartTypeAsync(
                service.ResolvedName, target, service.IsPerUserService);
            if (!result.Success)
            {
                service.Error = result.Message;
                Status = $"{service.DisplayName} — {result.Message}";
                return;
            }

            _log.Record(service.ResolvedName, previous, target);
            service.StartType = target;

            // Un service désactivé mais toujours en cours le reste jusqu'au redémarrage :
            // on tente l'arrêt à chaud, et on le dit clairement si Windows le refuse.
            if (stopAfter && service.CurrentStatus == ServiceControllerStatus.Running)
            {
                var stop = await _manager.StopServiceAsync(service.ResolvedName);
                if (!stop.Success) service.Error = stop.Message;
            }
            else if (!stopAfter && target == ServiceStartMode.Automatic)
            {
                await _manager.StartServiceAsync(service.ResolvedName);
            }

            // Un changement différé (service par utilisateur) ne sera visible qu'à la
            // prochaine session : relire l'état ici ferait revenir l'interrupteur en arrière.
            bool deferred = result.Message.Length > 0;

            var refreshed = await _manager.GetServiceStatusAsync(service.ResolvedName);
            if (refreshed != null)
            {
                service.CurrentStatus = refreshed.Value.Status;
                if (!deferred) service.StartType = refreshed.Value.StartType;
            }

            Status = $"{service.DisplayName} — {service.StartTypeText.ToLowerInvariant()}"
                   + (result.Message.Length > 0 ? $" ({result.Message})" : "");
        }
        finally
        {
            service.IsBusy = false;
            OnPropertyChanged(nameof(CanRestoreAll));
        }
    }

    // ------------------------------------------------------------------ restauration

    [RelayCommand]
    private async Task RestoreService(WindowsServiceInfo? service)
    {
        if (service is null || !_manager.IsElevated) return;

        var target = _log.OriginalStartType(service.ResolvedName) ?? service.DefaultStartType;
        await ApplyStartType(service, target, stopAfter: false);
        _log.Forget(service.ResolvedName);
        OnPropertyChanged(nameof(CanRestoreAll));
    }

    [RelayCommand]
    private async Task RestoreAll()
    {
        if (!_manager.IsElevated) { Status = ElevationMessage; return; }

        var changed = _log.ChangedServices();
        if (changed.Count == 0) { Status = "Aucun service modifié par AETHER."; return; }

        IsBusy = true;
        try
        {
            var index = Categories.SelectMany(c => c.Services)
                                  .ToDictionary(s => s.ResolvedName, StringComparer.OrdinalIgnoreCase);
            int restored = 0;

            foreach (var name in changed)
            {
                if (!index.TryGetValue(name, out var service)) continue;

                var target = _log.OriginalStartType(name) ?? service.DefaultStartType;
                await ApplyStartType(service, target, stopAfter: false);
                _log.Forget(name);
                restored++;
            }

            Status = $"{restored} service(s) remis dans leur état d'origine.";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanRestoreAll));
        }
    }

    // ---------------------------------------------------------------------- profils

    [RelayCommand]
    private async Task ApplyProfile(ServiceProfile? profile)
    {
        if (profile is null || IsBusy) return;

        if (!_manager.IsElevated) { Status = ElevationMessage; return; }

        var index = Categories.SelectMany(c => c.Services).ToList();

        // Seuls les services présents sur cette machine et encore actifs sont concernés.
        var targets = profile.ServiceNames
            .Select(n => index.FirstOrDefault(s => s.ServiceName.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .OfType<WindowsServiceInfo>()
            .Where(s => s.IsEnabled)
            .ToList();

        if (targets.Count == 0)
        {
            Status = $"Profil « {profile.Name} » : rien à changer, tout est déjà désactivé ou absent.";
            return;
        }

        var worst = targets.Max(s => s.ImpactLevel);
        var consequences = targets.Select(s => $"• {s.DisplayName} — {s.ConsequenceText}").ToList();

        var dialog = ServiceConfirmationDialog.ForProfile(
            profile.Name,
            $"{profile.Description} {targets.Count} service(s) vont être désactivés sur ce PC.",
            consequences, worst, Application.Current?.MainWindow);

        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        try
        {
            foreach (var service in targets)
                await ApplyStartType(service, ServiceStartMode.Disabled, stopAfter: true);

            Status = $"Profil « {profile.Name} » appliqué à {targets.Count} service(s).";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanRestoreAll));
        }
    }

    /// <summary>Relance AETHER en administrateur pour débloquer l'écriture.</summary>
    [RelayCommand]
    private void Elevate()
    {
        if (Aether.Services.Optimization.OptimizationEngine.RestartElevated())
            Application.Current.Shutdown();
        else
            Status = "Élévation refusée — les services restent en lecture seule.";
    }
}
