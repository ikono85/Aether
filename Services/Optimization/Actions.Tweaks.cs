using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Aether.Services.Optimization;

/// <summary>Bascule Windows sur « meilleures performances » : animations, ombres et transparences coupées.</summary>
public class VisualFxOffAction : OptimizationAction
{
    public override string Id => "visualfx";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr ptr, uint winIni);

    private const uint SPI_SETUIEFFECTS = 0x103F;
    private const uint SPIF_SENDCHANGE = 0x02;

    /// <summary>Masque « meilleures performances » utilisé par le panneau Performances de Windows.</summary>
    private static readonly byte[] PerfMask = { 0x90, 0x12, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00 };

    public override ActionResult Apply(RestoreStore store, CancellationToken ct)
    {
        try
        {
            SetRegistry(store, Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects",
                "VisualFXSetting", 2, RegistryValueKind.DWord);

            SetRegistry(store, Registry.CurrentUser,
                @"Control Panel\Desktop", "UserPreferencesMask", PerfMask, RegistryValueKind.Binary);

            SetRegistry(store, Registry.CurrentUser,
                @"Control Panel\Desktop", "DragFullWindows", "0", RegistryValueKind.String);

            SetRegistry(store, Registry.CurrentUser,
                @"Control Panel\Desktop", "MenuShowDelay", "0", RegistryValueKind.String);

            SetRegistry(store, Registry.CurrentUser,
                @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", RegistryValueKind.String);

            SetRegistry(store, Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "EnableTransparency", 0, RegistryValueKind.DWord);

            Broadcast();
            return ActionResult.Applied("Effets visuels coupés (animations, ombres, transparence).");
        }
        catch (Exception ex) { return ActionResult.Failed(ex.Message); }
    }

    public override ActionResult Revert(RestoreStore store)
    {
        int n = RevertRegistry(store);
        store.Clear(Id);
        Broadcast();
        return n == 0 ? ActionResult.Skipped("Aucun effet à rétablir.")
                      : ActionResult.Reverted("Effets visuels Windows rétablis.");
    }

    /// <summary>Applique le changement sans redémarrer la session.</summary>
    private static void Broadcast()
    {
        try { SystemParametersInfo(SPI_SETUIEFFECTS, 0, IntPtr.Zero, SPIF_SENDCHANGE); } catch { }
    }
}

/// <summary>Empêche Windows de couper l'alimentation des ports USB pour économiser l'énergie.</summary>
public class UsbPowerKeepAction : OptimizationAction
{
    public override string Id => "usb_power";

    // Sous-groupe « Paramètres USB » / réglage « Suspension sélective USB ».
    private const string SubGroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string Setting = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string BackupKey = "usb_power/selective_suspend";

    public override ActionResult Apply(RestoreStore store, CancellationToken ct)
    {
        if (!store.Contains(BackupKey))
        {
            var current = CurrentIndexes();
            if (current != null) store.Write(BackupKey, current);
        }

        var (code, output) = Run("powercfg", $"/setacvalueindex SCHEME_CURRENT {SubGroup} {Setting} 0");
        if (code != 0) return ActionResult.Failed($"Réglage refusé : {output.Trim()}");

        Run("powercfg", $"/setdcvalueindex SCHEME_CURRENT {SubGroup} {Setting} 0");
        Run("powercfg", "/setactive SCHEME_CURRENT");

        int devices = IsElevated ? DisableDeviceSaving(store) : 0;
        string note = IsElevated
            ? $" {devices} contrôleur(s) USB verrouillé(s) en alimentation permanente."
            : " (les réglages par périphérique demandent les droits administrateur)";

        return ActionResult.Applied($"Suspension sélective USB désactivée.{note}");
    }

    public override ActionResult Revert(RestoreStore store)
    {
        // Valeur par défaut de Windows : suspension sélective activée sur secteur et batterie.
        var previous = store.Read<PowerIndexes>(BackupKey) ?? new PowerIndexes(1, 1);
        RevertRegistry(store);

        var (code, output) = Run("powercfg", $"/setacvalueindex SCHEME_CURRENT {SubGroup} {Setting} {previous.Ac}");
        Run("powercfg", $"/setdcvalueindex SCHEME_CURRENT {SubGroup} {Setting} {previous.Dc}");
        Run("powercfg", "/setactive SCHEME_CURRENT");

        if (code != 0) return ActionResult.Failed($"Restauration refusée : {output.Trim()}");

        store.Clear(Id);
        return ActionResult.Reverted("Gestion d'énergie USB d'origine rétablie.");
    }

    /// <summary>Index du réglage sur secteur (AC) et sur batterie (DC) : ils peuvent différer.</summary>
    public record PowerIndexes(int Ac, int Dc);

