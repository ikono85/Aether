using System.Diagnostics;

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
            new MemoryCompressorAction(),
            new VisualFxOffAction(),
            new UsbPowerKeepAction(),
            new GameBarOffAction(),
            new OverlayCleanerAction(),
            new ServiceTrimmerAction(),
            new TelemetryBlockAction(),
        }.ToDictionary(a => a.Id);

    public bool IsElevated => OptimizationAction.IsElevated;

    /// <summary>Vrai si ce module a déjà été appliqué et peut être annulé.</summary>
    public bool IsApplied(string moduleId) => _store.HasBackup(moduleId);

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
                try { result = action.Apply(_store, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { result = ActionResult.Failed(ex.Message); }

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
                catch (Exception ex) { result = ActionResult.Failed(ex.Message); }

                progress.Report(new ModuleProgress(ids[i], result, i + 1, ids.Count));
            }
        }, ct);
    }

    /// <summary>Relance AETHER avec les droits administrateur (invite UAC de Windows).</summary>
    public static bool RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch { return false; }   // UAC refusé par l'utilisateur
    }
}
