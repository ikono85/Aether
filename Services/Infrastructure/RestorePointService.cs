using System.Management;
using Aether.Services.Dialogs;

namespace Aether.Services.Infrastructure;

public sealed record RestorePointResult(bool Created, bool Skipped, string Message);

/// <summary>
/// Point de restauration Windows avant une modification durable. C'est un filet de sécurité
/// indépendant d'AETHER : même si ses journaux étaient perdus, Windows pourrait revenir en arrière.
///
/// Windows n'accepte qu'un point par tranche de 24 h par défaut (il renvoie alors « succès »
/// sans rien créer) : le nombre de points est comparé avant et après pour le dire honnêtement.
/// Un seul point est demandé par session d'AETHER.
/// </summary>
public sealed class RestorePointService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _handledThisSession;

    /// <summary>
    /// Crée le point si le réglage le demande. Si Windows refuse, demande à l'utilisateur s'il veut
    /// continuer sans. Retourne faux si l'opération doit être abandonnée.
    /// </summary>
    public async Task<bool> EnsureBeforeChangeAsync(IDialogService dialogs, Action<string> status)
    {
        if (!AppSettings.Load().CreateRestorePoint) return true;

        status("Création d'un point de restauration Windows (quelques secondes)…");
        var result = await EnsureAsync("AETHER — avant modification de Windows");
        status(result.Message);

        if (result.Created || result.Skipped) return true;

        return dialogs.Confirm("AETHER — point de restauration",
            $"{result.Message}{Environment.NewLine}{Environment.NewLine}" +
            "Continuer sans point de restauration ? Les modifications restent annulables depuis AETHER " +
            "(onglet Historique).");
    }

    public async Task<RestorePointResult> EnsureAsync(string description)
    {
        await _gate.WaitAsync();
        try
        {
            if (_handledThisSession)
                return new RestorePointResult(false, true, "Point de restauration déjà traité pendant cette session.");

            var result = await Task.Run(() => Create(description));
            if (result.Created || result.Skipped) _handledThisSession = true;
            return result;
        }
        finally { _gate.Release(); }
    }

    private readonly record struct Point(uint Sequence, DateTime Created);

    private static RestorePointResult Create(string description)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\default");
            scope.Connect();

            var before = Latest(scope);

            using var cls = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
            using var input = cls.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = 12;   // MODIFY_SETTINGS
            input["EventType"] = 100;         // BEGIN_SYSTEM_CHANGE
            using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
            uint code = Convert.ToUInt32(output?["ReturnValue"] ?? 1u);

            if (code != 0)
            {
                var message = code == 1058
                    ? "La protection du système est désactivée : Windows ne peut pas créer de point de restauration."
                    : $"Windows a refusé la création du point de restauration (code {code}).";
                Log.Warn(message);
                return new RestorePointResult(false, false, message);
            }

            var after = Latest(scope);
            if (after is { } a && (before is null || a.Sequence > before.Value.Sequence))
            {
                Log.Audit($"Point de restauration Windows créé (n° {a.Sequence}).");
                return new RestorePointResult(true, false, $"Point de restauration Windows créé à {a.Created:HH:mm}.");
            }

            if (before is { } b)
                return new RestorePointResult(false, true,
                    "Windows n'a pas créé de nouveau point : il n'en accepte qu'un par 24 h. " +
                    $"Le plus récent date du {b.Created:dd/MM à HH:mm}.");

            return new RestorePointResult(false, false,
                "Windows n'a créé aucun point de restauration : la protection du système est probablement " +
                "désactivée sur le lecteur système.");
        }
        catch (Exception ex)
        {
            Log.Warn("Point de restauration impossible.", ex);
            return new RestorePointResult(false, false, $"Point de restauration impossible : {ex.Message}");
        }
    }

    private static Point? Latest(ManagementScope scope)
    {
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT SequenceNumber, CreationTime FROM SystemRestore"));

        Point? latest = null;
        foreach (ManagementBaseObject o in searcher.Get())
        {
            using (o)
            {
                uint sequence = Convert.ToUInt32(o["SequenceNumber"]);
                if (latest is { } l && l.Sequence >= sequence) continue;
                var created = o["CreationTime"] is string wmiDate
                    ? ManagementDateTimeConverter.ToDateTime(wmiDate)
                    : DateTime.MinValue;
                latest = new Point(sequence, created);
            }
        }
        return latest;
    }
}