    /// <summary>
    /// Lit les deux index courants. powercfg les imprime dans l'ordre (secteur puis batterie),
    /// en hexadécimal — ce sont les seules valeurs préfixées « 0x » de la sortie.
    /// </summary>
    private static PowerIndexes? CurrentIndexes()
    {
        var (code, output) = Run("powercfg", $"/query SCHEME_CURRENT {SubGroup} {Setting}");
        if (code != 0) return null;

        var matches = Regex.Matches(output, @"0x([0-9a-fA-F]{8})");
        if (matches.Count < 2) return null;

        return new PowerIndexes(
            Convert.ToInt32(matches[^2].Groups[1].Value, 16),
            Convert.ToInt32(matches[^1].Groups[1].Value, 16));
    }

    /// <summary>Coupe « Autoriser l'ordinateur à éteindre ce périphérique » sur les hubs USB.</summary>
    private int DisableDeviceSaving(RestoreStore store)
    {
        int n = 0;
        const string root = @"SYSTEM\CurrentControlSet\Enum\USB";
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(root);
            if (usb == null) return 0;

            foreach (var device in usb.GetSubKeyNames())
            {
                using var dev = usb.OpenSubKey(device);
                if (dev == null) continue;

                foreach (var instance in dev.GetSubKeyNames())
                {
                    var path = $@"{root}\{device}\{instance}\Device Parameters";
                    try
                    {
                        using var probe = Registry.LocalMachine.OpenSubKey(path);
                        if (probe?.GetValue("EnhancedPowerManagementEnabled") == null) continue;

                        SetRegistry(store, Registry.LocalMachine, path,
                            "EnhancedPowerManagementEnabled", 0, RegistryValueKind.DWord);
                        n++;
                    }
                    catch { /* périphérique protégé */ }
                }
            }
        }
        catch { }
        return n;
    }
}

/// <summary>Coupe la Xbox Game Bar et la capture en arrière-plan (Game DVR).</summary>
public class GameBarOffAction : OptimizationAction
{
    public override string Id => "gamebar";

    public override ActionResult Apply(RestoreStore store, CancellationToken ct)
    {
        try
        {
            SetRegistry(store, Registry.CurrentUser,
                @"System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord);

            SetRegistry(store, Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\GameDVR",
                "AppCaptureEnabled", 0, RegistryValueKind.DWord);

            SetRegistry(store, Registry.CurrentUser,
                @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0, RegistryValueKind.DWord);

            SetRegistry(store, Registry.CurrentUser,
                @"Software\Microsoft\GameBar", "ShowStartupPanel", 0, RegistryValueKind.DWord);

            string scope = "profil utilisateur";
            if (IsElevated)
            {
                SetRegistry(store, Registry.LocalMachine,
                    @"SOFTWARE\Policies\Microsoft\Windows\GameDVR",
                    "AllowGameDVR", 0, RegistryValueKind.DWord);
                scope = "tout le système";
            }

            return ActionResult.Applied($"Game Bar et capture en fond désactivées ({scope}).");
        }
        catch (Exception ex) { return ActionResult.Failed(ex.Message); }
    }

    public override ActionResult Revert(RestoreStore store)
    {
        int n = RevertRegistry(store);
        store.Clear(Id);
        return n == 0 ? ActionResult.Skipped("Rien à rétablir.")
                      : ActionResult.Reverted("Xbox Game Bar rétablie.");
    }
}

/// <summary>
/// Recense les overlays de jeu actifs. Chaque éditeur stocke ce réglage dans son propre
/// format interne : couper l'overlay à leur place casserait leur configuration, donc ce
/// module signale ce qui tourne et laisse la désactivation à l'application concernée.
/// </summary>
public class OverlayCleanerAction : OptimizationAction
{
    public override string Id => "overlay";

    private static readonly (string Process, string Label, string Where)[] Known =
    {
        ("Discord",         "Discord",            "Paramètres › Overlay de jeu"),
        ("GameOverlayUI",   "Steam",              "Steam › Paramètres › Dans le jeu"),
        ("NVIDIA Share",    "GeForce Experience", "GeForce Experience › Paramètres › Overlay"),
        ("NVIDIA Overlay",  "NVIDIA Overlay",     "GeForce Experience › Paramètres › Overlay"),
        ("MSIAfterburner",  "MSI Afterburner",    "Afterburner › Monitoring › OSD"),
        ("RTSS",            "RivaTuner",          "RivaTuner › Show On-Screen Display"),
        ("GameBar",         "Xbox Game Bar",      "module « Game Bar Off » de cette page"),
    };

    public override ActionResult Apply(RestoreStore store, CancellationToken ct)
    {
        var found = new List<string>();

        foreach (var (process, label, where) in Known)
        {
            ct.ThrowIfCancellationRequested();
            Process[] running;
            try { running = Process.GetProcessesByName(process); } catch { continue; }

            try
            {
                if (running.Length > 0) found.Add($"{label} → {where}");
            }
            finally { foreach (var p in running) p.Dispose(); }
        }

        return found.Count == 0
            ? ActionResult.Skipped("Aucun overlay de jeu détecté.")
            : ActionResult.Applied($"{found.Count} overlay(s) actif(s) — {string.Join(" · ", found)}");
    }

