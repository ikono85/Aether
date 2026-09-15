using System.Text.Json;
using Microsoft.Win32;
using Aether.Services.Infrastructure;

namespace Aether.Services.Optimization;

public enum ActionOutcome { Applied, Reverted, Skipped, Failed }

/// <summary>Résultat honnête d'une action : ce qui a réellement été fait, ou pourquoi non.</summary>
public record ActionResult(ActionOutcome Outcome, string Message)
{
    public static ActionResult Applied(string m) => new(ActionOutcome.Applied, m);
    public static ActionResult Reverted(string m) => new(ActionOutcome.Reverted, m);
    public static ActionResult Skipped(string m) => new(ActionOutcome.Skipped, m);
    public static ActionResult Failed(string m) => new(ActionOutcome.Failed, m);
}

/// <summary>Bilan d'une restauration de registre.</summary>
public readonly record struct RevertOutcome(int Restored, int Failed);

/// <summary>
/// Une optimisation système réelle. Chaque action sauvegarde l'état précédent dans le
/// <see cref="RestoreStore"/> avant de modifier quoi que ce soit, et sait revenir en arrière.
/// </summary>
public abstract class OptimizationAction
{
    /// <summary>Identifiant stable, utilisé comme préfixe des clés de restauration.</summary>
    public abstract string Id { get; }

    /// <summary>Vrai si l'action touche HKLM, les services ou les périphériques.</summary>
    public virtual bool RequiresAdmin => false;

    public abstract ActionResult Apply(RestoreStore store, CancellationToken ct);

    public abstract ActionResult Revert(RestoreStore store);

    // ---------------------------------------------------------------- helpers

    public static bool IsElevated => Elevation.IsElevated;

    /// <summary>
    /// Liste blanche des valeurs de registre que ce module a le droit d'écrire ET de restaurer.
    /// Le journal de restauration est un fichier : même protégé, il n'est jamais cru sur parole.
    /// Une entrée qui désigne une autre clé est rejetée au lieu d'être écrite en administrateur.
    /// </summary>
    protected virtual bool AllowsRegistryTarget(string hive, string subKey, string valueName) => false;

    /// <summary>Écrit une valeur de registre en sauvegardant d'abord la valeur d'origine.</summary>
    protected void SetRegistry(RestoreStore store, RegistryKey hive, string sub, string name,
                               object value, RegistryValueKind kind)
    {
        var hiveName = HiveName(hive);
        if (!AllowsRegistryTarget(hiveName, sub, name))
            throw new InvalidOperationException($"Cible de registre non déclarée par le module {Id} : {sub}\\{name}");

        string key = $"{Id}/reg/{hiveName}|{sub}|{name}";

        if (!store.Contains(key))
        {
            using var read = hive.OpenSubKey(sub);
            var existing = read?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            // Écrit (et persisté) AVANT la modification : si la sauvegarde échoue, rien n'est touché.
            store.Write(key, new RegBackup(
                Existed: existing != null,
                Kind: existing != null ? read!.GetValueKind(name).ToString() : kind.ToString(),
                Value: Encode(existing)));
        }

        using var write = hive.CreateSubKey(sub, true)
            ?? throw new InvalidOperationException($"Clé inaccessible : {sub}");
        write.SetValue(name, value, kind);
        Log.Audit($"[{Id}] {hiveName}\\{sub}\\{name} = {Encode(value)}");
    }

