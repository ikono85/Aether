using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aether.Services;
using Aether.Services.Dialogs;
using Aether.Services.History;
using Aether.Services.Infrastructure;
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

    /// <summary>
    /// Vrai pour une action ponctuelle qui ne sauvegarde rien (nettoyage, TRIM, détection) :
    /// elle s'exécute puis se relance, mais ne se désactive pas.
    /// </summary>
    public bool IsOneShot { get; set; }

    [ObservableProperty] private bool _isExpanded;

    /// <summary>Ce que l'action a réellement fait lors du dernier passage.</summary>
    [ObservableProperty] private string _lastResult = "";
    [ObservableProperty] private ActionOutcome? _lastOutcome;

    /// <summary>Vrai si le module est appliqué et peut être annulé.</summary>
    [ObservableProperty] private bool _isApplied;

    [ObservableProperty] private bool _isRunning;

    public bool HasResult => LastResult.Length > 0;

    /// <summary>Vrai si l'optimisation est en place ou vient d'être exécutée avec succès.</summary>
    public bool IsActive => IsApplied || LastOutcome == ActionOutcome.Applied;

    /// <summary>État affiché sur la carte.</summary>
    public string StateLabel =>
        IsRunning ? "EN COURS…" :
        IsActive ? "ACTIVÉ" :
        LastOutcome switch
        {
            ActionOutcome.Reverted => "DÉSACTIVÉ",
            ActionOutcome.Skipped => "SANS EFFET",
            ActionOutcome.Failed => "ÉCHEC",
            _ => "INACTIF"
        };

    /// <summary>Libellé du bouton de la carte : ce qu'un clic va faire.</summary>
    public string ActionLabel =>
        IsRunning ? "⏳  En cours…" :
        IsApplied ? "⏻  Désactiver" :
        LastOutcome == ActionOutcome.Applied ? "↻  Relancer" :
        "⚡  Activer";

    partial void OnLastResultChanged(string value) => OnPropertyChanged(nameof(HasResult));
    partial void OnLastOutcomeChanged(ActionOutcome? value) => NotifyState();
    partial void OnIsAppliedChanged(bool value) => NotifyState();
    partial void OnIsRunningChanged(bool value) => NotifyState();

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ActionLabel));
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;
}

public partial class OptimizationViewModel : ObservableObject
{
    private readonly OptimizationEngine _engine;
    private readonly IDialogService _dialogs;
    private readonly RestorePointService _restorePoints;
    private readonly ChangeHistoryService _history;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Modules retirés ou déplacés : ils ne restent affichés que s'ils avaient été appliqués par
    /// une version précédente, le temps de les annuler.
    /// </summary>
    private static readonly Dictionary<string, string> RetiredModules = new()
    {
        ["network"] = "Module retiré — désactivez-le pour rétablir votre réglage TCP",
        ["services"] = "Déplacé dans l'onglet Services (profil « Services rarement utiles ») — désactivez-le ici pour rétablir l'état d'origine",
        ["telemetry"] = "Déplacé dans l'onglet Services (profil « Télémétrie Windows ») — désactivez-le ici pour rétablir l'état d'origine",
    };

