using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aether.Services.Optimization;

namespace Aether.ViewModels;

public partial class OptimizationModule : ObservableObject
{
    /// <summary>Identifiant de l'action système correspondante.</summary>
    public string Id { get; set; } = "";

    public string Icon { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Details { get; set; } = "";

    /// <summary>Vrai si l'action a besoin des droits administrateur pour aboutir.</summary>
    public bool RequiresAdmin { get; set; }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Ce que l'action a réellement fait lors du dernier passage.</summary>
    [ObservableProperty] private string _lastResult = "";
    [ObservableProperty] private ActionOutcome? _lastOutcome;

    /// <summary>Vrai si le module est appliqué et peut être annulé.</summary>
    [ObservableProperty] private bool _isApplied;

    [ObservableProperty] private bool _isRunning;

    public bool HasResult => LastResult.Length > 0;
    partial void OnLastResultChanged(string value) => OnPropertyChanged(nameof(HasResult));

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;
}

public partial class OptimizationViewModel : ObservableObject
{
    private readonly OptimizationEngine _engine = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<OptimizationModule> Modules { get; } = new()
    {
        new() { Id = "gaming_boost", Icon = "⚡", Title = "Gaming Boost", Description = "Plan d'alimentation performances + mode Jeu", Enabled = true,
            Details = "Bascule Windows sur le plan d'alimentation « Performances élevées » (le CPU ne descend plus en fréquence au repos) et active le mode Jeu, qui donne la priorité au jeu au premier plan. Le plan d'alimentation d'origine est sauvegardé et restaurable." },
        new() { Id = "cleanup", Icon = "🧹", Title = "Cleanup Engine", Description = "Supprime les fichiers temporaires",
            Details = "Supprime réellement les fichiers de %TEMP%, des rapports de plantage et du cache Internet, ainsi que C:\\Windows\\Temp en mode administrateur. Sécurité : tout fichier modifié dans les dernières 24 h est épargné, et les fichiers verrouillés sont ignorés. L'espace libéré est affiché. Non réversible." },
        new() { Id = "startup", Icon = "🚀", Title = "Startup Manager", Description = "Désactive les lancements au démarrage",
            Details = "Désactive les programmes tiers lancés au démarrage de Windows, via le même mécanisme que le Gestionnaire des tâches (StartupApproved). Les composants Windows ne sont jamais touchés. Entièrement réversible." },
        new() { Id = "network", Icon = "🌐", Title = "Network Accelerator", Description = "Vide le cache DNS, règle TCP",
            Details = "Vide le cache DNS (résolutions périmées supprimées) et remet l'auto-tuning de la fenêtre TCP sur « normal », la valeur recommandée par Microsoft. La valeur précédente est sauvegardée. Le réglage TCP demande les droits administrateur." },
        new() { Id = "storage", Icon = "💾", Title = "Storage Optimizer", Description = "TRIM SSD ou défragmentation HDD", RequiresAdmin = true,
            Details = "Lance l'entretien du disque système avec l'opération correcte pour le média : commande TRIM sur SSD, défragmentation sur disque mécanique. Windows détecte lui-même le type de disque, un SSD n'est donc jamais défragmenté. Peut durer plusieurs minutes." },
        new() { Id = "memory", Icon = "🧠", Title = "Memory Compressor", Description = "Rend la RAM inactive au système",
            Details = "Vide le jeu de travail des processus accessibles : les pages inactives repartent vers le fichier d'échange et la RAM redevient disponible. Le gain réel est affiché en Mo. Windows recharge les pages à la demande, il n'y a donc rien à annuler." },
        new() { Id = "visualfx", Icon = "🎛️", Title = "Visual FX Off", Description = "Désactive les effets visuels Windows",
            Details = "Applique le profil « meilleures performances » de Windows : animations, ombres, transparences et effets de fenêtres coupés. Le changement est visible immédiatement, sans redéconnexion. Les réglages d'origine sont sauvegardés et restaurables." },
        new() { Id = "usb_power", Icon = "🔌", Title = "USB Power Keep", Description = "Désactive l'économie d'énergie USB",
            Details = "Désactive la suspension sélective USB dans le plan d'alimentation actif, et en mode administrateur, l'option « Autoriser l'ordinateur à éteindre ce périphérique » sur les contrôleurs USB. Évite les coupures de souris, casque, manette ou disque externe. Sur portable, réduit légèrement l'autonomie. Réversible." },
        new() { Id = "gamebar", Icon = "🎮", Title = "Game Bar Off", Description = "Désactive la Xbox Game Bar",
            Details = "Coupe la Xbox Game Bar et la capture en arrière-plan (Game DVR), qui tournent en permanence et grignotent des FPS en jeu. En mode administrateur, la désactivation s'applique à tout le système. Réversible." },
        new() { Id = "overlay", Icon = "🧩", Title = "Overlay Scanner", Description = "Détecte les overlays de jeu actifs",
            Details = "Détection seule : recense les overlays en cours d'exécution (Discord, Steam, GeForce Experience, MSI Afterburner, RivaTuner, Xbox) et indique où les désactiver dans chaque application. AETHER ne modifie pas la configuration interne de ces logiciels, pour ne pas casser leurs réglages." },
        new() { Id = "services", Icon = "⚙️", Title = "Service Trimmer", Description = "Services inutiles en démarrage manuel", RequiresAdmin = true,
            Details = "Passe en démarrage manuel une courte liste de services non essentiels : Télécopie, Registre à distance, Mode démonstration, Cartes hors connexion, Partage Windows Media, Téléphonie. Ils restent disponibles à la demande, ils ne démarrent simplement plus tout seuls. Chaque type de démarrage d'origine est sauvegardé." },
        new() { Id = "telemetry", Icon = "🛰️", Title = "Telemetry Block", Description = "Désactive la télémétrie Windows", RequiresAdmin = true,
            Details = "Désactive les services de diagnostic DiagTrack et dmwappushservice, les tâches planifiées de collecte (Compatibility Appraiser, CEIP) et passe la stratégie AllowTelemetry à 0. Moins d'activité réseau et disque en fond. Entièrement réversible." },
    };

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "Prêt à optimiser";