    public override ActionResult Revert(RestoreStore store) =>
        ActionResult.Skipped("Détection seule : aucune modification n'a été faite.");
}

/// <summary>Passe en démarrage manuel une courte liste de services Windows non essentiels.</summary>
public class ServiceTrimmerAction : OptimizationAction
{
    public override string Id => "services";
    public override bool RequiresAdmin => true;

    /// <summary>Liste volontairement courte et conservatrice : aucun service critique.</summary>
    private static readonly (string Name, string Label)[] Targets =
    {
        ("Fax",             "Télécopie"),
        ("RemoteRegistry",  "Registre à distance"),
        ("RetailDemo",      "Mode démonstration magasin"),
        ("MapsBroker",      "Cartes hors connexion"),
        ("WMPNetworkSvc",   "Partage Windows Media Player"),
        ("PhoneSvc",        "Service de téléphonie"),
    };

    private const int Manual = 3;

    public override ActionResult Apply(RestoreStore store, CancellationToken ct)
    {
        if (!IsElevated) return ActionResult.Skipped("Droits administrateur requis.");

        var changed = new List<string>();

        foreach (var (name, label) in Targets)
        {
            ct.ThrowIfCancellationRequested();
            var path = $@"SYSTEM\CurrentControlSet\Services\{name}";

            try
            {
                using var probe = Registry.LocalMachine.OpenSubKey(path);
                if (probe == null) continue;                                   // service absent
                if (probe.GetValue("Start") is not int start || start >= Manual) continue;

                SetRegistry(store, Registry.LocalMachine, path, "Start", Manual, RegistryValueKind.DWord);
                Run("sc", $"stop {name}", 10_000);
                changed.Add(label);
            }
            catch { /* service protégé */ }
        }

        return changed.Count == 0
            ? ActionResult.Skipped("Tous ces services sont déjà au minimum.")
            : ActionResult.Applied($"{changed.Count} service(s) en démarrage manuel : {string.Join(", ", changed)}.");
    }

    public override ActionResult Revert(RestoreStore store)
    {
        if (!IsElevated) return ActionResult.Skipped("Droits administrateur requis.");
        int n = RevertRegistry(store);
        store.Clear(Id);
        return n == 0 ? ActionResult.Skipped("Aucun service à rétablir.")
                      : ActionResult.Reverted($"{n} service(s) remis en démarrage automatique.");
    }
}

/// <summary>Coupe la collecte de données de diagnostic de Windows.</summary>
public class TelemetryBlockAction : OptimizationAction
{
    public override string Id => "telemetry";
    public override bool RequiresAdmin => true;

    private static readonly string[] Services = { "DiagTrack", "dmwappushservice" };

    private static readonly string[] Tasks =
    {
        @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
        @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
        @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
        @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
    };

    private const int Disabled = 4;

    public override ActionResult Apply(RestoreStore store, CancellationToken ct)
    {
        if (!IsElevated) return ActionResult.Skipped("Droits administrateur requis.");

        int services = 0, tasks = 0;

        foreach (var name in Services)
        {
            ct.ThrowIfCancellationRequested();
            var path = $@"SYSTEM\CurrentControlSet\Services\{name}";
            try
            {
                using var probe = Registry.LocalMachine.OpenSubKey(path);
                if (probe == null) continue;

                SetRegistry(store, Registry.LocalMachine, path, "Start", Disabled, RegistryValueKind.DWord);
                Run("sc", $"stop {name}", 10_000);
                services++;
            }
            catch { }
        }

        try
        {
            SetRegistry(store, Registry.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                "AllowTelemetry", 0, RegistryValueKind.DWord);
        }
        catch { }

        foreach (var task in Tasks)
        {
            ct.ThrowIfCancellationRequested();
            var (code, _) = Run("schtasks", $"/Change /TN \"{task}\" /Disable", 10_000);
            if (code == 0) tasks++;
        }

        store.Write("telemetry/tasks", Tasks);

        return services == 0 && tasks == 0
            ? ActionResult.Skipped("Télémétrie déjà inactive.")
            : ActionResult.Applied($"{services} service(s) et {tasks} tâche(s) planifiée(s) de télémétrie désactivés.");
    }

    public override ActionResult Revert(RestoreStore store)
    {
        if (!IsElevated) return ActionResult.Skipped("Droits administrateur requis.");

        int n = RevertRegistry(store);

        foreach (var task in store.Read<string[]>("telemetry/tasks") ?? Array.Empty<string>())
            Run("schtasks", $"/Change /TN \"{task}\" /Enable", 10_000);

        store.Clear(Id);
        return n == 0 ? ActionResult.Skipped("Rien à rétablir.")
                      : ActionResult.Reverted("Télémétrie Windows rétablie dans son état d'origine.");
    }
}
