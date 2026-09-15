using Microsoft.Extensions.DependencyInjection;
using Aether.Services.History;
using Aether.Services.Optimization;

namespace Aether.Services.Infrastructure;

public sealed record CliCommand(string Verb, string Argument);

/// <summary>
/// Ligne de commande, sans interface :
///   Aether.exe --restore-all        rétablit tout ce qu'AETHER a modifié (utilisé à la désinstallation)
///   Aether.exe --apply &lt;module&gt;    applique une optimisation (tests d'intégration)
///   Aether.exe --revert &lt;module&gt;   annule une optimisation
/// Codes de sortie : 0 succès, 1 échec, 2 commande invalide, 3 AETHER déjà ouvert.
/// Le détail est écrit dans le journal.
/// </summary>
public static class CommandLine
{
    public const int Success = 0, Failure = 1, Usage = 2, AlreadyRunning = 3;

    /// <summary>Null si aucun argument de ligne de commande : lancement normal de l'interface.</summary>
    public static CliCommand? TryParse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return null;

        var verb = args[0].Trim().ToLowerInvariant();
        return verb switch
        {
            "--restore-all" => new CliCommand(verb, ""),
            "--apply" or "--revert" => new CliCommand(verb, args.Count > 1 ? args[1].Trim() : ""),
            _ => new CliCommand("--invalid", args[0])
        };
    }

    public static async Task<int> RunAsync(CliCommand command, IServiceProvider services)
    {
        Log.Info($"Ligne de commande : {command.Verb} {command.Argument}".TrimEnd());

        switch (command.Verb)
        {
            case "--restore-all":
            {
                var history = services.GetRequiredService<ChangeHistoryService>();
                var (restored, failed) = await history.RestoreAllAsync(Log.Info);
                Log.Info($"--restore-all : {restored} restauré(s), {failed} échec(s).");
                return failed == 0 ? Success : Failure;
            }

            case "--apply":
            case "--revert":
            {
                var engine = services.GetRequiredService<OptimizationEngine>();
                if (!engine.Actions.ContainsKey(command.Argument))
                {
                    Log.Warn($"Module inconnu : « {command.Argument} ». Modules : {string.Join(", ", engine.Actions.Keys)}.");
                    return Usage;
                }

                var result = command.Verb == "--apply"
                    ? await engine.ApplyOneAsync(command.Argument, CancellationToken.None)
                    : await engine.RevertOneAsync(command.Argument);

                Log.Info($"{command.Verb} {command.Argument} : {result.Outcome} — {result.Message}");
                return result.Outcome == ActionOutcome.Failed ? Failure : Success;
            }

            default:
                Log.Warn($"Argument inconnu : {command.Argument}. Usage : --restore-all | --apply <module> | --revert <module>.");
                return Usage;
        }
    }
}