    public ObservableCollection<OptimizationModule> Modules { get; } = new()
    {
        new() { Id = "gaming_boost", Icon = "⚡", Title = "Gaming Boost", Description = "Plan d'alimentation performances + mode Jeu",
            Details = "Bascule Windows sur le plan d'alimentation « Performances élevées » (le CPU ne descend plus en fréquence au repos) et active le mode Jeu, qui donne la priorité au jeu au premier plan. Le plan d'alimentation d'origine est sauvegardé et restaurable." },
        new() { Id = "cleanup", Icon = "🧹", Title = "Cleanup Engine", Description = "Supprime les fichiers temporaires", IsOneShot = true,
            Details = "Supprime réellement les fichiers de %TEMP%, des rapports de plantage et du cache Internet, ainsi que C:\\Windows\\Temp en mode administrateur. Sécurité : tout fichier modifié dans les dernières 24 h est épargné, et les fichiers verrouillés sont ignorés. L'espace libéré est affiché. Non réversible." },
        new() { Id = "startup", Icon = "🚀", Title = "Startup Manager", Description = "Désactive les lancements au démarrage choisis",
            Details = "Vous choisissez, programme par programme, ce qui ne doit plus démarrer avec Windows. Même mécanisme que le Gestionnaire des tâches (StartupApproved) ; les composants Windows ne sont jamais proposés. Entièrement réversible." },
        new() { Id = "network", Icon = "🌐", Title = "Network Accelerator", Description = "Vide le cache DNS, règle TCP",
            Details = "Module retiré : le réglage TCP qu'il appliquait (« normal ») est déjà la valeur par défaut de Windows, et le vidage du cache DNS se trouve dans l'onglet Network. « Désactiver » rétablit la valeur TCP d'origine." },
        new() { Id = "storage", Icon = "💾", Title = "Storage Optimizer", Description = "TRIM SSD ou défragmentation HDD", RequiresAdmin = true, IsOneShot = true,
            Details = "Lance l'entretien du disque système avec l'opération correcte pour le média : commande TRIM sur SSD, défragmentation sur disque mécanique. Windows détecte lui-même le type de disque, un SSD n'est donc jamais défragmenté. Peut durer plusieurs minutes." },
        new() { Id = "visualfx", Icon = "🎛️", Title = "Visual FX Off", Description = "Désactive les effets visuels Windows",
            Details = "Applique le profil « meilleures performances » de Windows : animations, ombres, transparences et effets de fenêtres coupés. Le changement est visible immédiatement, sans redéconnexion. Les réglages d'origine sont sauvegardés et restaurables." },
        new() { Id = "usb_power", Icon = "🔌", Title = "USB Power Keep", Description = "Désactive l'économie d'énergie USB",
            Details = "Désactive la suspension sélective USB dans le plan d'alimentation actif, et en mode administrateur, l'option « Autoriser l'ordinateur à éteindre ce périphérique » sur les contrôleurs USB. Évite les coupures de souris, casque, manette ou disque externe. Sur portable, réduit légèrement l'autonomie. Réversible." },
        new() { Id = "gamebar", Icon = "🎮", Title = "Game Bar Off", Description = "Désactive la Xbox Game Bar",
            Details = "Coupe la Xbox Game Bar et la capture en arrière-plan (Game DVR), qui tournent en permanence et grignotent des FPS en jeu. En mode administrateur, la désactivation s'applique à tout le système. Réversible." },
        new() { Id = "overlay", Icon = "🧩", Title = "Overlay Scanner", Description = "Détecte les overlays de jeu actifs", IsOneShot = true,
            Details = "Détection seule : recense les overlays en cours d'exécution (Discord, Steam, GeForce Experience, MSI Afterburner, RivaTuner, Xbox) et indique où les désactiver dans chaque application. AETHER ne modifie pas la configuration interne de ces logiciels, pour ne pas casser leurs réglages." },
        new() { Id = "services", Icon = "⚙️", Title = "Service Trimmer", Description = "Services inutiles en démarrage manuel", RequiresAdmin = true,
            Details = "Ces services sont désormais gérés dans l'onglet Services, qui affiche leur état réel et les conséquences de chaque changement. « Désactiver » rétablit ici le type de démarrage d'origine enregistré par l'ancienne version." },
        new() { Id = "telemetry", Icon = "🛰️", Title = "Telemetry Block", Description = "Désactive la télémétrie Windows", RequiresAdmin = true,
            Details = "La télémétrie est désormais gérée dans l'onglet Services (profil « Télémétrie Windows »). « Désactiver » rétablit ici les services, tâches planifiées et la stratégie modifiés par l'ancienne version." },
    };

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "Cliquez sur « Activer » sur une carte pour appliquer l'optimisation.";

    public OptimizationViewModel(OptimizationEngine engine, IDialogService dialogs,
                                 RestorePointService restorePoints, ChangeHistoryService history)
    {
        _engine = engine;
        _dialogs = dialogs;
        _restorePoints = restorePoints;
        _history = history;

        // Un module appliqué lors d'une session précédente reste annulable.
        foreach (var m in Modules)
            m.IsApplied = _engine.IsApplied(m.Id);

        foreach (var (id, description) in RetiredModules)
        {
            if (Modules.FirstOrDefault(m => m.Id == id) is not { } module) continue;
            if (!module.IsApplied) Modules.Remove(module);
            else module.Description = description;
        }

        // Une restauration depuis l'onglet Historique doit se voir sur les cartes.
        _history.Changed += OnHistoryChanged;

        if (_engine.StoreError is { } error) Status = error;
    }

