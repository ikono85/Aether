using Aether.Services.Infrastructure;

namespace Aether.Services.Optimization;

/// <summary>Progression d'une exécution : quel module, quel résultat.</summary>
public record ModuleProgress(string ModuleId, ActionResult Result, int Done, int Total);

/// <summary>
/// Exécute les optimisations sélectionnées, l'une après l'autre, hors du thread d'interface.
/// Rien n'est simulé : chaque module renvoie ce qu'il a réellement pu faire.
/// </summary>
public class OptimizationEngine
{
    private readonly RestoreStore _store = new();

    public IReadOnlyDictionary<string, OptimizationAction> Actions { get; } =
        new OptimizationAction[]
        {
            new GamingBoostAction(),
            new CleanupAction(),
            new StartupManagerAction(),
            new NetworkAcceleratorAction(),
            new StorageOptimizerAction(),
            new VisualFxOffAction(),
            new UsbPowerKeepAction(),
            new GameBarOffAction(),
            new OverlayCleanerAction(),
            new ServiceTrimmerAction(),
            new TelemetryBlockAction(),
        }.ToDictionary(a => a.Id);

    /// <summary>Message à afficher si le journal de restauration est inutilisable, sinon null.</summary>
    public string? StoreError => _store.LoadError;

    /// <summary>Vrai si ce module a déjà été appliqué et peut être annulé.</summary>
    public bool IsApplied(string moduleId) => _store.HasBackup(moduleId);

    /// <summary>Modules actuellement appliqués (onglet Historique).</summary>
    public IReadOnlyList<string> AppliedModuleIds() => Actions.Keys.Where(IsApplied).ToList();

    public async Task<ActionResult> ApplyOneAsync(string moduleId, CancellationToken ct)
    {
        var result = ActionResult.Skipped("Module inconnu.");
        await RunAsync(new[] { moduleId }, new InlineProgress(p => result = p.Result), ct);
        return result;
    }

    public async Task<ActionResult> RevertOneAsync(string moduleId)
    {
        var result = ActionResult.Skipped("Module inconnu.");
        await RevertAsync(new[] { moduleId }, new InlineProgress(p => result = p.Result), CancellationToken.None);
        return result;
    }

    /// <summary>Progression reçue immédiatement, sur le thread de l'action (sans Dispatcher).</summary>
    private sealed class InlineProgress(Action<ModuleProgress> report) : IProgress<ModuleProgress>
    {
        public void Report(ModuleProgress value) => report(value);
    }

    public async Task RunAsync(IEnumerable<string> moduleIds, IProgress<ModuleProgress> progress,
                               CancellationToken ct)
    {
        var ids = moduleIds.Where(Actions.ContainsKey).ToList();

        await Task.Run(() =>
        {
            for (int i = 0; i < ids.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var action = Actions[ids[i]];

                ActionResult result;
                if (_store.IsReadOnly)
                {
                    result = ActionResult.Failed(_store.LoadError!);
                }
                else
                {
                    try { result = action.Apply(_store, ct); }
                    catch (OperationCanceledException) { Log.Info($"[{ids[i]}] Activation annulée."); throw; }
                    catch (Exception ex)
                    {
                        Log.Error($"[{ids[i]}] Exception pendant l'activation.", ex);
                        result = ActionResult.Failed(ex.Message);
                    }
                }

                Log.Audit($"[{ids[i]}] Activation : {result.Outcome} — {result.Message}");
                progress.Report(new ModuleProgress(ids[i], result, i + 1, ids.Count));
            }
        }, ct);
    }

    public async Task RevertAsync(IEnumerable<string> moduleIds, IProgress<ModuleProgress> progress,
                                  CancellationToken ct)
    {
        var ids = moduleIds.Where(Actions.ContainsKey).ToList();

        await Task.Run(() =>
        {
            for (int i = 0; i < ids.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var action = Actions[ids[i]];

                ActionResult result;
                try { result = action.Revert(_store); }
                catch (Exception ex)
                {
                    Log.Error($"[{ids[i]}] Exception pendant la désactivation.", ex);
                    result = ActionResult.Failed(ex.Message);
                }

                Log.Audit($"[{ids[i]}] Désactivation : {result.Outcome} — {result.Message}");
                progress.Report(new ModuleProgress(ids[i], result, i + 1, ids.Count));
            }
        }, ct);
    }
}
