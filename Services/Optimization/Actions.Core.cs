using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Aether.Services.Infrastructure;

namespace Aether.Services.Optimization;

/// <summary>Bascule le plan d'alimentation sur « Performances élevées » et active le mode Jeu.</summary>
public class GamingBoostAction : OptimizationAction
{
    public override string Id => "gaming_boost";

    private const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string BackupKey = "gaming_boost/scheme";

    private static readonly Regex GuidPattern =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");

    protected override bool AllowsRegistryTarget(string hive, string subKey, string valueName) =>
        hive == "HKCU" && subKey == @"Software\Microsoft\GameBar" && valueName == "AutoGameModeEnabled";

    public override ActionResult Apply(RestoreStore store, CancellationToken ct)
    {
        var previous = ActiveScheme();
        if (previous == null) return ActionResult.Failed("Plan d'alimentation actif illisible.");

        bool savedNow = false;
        if (!store.Contains(BackupKey)) { store.Write(BackupKey, previous); savedNow = true; }

        ActionResult Abort(string message)
        {
            // Rien n'a changé : on ne laisse pas croire que le module est appliqué.
            if (savedNow) store.Remove(BackupKey);
            return ActionResult.Failed(message);
        }

        // Le plan « Performances élevées » est masqué sur certains PC : on le recrée au besoin.
        var (listCode, list) = Run("powercfg", "/list");
        if (listCode != 0) return Abort("powercfg indisponible.");

        if (!list.Contains(HighPerf, StringComparison.OrdinalIgnoreCase))
        {
            // Sans GUID de destination, powercfg crée une copie au GUID aléatoire et
            // l'activation de HighPerf échouerait : on impose le GUID attendu.
            var (dupCode, dupOutput) = Run("powercfg", "-duplicatescheme", HighPerf, HighPerf);
            if (dupCode != 0)
                return Abort("Plan « Performances élevées » indisponible sur ce PC (fréquent sur les " +
                             $"portables Modern Standby). {FirstLine(dupOutput)}");
        }

        var (code, output) = Run("powercfg", "/setactive", HighPerf);
        if (code != 0) return Abort($"Activation refusée : {FirstLine(output)}");
        Log.Audit($"[{Id}] Plan d'alimentation {previous} → {HighPerf}");

        // Mode Jeu de Windows (planification prioritaire du jeu au premier plan).
        try
        {
            SetRegistry(store, Registry.CurrentUser,
                @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex) { Log.Warn($"[{Id}] Mode Jeu non activé.", ex); }

        return ActionResult.Applied("Plan « Performances élevées » actif, mode Jeu activé.");
    }

    public override ActionResult Revert(RestoreStore store)
    {
        var previous = store.Read<string>(BackupKey);
        if (previous != null && !GuidPattern.IsMatch(previous))
        {
            Log.Warn($"[{Id}] Plan sauvegardé invalide rejeté : {previous}");
            store.Remove(BackupKey);
            previous = null;
        }

        var reg = RevertRegistry(store);

        if (previous is null)
            return FinishRevert(store, reg, "Mode Jeu rétabli.", "Aucun plan sauvegardé.");

        var (code, output) = Run("powercfg", "/setactive", previous);
        if (code != 0) return ActionResult.Failed($"Restauration refusée : {FirstLine(output)}");

        if (reg.Failed > 0) return FinishRevert(store, reg, "", "");
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
        var localFolders = new List<string>();

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0) localFolders.Add(local);

        // Compte standard élevé avec les identifiants d'un administrateur : %TEMP% et
        // %LOCALAPPDATA% désignent alors les dossiers de l'administrateur. On nettoie aussi
        // ceux de l'utilisateur réellement connecté.
        if (SessionUser.IsDifferentAccount)
        {
            if (SessionUser.TempPath is { } userTemp) targets.Add(userTemp);
            if (SessionUser.LocalAppDataPath is { } userLocal) localFolders.Add(userLocal);
        }

        foreach (var folder in localFolders)
        {
            targets.Add(Path.Combine(folder, "CrashDumps"));
            targets.Add(Path.Combine(folder, "Microsoft", "Windows", "INetCache", "IE"));
        }

        if (IsElevated)
            targets.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));

        long freed = 0; int files = 0, locked = 0;
        var cutoff = DateTime.UtcNow - MinAge;

        var dirs = targets
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists);

        foreach (var dir in dirs)
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

        // Un sous-dossier inaccessible est sauté au lieu d'interrompre tout le parcours, et les
        // jonctions ne sont jamais suivies : on ne supprime rien en dehors du dossier ciblé.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFiles(dir, "*", options); }
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
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", options).Reverse())
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

    protected override bool AllowsRegistryTarget(string hive, string subKey, string valueName) =>
        hive == "HKCU" && subKey == ApprovedKey;

    /// <summary>
    /// Programmes choisis par l'utilisateur pour la prochaine exécution ; null = tous les
    /// programmes tiers (ligne de commande). Remis à null après chaque exécution.
    /// </summary>
    public IReadOnlyCollection<string>? SelectedEntries { get; set; }

    /// <summary>Programmes qu'une activation désactiverait : listés dans la confirmation.</summary>
    public static IReadOnlyList<string> PreviewEntries()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run == null) return Array.Empty<string>();

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return run.GetValueNames()
                      .Where(n => !(run.GetValue(n)?.ToString() ?? "").Contains(windows, StringComparison.OrdinalIgnoreCase))
                      .OrderBy(n => n)
                      .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("Lecture des programmes au démarrage impossible.", ex);
            return Array.Empty<string>();
        }
    }

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

            // On ne touche jamais aux composants Windows, ni aux programmes que l'utilisateur a gardés.
            if (command.Contains(windows, StringComparison.OrdinalIgnoreCase)) { kept++; continue; }
            if (SelectedEntries is { } selected && !selected.Contains(name)) { kept++; continue; }

            try
            {
                // 12 octets ; premier octet 2 = activé, 3 = désactivé.
                var payload = new byte[12];
                payload[0] = 3;
                SetRegistry(store, Registry.CurrentUser, ApprovedKey, name, payload, RegistryValueKind.Binary);
                disabled++;
            }
            catch (Exception ex)
            {
                Log.Warn($"[{Id}] Entrée « {name} » non désactivée.", ex);
                kept++;
            }
        }

        return disabled == 0
            ? ActionResult.Skipped($"Rien à désactiver ({kept} entrée(s) conservée(s)).")
            : ActionResult.Applied($"{disabled} programme(s) tiers désactivé(s) au démarrage.");
    }

    public override ActionResult Revert(RestoreStore store)
    {
        var reg = RevertRegistry(store);
        return FinishRevert(store, reg, $"{reg.Restored} programme(s) réactivé(s) au démarrage.",
                            "Aucune entrée à réactiver.");
    }
}

