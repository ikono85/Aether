using System.Collections.ObjectModel;
using System.ServiceProcess;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aether.Models;
using Aether.Services.Dialogs;
using Aether.Services.History;
using Aether.Services.Infrastructure;
using Aether.Services.WindowsServices;

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
    private const string AdminRequired = "Droits administrateur requis.";

    private readonly WindowsServiceManager _manager;
    private readonly IDialogService _dialogs;
    private readonly ServiceChangeLog _log;
    private readonly RestorePointService _restorePoints;
    private readonly ChangeHistoryService _history;
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

        // Reprend l'ancien module « Service Trimmer » de l'onglet Optimisation.
        new ServiceProfile("Services rarement utiles",
            "Télécopie, registre à distance, mode démonstration, cartes hors connexion, partage Windows Media et téléphonie.",
            new[] { "Fax", "RemoteRegistry", "RetailDemo", "MapsBroker", "WMPNetworkSvc", "PhoneSvc" }),

        // Reprend l'ancien module « Telemetry Block » : ici, chaque conséquence est affichée.
        new ServiceProfile("Télémétrie Windows",
            "Coupe les services de collecte de données de diagnostic (DiagTrack) et de routage WAP Push.",
            new[] { "DiagTrack", "dmwappushservice" }),
    };

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Lecture de l'état des services…";

    /// <summary>Rappel affiché en permanence : ces désactivations ne sont pas un gain de performance.</summary>
    public string InfoBanner =>
        "Sur un PC moderne équipé d'un SSD, désactiver ces services n'apporte qu'un gain de performance " +
        "marginal : l'intérêt principal est de réduire la surface d'attaque et les tâches de fond inutiles. " +
        "Pour la vie privée, les paramètres de confidentialité de Windows sont plus efficaces que la " +
        "désactivation du service de télémétrie.";

    public bool CanRestoreAll => _log.ChangedServices().Count > 0;

    public WindowsServicesOptimizationViewModel(WindowsServiceManager manager, IDialogService dialogs,
                                                ServiceChangeLog log, RestorePointService restorePoints,
                                                ChangeHistoryService history)
    {
        _manager = manager;
        _dialogs = dialogs;
        _log = log;
        _restorePoints = restorePoints;
        _history = history;

        // Restauration depuis l'onglet Historique : les lignes partagent les objets du catalogue,
        // seul le bouton « Tout restaurer » est à réévaluer.
        _history.Changed += () => OnPropertyChanged(nameof(CanRestoreAll));

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

        if (_log.LoadError is { } logError) Status = logError;

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
            service.Error = AdminRequired;
            return;
        }

        bool disabling = service.IsEnabled;

        if (disabling && !await ConfirmDisable(service)) return;   // toggle inchangé
        if (disabling && !await _restorePoints.EnsureBeforeChangeAsync(_dialogs, s => Status = s)) return;

        var target = disabling
            ? ServiceStartMode.Disabled
            : _log.OriginalStartType(service.ServiceName) ?? service.DefaultStartType;

        await ApplyStartType(service, target, stopAfter: disabling);
    }

    /// <summary>Ouvre la confirmation, sauf pour un service sans risque déjà acquitté.</summary>
    private Task<bool> ConfirmDisable(WindowsServiceInfo service)
    {
        if (service.ImpactLevel == ImpactLevel.Safe && _prefs.SkipConfirmationForSafe) return Task.FromResult(true);

        var answer = _dialogs.ConfirmServiceDisable(service);
        if (answer.DontAskAgain)
        {
            _prefs.SkipConfirmationForSafe = true;
            _prefs.Save();
        }

        return Task.FromResult(answer.Confirmed);
    }

    /// <summary>
    /// Applique un type de démarrage, journalise le changement et rafraîchit la ligne.
    /// Retourne vrai si le type de démarrage a réellement été modifié.
    /// </summary>
    private async Task<bool> ApplyStartType(WindowsServiceInfo service, ServiceStartMode target, bool stopAfter)
    {
        service.IsBusy = true;
        service.Error = "";
        var previous = service.StartType;

        try
        {
            // Journalisé AVANT le changement, sous le nom de catalogue : si le journal ne peut
            // pas être écrit, rien n'est modifié — l'état d'origine ne peut donc jamais se perdre.
            ServiceChangeEntry entry;
            try { entry = _log.Record(service.ServiceName, previous, target); }
            catch (Exception ex)
            {
                Log.Error($"Journal des services indisponible ({service.ServiceName}).", ex);
                service.Error = $"Journal des changements indisponible : {ex.Message}";
                Status = $"{service.DisplayName} — rien n'a été modifié.";
                return false;
            }

            var result = await _manager.SetStartTypeAsync(
                service.ResolvedName, target, service.IsPerUserService);
            if (!result.Success)
            {
                _log.Discard(entry);
                Log.Warn($"Service {service.ResolvedName} : {previous} → {target} refusé ({result.Message}).");
                service.Error = result.Message;
                Status = $"{service.DisplayName} — {result.Message}";
                return false;
            }

            Log.Audit($"Service {service.ResolvedName} : {previous} → {target}.");
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
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Changement du service {service.ResolvedName} interrompu.", ex);
            service.Error = ex.Message;
            return false;
        }
        finally
        {
            service.IsBusy = false;
            OnPropertyChanged(nameof(CanRestoreAll));
            _history.NotifyChanged();
        }
    }

    // ------------------------------------------------------------------ restauration

    [RelayCommand]
    private async Task RestoreService(WindowsServiceInfo? service)
    {
        if (service is null || !_manager.IsElevated) return;

        var target = _log.OriginalStartType(service.ServiceName) ?? service.DefaultStartType;

        // L'historique n'est oublié qu'une fois la restauration réellement appliquée.
        if (await ApplyStartType(service, target, stopAfter: false))
            _log.Forget(service.ServiceName);
        OnPropertyChanged(nameof(CanRestoreAll));
        _history.NotifyChanged();
    }

    [RelayCommand]
    private async Task RestoreAll()
    {
        if (!_manager.IsElevated) { Status = AdminRequired; return; }

        var changed = _log.ChangedServices();
        if (changed.Count == 0) { Status = "Aucun service modifié par AETHER."; return; }

        IsBusy = true;
        try
        {
            // Index par nom de catalogue : stable d'une session à l'autre, contrairement au nom résolu.
            var index = Categories.SelectMany(c => c.Services)
                                  .ToDictionary(s => s.ServiceName, StringComparer.OrdinalIgnoreCase);
            int restored = 0, failed = 0, absent = 0;

            foreach (var name in changed)
            {
                if (!index.TryGetValue(name, out var service)) { absent++; continue; }

                var target = _log.OriginalStartType(name) ?? service.DefaultStartType;
                if (await ApplyStartType(service, target, stopAfter: false))
                {
                    _log.Forget(name);
                    restored++;
                }
                else failed++;
            }

            var parts = new List<string> { $"{restored} service(s) remis dans leur état d'origine" };
            if (failed > 0) parts.Add($"{failed} en échec (historique conservé)");
            if (absent > 0) parts.Add($"{absent} absent(s) de ce PC");
            Status = string.Join(" · ", parts) + ".";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanRestoreAll));
            _history.NotifyChanged();
        }
    }

    // ---------------------------------------------------------------------- profils

    [RelayCommand]
    private async Task ApplyProfile(ServiceProfile? profile)
    {
        if (profile is null || IsBusy) return;

        if (!_manager.IsElevated) { Status = AdminRequired; return; }

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

        if (!_dialogs.ConfirmProfile(
                profile.Name,
                $"{profile.Description} {targets.Count} service(s) vont être désactivés sur ce PC.",
                consequences, worst))
            return;

        if (!await _restorePoints.EnsureBeforeChangeAsync(_dialogs, s => Status = s)) return;

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
}