    /// <summary>Vrai si AETHER tourne avec les droits administrateur.</summary>
    public bool IsElevated => _engine.IsElevated;

    /// <summary>Vrai si des modules sélectionnés exigent une élévation dont on ne dispose pas.</summary>
    public bool NeedsElevation => !IsElevated && Modules.Any(m => m.Enabled && m.RequiresAdmin);

    public OptimizationViewModel()
    {
        // Un module appliqué lors d'une session précédente reste annulable.
        foreach (var m in Modules)
        {
            m.IsApplied = _engine.IsApplied(m.Id);
            m.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OptimizationModule.Enabled))
                    OnPropertyChanged(nameof(NeedsElevation));
            };
        }
    }

    private IEnumerable<OptimizationModule> Selected => Modules.Where(m => m.Enabled);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Optimize()
    {
        var selected = Selected.ToList();
        if (selected.Count == 0) { Status = "Aucun module sélectionné."; return; }

        await Execute(selected, revert: false);
    }

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task Revert()
    {
        var applied = Modules.Where(m => m.IsApplied).ToList();
        if (applied.Count == 0) { Status = "Rien à annuler."; return; }

        await Execute(applied, revert: true);
    }

    private bool CanRun() => !IsRunning;
    private bool CanRevert() => !IsRunning && Modules.Any(m => m.IsApplied);

    private async Task Execute(List<OptimizationModule> targets, bool revert)
    {
        IsRunning = true;
        Progress = 0;
        OptimizeCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();

        _cts = new CancellationTokenSource();
        var byId = targets.ToDictionary(m => m.Id);

        foreach (var m in targets) { m.IsRunning = true; m.LastResult = ""; m.LastOutcome = null; }

        int applied = 0, skipped = 0, failed = 0;

        var progress = new Progress<ModuleProgress>(p =>
        {
            if (!byId.TryGetValue(p.ModuleId, out var module)) return;

            module.IsRunning = false;
            module.LastOutcome = p.Result.Outcome;
            module.LastResult = p.Result.Message;
            module.IsApplied = _engine.IsApplied(p.ModuleId);

            switch (p.Result.Outcome)
            {
                case ActionOutcome.Applied:
                case ActionOutcome.Reverted: applied++; break;
                case ActionOutcome.Skipped: skipped++; break;
                case ActionOutcome.Failed: failed++; break;
            }

            Progress = p.Done / (double)p.Total * 100;
            Status = $"{module.Title} — {p.Result.Message}";
        });

        try
        {
            var ids = targets.Select(m => m.Id);
            if (revert) await _engine.RevertAsync(ids, progress, _cts.Token);
            else await _engine.RunAsync(ids, progress, _cts.Token);

            var verb = revert ? "annulé(s)" : "appliqué(s)";
            var parts = new List<string> { $"{applied} module(s) {verb}" };
            if (skipped > 0) parts.Add($"{skipped} sans effet");
            if (failed > 0) parts.Add($"{failed} en échec");
            Status = string.Join(" · ", parts) + ".";
        }
        catch (OperationCanceledException)
        {
            Status = "Opération annulée.";
        }
        catch (Exception ex)
        {
            Status = $"Erreur : {ex.Message}";
        }
        finally
        {
            foreach (var m in targets) m.IsRunning = false;
            IsRunning = false;
            Progress = 100;
            _cts?.Dispose();
            _cts = null;
            OptimizeCommand.NotifyCanExecuteChanged();
            RevertCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Relance AETHER en administrateur pour débloquer les modules qui l'exigent.</summary>
    [RelayCommand]
    private void Elevate()
    {
        if (OptimizationEngine.RestartElevated())
            Application.Current.Shutdown();
        else
            Status = "Élévation refusée — les modules administrateur resteront ignorés.";
    }
}
