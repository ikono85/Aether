using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Aether.Services.Optimization;

/// <summary>Bascule le plan d'alimentation sur « Performances élevées » et active le mode Jeu.</summary>
public class GamingBoostAction : OptimizationAction
{
    public override string Id => "gaming_boost";

    private const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string BackupKey = "gaming_boost/scheme";

    public override ActionResult Apply(RestoreStore store, CancellationToken ct)
    {
        var previous = ActiveScheme();
        if (previous == null) return ActionResult.Failed("Plan d'alimentation actif illisible.");

        if (!store.Contains(BackupKey)) store.Write(BackupKey, previous);

        // Le plan « Performances élevées » est masqué sur certains PC : on le recrée au besoin.
        var (listCode, list) = Run("powercfg", "/list");
        if (listCode != 0) return ActionResult.Failed("powercfg indisponible.");

        if (!list.Contains(HighPerf, StringComparison.OrdinalIgnoreCase))
            Run("powercfg", $"-duplicatescheme {HighPerf}");

        var (code, output) = Run("powercfg", $"/setactive {HighPerf}");
        if (code != 0) return ActionResult.Failed($"Activation refusée : {output.Trim()}");

        // Mode Jeu de Windows (planification prioritaire du jeu au premier plan).
        try
        {
            SetRegistry(store, Registry.CurrentUser,
                @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord);
        }
        catch { /* non bloquant */ }

        return ActionResult.Applied("Plan « Performances élevées » actif, mode Jeu activé.");
    }

    public override ActionResult Revert(RestoreStore store)
    {
        var previous = store.Read<string>(BackupKey);
        RevertRegistry(store);

        if (string.IsNullOrWhiteSpace(previous)) return ActionResult.Skipped("Aucun plan sauvegardé.");

        var (code, output) = Run("powercfg", $"/setactive {previous}");
        if (code != 0) return ActionResult.Failed($"Restauration refusée : {output.Trim()}");

        store.Clear(Id);
        return ActionResult.Reverted("Plan d'alimentation d'origine restauré.");
    }

    private static string? ActiveScheme()
    {
        var (code, output) = Run("powercfg", "/getactivescheme");
        if (code != 0) return null;
        var m = Regex.Match(output, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value : null;
    }
}

/// <summary>Vide les fichiers temporaires réellement présents sur le disque.</summary>
public class CleanupAction : OptimizationAction
{
    public override string Id => "cleanup";

    /// <summary>Marge de sécurité : on ne touche pas à ce qui a été écrit dans les dernières 24 h.</summary>
    private static readonly TimeSpan MinAge = TimeSpan.FromHours(24);

    public override ActionResult Apply(RestoreStore store, CancellationToken ct)
    {
        var targets = new List<string> { Path.GetTempPath() };

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0)
        {
            targets.Add(Path.Combine(local, "CrashDumps"));
            targets.Add(Path.Combine(local, "Microsoft", "Windows", "INetCache", "IE"));
        }

        if (IsElevated)
            targets.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));

        long freed = 0; int files = 0, locked = 0;
        var cutoff = DateTime.UtcNow - MinAge;

        foreach (var dir in targets.Where(Directory.Exists))
        {
            ct.ThrowIfCancellationRequested();
            var (b, f, l) = Purge(dir, cutoff, ct);
            freed += b; files += f; locked += l;
        }

        string lockedNote = locked > 0 ? $", {locked} en cours d'utilisation ignoré(s)" : "";
        return files == 0
            ? ActionResult.Skipped($"Rien à supprimer{lockedNote}.")
            : ActionResult.Applied($"{files} fichier(s) supprimé(s), {Format(freed)} libéré(s){lockedNote}.");
    }

    public override ActionResult Revert(RestoreStore store) =>
        ActionResult.Skipped("Suppression de fichiers temporaires : rien à restaurer.");

    private static (long Bytes, int Files, int Locked) Purge(string dir, DateTime cutoff, CancellationToken ct)
    {
        long bytes = 0; int files = 0, locked = 0;

        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
        catch { return (0, 0, 0); }

        foreach (var file in entries)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc > cutoff) continue;
                long size = info.Length;
                info.Attributes = FileAttributes.Normal;
                info.Delete();
                bytes += size; files++;
            }
            catch { locked++; }
        }

        // Dossiers devenus vides.
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).Reverse())
            {
                try { if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub); } catch { }
            }
        }
        catch { }

        return (bytes, files, locked);
    }

    private static string Format(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:0.0} Go",
        >= 1_048_576 => $"{bytes / 1_048_576.0:0} Mo",
        _ => $"{bytes / 1024.0:0} Ko"
    };
}

/// <summary>Désactive les programmes tiers lancés au démarrage (mécanisme du Gestionnaire des tâches).</summary>
public class StartupManagerAction : OptimizationAction
{
    public override string Id => "startup";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public override ActionResult Apply(RestoreStore store, CancellationToken ct)
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run == null) return ActionResult.Skipped("Aucune entrée de démarrage utilisateur.");

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        int disabled = 0, kept = 0;

        foreach (var name in run.GetValueNames())
        {
            ct.ThrowIfCancellationRequested();
            var command = run.GetValue(name)?.ToString() ?? "";

            // On ne touche jamais aux composants Windows.
            if (command.Contains(windows, StringComparison.OrdinalIgnoreCase)) { kept++; continue; }

            try
            {
                // 12 octets ; premier octet 2 = activé, 3 = désactivé.
                var payload = new byte[12];
                payload[0] = 3;
                SetRegistry(store, Registry.CurrentUser, ApprovedKey, name, payload, RegistryValueKind.Binary);
                disabled++;
            }
            catch { kept++; }
        }

        return disabled == 0
            ? ActionResult.Skipped($"Rien à désactiver ({kept} entrée(s) système conservée(s)).")
            : ActionResult.Applied($"{disabled} programme(s) tiers désactivé(s) au démarrage.");
    }

    public override ActionResult Revert(RestoreStore store)
    {
        int n = RevertRegistry(store);
        store.Clear(Id);
        return n == 0 ? ActionResult.Skipped("Aucune entrée à réactiver.")
                      : ActionResult.Reverted($"{n} programme(s) réactivé(s) au démarrage.");
    }
}