    /// <summary>
    /// Restaure les valeurs de registre sauvegardées par ce module. Chaque entrée n'est retirée du
    /// journal qu'une fois réellement restaurée : un échec laisse la sauvegarde en place.
    /// </summary>
    protected RevertOutcome RevertRegistry(RestoreStore store)
    {
        int restored = 0, failed = 0;

        foreach (var key in store.KeysFor(Id).Where(k => k.Contains("/reg/")))
        {
            var parts = key[(key.IndexOf("/reg/", StringComparison.Ordinal) + 5)..].Split('|');
            var hive = parts.Length == 3 ? ParseHive(parts[0]) : null;
            var backup = store.Read<RegBackup>(key);

            if (hive == null || backup == null || !AllowsRegistryTarget(parts[0], parts[1], parts[2]))
            {
                Log.Warn($"[{Id}] Entrée de restauration rejetée (hors liste blanche ou illisible) : {key}");
                store.Remove(key);
                continue;
            }

            try
            {
                if (backup.Existed && backup.Value != null)
                {
                    var kind = Enum.TryParse<RegistryValueKind>(backup.Kind, out var k) ? k : RegistryValueKind.Unknown;
                    object value = kind switch
                    {
                        RegistryValueKind.DWord => int.Parse(backup.Value),
                        RegistryValueKind.QWord => long.Parse(backup.Value),
                        RegistryValueKind.Binary => Convert.FromBase64String(backup.Value),
                        RegistryValueKind.String or RegistryValueKind.ExpandString => backup.Value,
                        RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(backup.Value)
                                                         ?? throw new FormatException("MultiString illisible"),
                        _ => throw new FormatException($"Type de valeur non pris en charge : {backup.Kind}")
                    };

                    using var target = hive.CreateSubKey(parts[1], true)
                        ?? throw new InvalidOperationException($"Clé inaccessible : {parts[1]}");
                    target.SetValue(parts[2], value, kind);
                }
                else
                {
                    using var target = hive.OpenSubKey(parts[1], true);
                    target?.DeleteValue(parts[2], throwOnMissingValue: false);
                }

                store.Remove(key);
                restored++;
                Log.Audit($"[{Id}] Restauré {parts[0]}\\{parts[1]}\\{parts[2]}");
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warn($"[{Id}] Restauration impossible de {parts[1]}\\{parts[2]} : sauvegarde conservée.", ex);
            }
        }

        return new RevertOutcome(restored, failed);
    }

    /// <summary>Conclusion commune : la sauvegarde n'est effacée que si tout a été restauré.</summary>
    protected ActionResult FinishRevert(RestoreStore store, RevertOutcome reg, string reverted, string nothing)
    {
        if (reg.Failed > 0)
            return ActionResult.Failed(
                $"{reg.Failed} valeur(s) n'ont pas pu être restaurées. La sauvegarde est conservée : réessayez « Désactiver ».");

        store.Clear(Id);
        return reg.Restored == 0 ? ActionResult.Skipped(nothing) : ActionResult.Reverted(reverted);
    }

    /// <summary>Sérialise une valeur de registre en texte (base64 pour le binaire, JSON pour MultiString).</summary>
    private static string? Encode(object? value) => value switch
    {
        null => null,
        byte[] b => Convert.ToBase64String(b),
        string[] s => JsonSerializer.Serialize(s),
        _ => value.ToString()
    };

    private static string HiveName(RegistryKey hive) =>
        hive == Registry.CurrentUser ? "HKCU" :
        hive == Registry.LocalMachine ? "HKLM" : "OTHER";

    private static RegistryKey? ParseHive(string name) => name switch
    {
        "HKCU" => Registry.CurrentUser,
        "HKLM" => Registry.LocalMachine,
        _ => null
    };

    /// <summary>Lance un utilitaire console sans fenêtre (délai 30 s) et renvoie (code de sortie, sortie).</summary>
    protected static (int Code, string Output) Run(string exe, params string[] args) =>
        RunWithTimeout(30_000, CancellationToken.None, exe, args);

    protected static (int Code, string Output) RunWithTimeout(int timeoutMs, CancellationToken ct,
                                                              string exe, params string[] args)
    {
        var r = ProcessRunner.Run(exe, args, timeoutMs, ct);
        return (r.Code, r.Output);
    }

    /// <summary>Première ligne non vide d'une sortie console.</summary>
    protected static string FirstLine(string s) =>
        s.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "erreur inconnue";

    public record RegBackup(bool Existed, string Kind, string? Value);
}
