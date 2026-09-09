using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

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

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>Écrit une valeur de registre en sauvegardant d'abord la valeur d'origine.</summary>
    protected void SetRegistry(RestoreStore store, RegistryKey hive, string sub, string name,
                               object value, RegistryValueKind kind)
    {
        string key = $"{Id}/reg/{HiveName(hive)}|{sub}|{name}";

        if (!store.Contains(key))
        {
            using var read = hive.OpenSubKey(sub);
            var existing = read?.GetValue(name);
            store.Write(key, new RegBackup(
                Existed: existing != null,
                Kind: existing != null ? read!.GetValueKind(name).ToString() : kind.ToString(),
                Value: Encode(existing)));
        }

        using var write = hive.CreateSubKey(sub, true)
            ?? throw new InvalidOperationException($"Clé inaccessible : {sub}");
        write.SetValue(name, value, kind);
    }

    /// <summary>Restaure toutes les valeurs de registre sauvegardées par ce module.</summary>
    protected int RevertRegistry(RestoreStore store)
    {
        int n = 0;
        foreach (var key in store.KeysFor(Id).Where(k => k.Contains("/reg/")))
        {
            var backup = store.Read<RegBackup>(key);
            if (backup == null) continue;

            var parts = key[(key.IndexOf("/reg/", StringComparison.Ordinal) + 5)..].Split('|');
            if (parts.Length != 3) continue;

            var hive = ParseHive(parts[0]);
            if (hive == null) continue;

            try
            {
                using var k = hive.OpenSubKey(parts[1], true);
                if (k == null) continue;

                if (backup.Existed && backup.Value != null)
                {
                    var kind = Enum.TryParse<RegistryValueKind>(backup.Kind, out var kk) ? kk : RegistryValueKind.String;
                    object v = kind switch
                    {
                        RegistryValueKind.DWord => int.Parse(backup.Value),
                        RegistryValueKind.QWord => long.Parse(backup.Value),
                        RegistryValueKind.Binary => Convert.FromBase64String(backup.Value),
                        _ => backup.Value
                    };
                    k.SetValue(parts[2], v, kind);
                }
                else
                {
                    k.DeleteValue(parts[2], false);
                }
                n++;
            }
            catch { /* clé disparue : rien à restaurer */ }
        }
        return n;
    }

    /// <summary>Sérialise une valeur de registre en texte (base64 pour le binaire).</summary>
    private static string? Encode(object? value) => value switch
    {
        null => null,
        byte[] b => Convert.ToBase64String(b),
        _ => value.ToString()
    };

    private static string HiveName(RegistryKey hive) =>
        hive == Registry.CurrentUser ? "HKCU" :
        hive == Registry.LocalMachine ? "HKLM" :
        hive == Registry.ClassesRoot ? "HKCR" : "HKU";

    private static RegistryKey? ParseHive(string name) => name switch
    {
        "HKCU" => Registry.CurrentUser,
        "HKLM" => Registry.LocalMachine,
        "HKCR" => Registry.ClassesRoot,
        _ => null
    };

    /// <summary>Lance un utilitaire console sans fenêtre et renvoie (code de sortie, sortie).</summary>
    protected static (int Code, string Output) Run(string exe, string args, int timeoutMs = 30_000)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                }
            };
            p.Start();
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "délai dépassé"); }
            return (p.ExitCode, output);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    public record RegBackup(bool Existed, string Kind, string? Value);
}