/// <summary>Vide le cache DNS et remet l'auto-tuning TCP sur sa valeur recommandée.</summary>
public class NetworkAcceleratorAction : OptimizationAction
{
    public override string Id => "network";
    private const string BackupKey = "network/autotuning";

    public override ActionResult Apply(RestoreStore store, CancellationToken ct)
    {
        var done = new List<string>();

        var (dnsCode, _) = Run("ipconfig", "/flushdns");
        if (dnsCode == 0) done.Add("cache DNS vidé");

        if (IsElevated)
        {
            if (!store.Contains(BackupKey))
            {
                var current = AutoTuningLevel();
                if (current != null) store.Write(BackupKey, current);
            }

            var (code, output) = Run("netsh", "int tcp set global autotuninglevel=normal");
            done.Add(code == 0 ? "auto-tuning TCP sur « normal »" : $"TCP inchangé ({output.Trim()})");
        }
        else
        {
            done.Add("réglage TCP ignoré (droits administrateur requis)");
        }

        return done.Count == 0
            ? ActionResult.Failed("Aucune opération réseau n'a abouti.")
            : ActionResult.Applied(string.Join(", ", done) + ".");
    }

    public override ActionResult Revert(RestoreStore store)
    {
        var previous = store.Read<string>(BackupKey);
        if (string.IsNullOrWhiteSpace(previous)) return ActionResult.Skipped("Aucun réglage TCP à restaurer.");

        var (code, output) = Run("netsh", $"int tcp set global autotuninglevel={previous}");
        if (code != 0) return ActionResult.Failed($"Restauration refusée : {output.Trim()}");

        store.Clear(Id);
        return ActionResult.Reverted($"Auto-tuning TCP remis sur « {previous} ».");
    }

    private static readonly string[] Levels =
        { "disabled", "highlyrestricted", "restricted", "normal", "experimental" };

    /// <summary>
    /// Lit le niveau d'auto-tuning actuel. On interroge Get-NetTCPSetting plutôt que netsh :
    /// le nom de propriété est identique dans toutes les langues, alors que la sortie de netsh
    /// est traduite et mêle plusieurs réglages ayant les mêmes valeurs (« disabled »…).
    /// </summary>
    private static string? AutoTuningLevel()
    {
        var (code, output) = Run("powershell",
            "-NoProfile -Command \"(Get-NetTCPSetting -SettingName Internet).AutoTuningLevelLocal\"", 20_000);

        var value = output.Trim().ToLowerInvariant();
        return code == 0 && Levels.Contains(value) ? value : null;
    }
}

/// <summary>Lance l'entretien adapté au disque système : TRIM sur SSD, défragmentation sur HDD.</summary>
public class StorageOptimizerAction : OptimizationAction
{
    public override string Id => "storage";
    public override bool RequiresAdmin => true;

    public override ActionResult Apply(RestoreStore store, CancellationToken ct)
    {
        if (!IsElevated) return ActionResult.Skipped("Droits administrateur requis.");

        var drive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

        // /O applique l'opération correcte selon le média : TRIM sur SSD, défragmentation sur HDD.
        var (code, output) = Run("defrag", $"{drive} /O /H", timeoutMs: 15 * 60_000);

        return code != 0
            ? ActionResult.Failed($"Entretien interrompu : {First(output)}")
            : ActionResult.Applied($"Entretien de {drive} terminé ({MediaType()}).");
    }

    public override ActionResult Revert(RestoreStore store) =>
        ActionResult.Skipped("Entretien disque : aucune modification à annuler.");

    private static string MediaType()
    {
        var (code, output) = Run("powershell",
            "-NoProfile -Command \"(Get-PhysicalDisk | Select-Object -First 1).MediaType\"", 20_000);
        var t = output.Trim();
        return code == 0 && t.Length > 0 ? t : "type de disque inconnu";
    }

    private static string First(string s) =>
        s.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "erreur inconnue";
}

/// <summary>Vide le jeu de travail des processus accessibles pour rendre de la RAM au système.</summary>
public class MemoryCompressorAction : OptimizationAction
{
    public override string Id => "memory";

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public override ActionResult Apply(RestoreStore store, CancellationToken ct)
    {
        long before = 0, after = 0;
        int trimmed = 0;

        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            ct.ThrowIfCancellationRequested();
            using (p)
            {
                try
                {
                    long ws = p.WorkingSet64;
                    if (ws <= 0) continue;
                    before += ws;

                    if (EmptyWorkingSet(p.Handle)) trimmed++;

                    p.Refresh();
                    after += p.WorkingSet64;
                }
                catch { /* processus protégé ou terminé entre-temps */ }
            }
        }

        long freed = Math.Max(0, before - after);
        return trimmed == 0
            ? ActionResult.Skipped("Aucun processus n'a pu être compacté.")
            : ActionResult.Applied($"{trimmed} processus compacté(s), {freed / 1_048_576.0:0} Mo rendus au système.");
    }

    public override ActionResult Revert(RestoreStore store) =>
        ActionResult.Skipped("Compactage mémoire : Windows recharge les pages à la demande.");
}