    private void OnHistoryChanged()
    {
        if (IsRunning) return;
        foreach (var m in Modules) m.IsApplied = _engine.IsApplied(m.Id);
        RevertCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Bouton d'une carte : désactive le module s'il est appliqué, sinon l'applique immédiatement.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Toggle(OptimizationModule? module)
    {
        if (module is null) return;
        await Execute(new List<OptimizationModule> { module }, revert: module.IsApplied);
    }

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task Revert()
    {
        var applied = Modules.Where(m => m.IsApplied).ToList();
        if (applied.Count == 0) { Status = "Rien à désactiver."; return; }

        await Execute(applied, revert: true);
    }

    private StartupManagerAction? StartupAction =>
        _engine.Actions.TryGetValue("startup", out var a) ? a as StartupManagerAction : null;

    /// <summary>
    /// Avant d'appliquer : choix des programmes pour Startup Manager, puis confirmation des autres
    /// modules durables (réglage « Confirmer les modifications durables »). Faux = annulation.
    /// </summary>
    private bool PrepareAndConfirm(List<OptimizationModule> targets)
    {
        if (targets.Any(m => m.Id == "startup") && StartupAction is { } startup)
        {
            var entries = StartupManagerAction.PreviewEntries();
            if (entries.Count > 0)
            {
                // Le choix explicite, programme par programme, tient lieu de confirmation.
                var chosen = _dialogs.ChooseStartupEntries(entries);
                if (chosen is null) return false;
                if (chosen.Count == 0) { Status = "Aucun programme sélectionné : rien n'a été modifié."; return false; }
                startup.SelectedEntries = chosen;
            }
        }

        if (!AppSettings.Load().ConfirmRiskyActions) return true;

        var persistent = targets.Where(m => !m.IsOneShot && m.Id != "startup").ToList();
        if (persistent.Count == 0) return true;

        var nl = Environment.NewLine;
        var text = new StringBuilder()
            .Append("Ces modules modifient durablement la configuration Windows :").Append(nl).Append(nl);
        foreach (var m in persistent) text.Append("• ").Append(m.Title).Append(nl);
        text.Append(nl).Append("L'état précédent est enregistré et reste annulable avec « Désactiver » ou depuis l'onglet Historique. Continuer ?");

        return _dialogs.Confirm("AETHER — confirmation", text.ToString());
    }

    // Une seule exécution à la fois : les actions partagent le même magasin de restauration.
    private bool CanRun() => !IsRunning;
    private bool CanRevert() => !IsRunning && Modules.Any(m => m.IsApplied);

    private async Task Execute(List<OptimizationModule> targets, bool revert)
    {
        if (!revert && !PrepareAndConfirm(targets))
        {
            if (StartupAction is { } s) s.SelectedEntries = null;
            return;
        }

        IsRunning = true;
        Progress = 0;
        ToggleCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();

        var byId = targets.ToDictionary(m => m.Id);
        int applied = 0, skipped = 0, failed = 0;

        try
        {
            // Filet de sécurité Windows avant la première modification durable de la session.
            if (!revert && targets.Any(m => !m.IsOneShot) &&
                !await _restorePoints.EnsureBeforeChangeAsync(_dialogs, message => Status = message))
            {
                Status = "Opération annulée : rien n'a été modifié.";
                return;
            }

            _cts = new CancellationTokenSource();
            foreach (var m in targets) { m.IsRunning = true; m.LastResult = ""; m.LastOutcome = null; }

            Status = targets.Count == 1
                ? $"{targets[0].Title} — {(revert ? "désactivation" : "activation")} en cours…"
                : $"Désactivation de {targets.Count} module(s)…";

            // Progression traitée de façon synchrone sur le thread d'interface : le compte rendu
            // final ne peut plus être calculé avant l'arrivée du dernier résultat.
            var progress = new DispatcherProgress(p =>
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

            var ids = targets.Select(m => m.Id);
            if (revert) await _engine.RevertAsync(ids, progress, _cts.Token);
            else await _engine.RunAsync(ids, progress, _cts.Token);

            // Un seul module : le compte rendu de l'action reste affiché tel quel.
            if (targets.Count > 1)
            {
                var verb = revert ? "désactivé(s)" : "activé(s)";
                var parts = new List<string> { $"{applied} module(s) {verb}" };
                if (skipped > 0) parts.Add($"{skipped} sans effet");
                if (failed > 0) parts.Add($"{failed} en échec");
                Status = string.Join(" · ", parts) + ".";
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Opération annulée.";
        }
        catch (Exception ex)
        {
            Log.Error("Exécution des optimisations interrompue.", ex);
            Status = $"Erreur : {ex.Message}";
        }
        finally
        {
            foreach (var m in targets) m.IsRunning = false;
            if (StartupAction is { } startup) startup.SelectedEntries = null;
            IsRunning = false;
            Progress = 100;
            _cts?.Dispose();
            _cts = null;
            ToggleCommand.NotifyCanExecuteChanged();
            RevertCommand.NotifyCanExecuteChanged();
            _history.NotifyChanged();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private sealed class DispatcherProgress(Action<ModuleProgress> handler) : IProgress<ModuleProgress>
    {
        private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        public void Report(ModuleProgress value) => _dispatcher.Invoke(() => handler(value));
    }
}