/// <summary>
/// Ancien module « Network Accelerator », RETIRÉ : « normal » est déjà la valeur par défaut de
/// Windows et le vidage du cache DNS existe dans la boîte à outils réseau. La classe est gardée
/// uniquement pour annuler une application faite par une version précédente.
/// </summary>
public class NetworkAcceleratorAction : OptimizationAction
{
    public override string Id => "network";
    private const string BackupKey = "network/autotuning";

    public override ActionResult Apply(RestoreStore store, CancellationToken ct) =>
        ActionResult.Skipped("Module retiré : utilisez « Vider le cache DNS » dans l'onglet Network.");

    public override ActionResult Revert(RestoreStore store)
    {
        var previous = store.Read<string>(BackupKey);
        if (string.IsNullOrWhiteSpace(previous)) return ActionResult.Skipped("Aucun réglage TCP à restaurer.");

        if (!Levels.Contains(previous))
        {
            Log.Warn($"[{Id}] Niveau d'auto-tuning sauvegardé invalide rejeté : {previous}");
            store.Clear(Id);
            return ActionResult.Skipped("Sauvegarde TCP invalide ignorée.");
        }

        var (code, output) = Run("netsh", "int", "tcp", "set", "global", $"autotuninglevel={previous}");
        if (code != 0) return ActionResult.Failed($"Restauration refusée : {FirstLine(output)}");

        store.Clear(Id);
        return ActionResult.Reverted($"Auto-tuning TCP remis sur « {previous} ».");
    }

    private static readonly string[] Levels =
        { "disabled", "highlyrestricted", "restricted", "normal", "experimental" };
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
        // Le jeton d'annulation est transmis : « Annuler » interrompt réellement defrag.
        var (code, output) = RunWithTimeout(15 * 60_000, ct, "defrag", drive, "/O", "/H");

        return code != 0
            ? ActionResult.Failed($"Entretien interrompu : {FirstLine(output)}")
            : ActionResult.Applied($"Entretien de {drive} terminé ({MediaType()}).");
    }

    public override ActionResult Revert(RestoreStore store) =>
        ActionResult.Skipped("Entretien disque : aucune modification à annuler.");

    private static string MediaType()
    {
        var (code, output) = RunWithTimeout(20_000, CancellationToken.None, "powershell",
            "-NoProfile", "-NonInteractive", "-Command", "(Get-PhysicalDisk | Select-Object -First 1).MediaType");
        var t = output.Trim();
        return code == 0 && t.Length > 0 ? t : "type de disque inconnu";
    }
}

