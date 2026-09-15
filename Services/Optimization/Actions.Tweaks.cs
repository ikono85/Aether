using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Aether.Services.Infrastructure;

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

    private static readonly HashSet<string> Targets = new(StringComparer.Ordinal)
    {
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects|VisualFXSetting",
        @"Control Panel\Desktop|UserPreferencesMask",
        @"Control Panel\Desktop|DragFullWindows",
        @"Control Panel\Desktop|MenuShowDelay",
        @"Control Panel\Desktop\WindowMetrics|MinAnimate",
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize|EnableTransparency",
    };

    protected override bool AllowsRegistryTarget(string hive, string subKey, string valueName) =>
        hive == "HKCU" && Targets.Contains($"{subKey}|{valueName}");

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
        catch (Exception ex)
        {
            Log.Warn($"[{Id}] Application partielle.", ex);
            return ActionResult.Failed(ex.Message);
        }
    }

    public override ActionResult Revert(RestoreStore store)
    {
        var reg = RevertRegistry(store);
        Broadcast();
        return FinishRevert(store, reg, "Effets visuels Windows rétablis.", "Aucun effet à rétablir.");
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

    private const string UsbRoot = @"SYSTEM\CurrentControlSet\Enum\USB";
    private static readonly Regex DevicePath =
        new(@"^SYSTEM\\CurrentControlSet\\Enum\\USB\\[^\\]+\\[^\\]+\\Device Parameters$", RegexOptions.IgnoreCase);

    protected override bool AllowsRegistryTarget(string hive, string subKey, string valueName) =>
        hive == "HKLM" && valueName == "EnhancedPowerManagementEnabled" && DevicePath.IsMatch(subKey);

    public override ActionResult Apply(RestoreStore store, CancellationToken ct)
    {
        if (!store.Contains(BackupKey))
        {
            var current = CurrentIndexes();
            if (current == null) return ActionResult.Failed("Réglage USB actuel illisible : rien n'a été modifié.");
            store.Write(BackupKey, current);
        }

        var (code, output) = Run("powercfg", "/setacvalueindex", "SCHEME_CURRENT", SubGroup, Setting, "0");
        if (code != 0) return ActionResult.Failed($"Réglage refusé : {FirstLine(output)}");

        Run("powercfg", "/setdcvalueindex", "SCHEME_CURRENT", SubGroup, Setting, "0");
        Run("powercfg", "/setactive", "SCHEME_CURRENT");
        Log.Audit($"[{Id}] Suspension sélective USB désactivée (AC/DC).");

        int devices = IsElevated ? DisableDeviceSaving(store) : 0;
        string note = IsElevated
            ? $" {devices} contrôleur(s) USB verrouillé(s) en alimentation permanente."
            : " (les réglages par périphérique demandent les droits administrateur)";

        return ActionResult.Applied($"Suspension sélective USB désactivée.{note}");
    }

    public override ActionResult Revert(RestoreStore store)
    {
        // Valeur par défaut de Windows : suspension sélective activée sur secteur et batterie.
        var saved = store.Read<PowerIndexes>(BackupKey);
        var previous = saved is { Ac: 0 or 1, Dc: 0 or 1 } ? saved : new PowerIndexes(1, 1);

        var reg = RevertRegistry(store);

        var (code, output) = Run("powercfg", "/setacvalueindex", "SCHEME_CURRENT", SubGroup, Setting, previous.Ac.ToString());
        Run("powercfg", "/setdcvalueindex", "SCHEME_CURRENT", SubGroup, Setting, previous.Dc.ToString());
        Run("powercfg", "/setactive", "SCHEME_CURRENT");

        if (code != 0) return ActionResult.Failed($"Restauration refusée : {FirstLine(output)}");
        if (reg.Failed > 0) return FinishRevert(store, reg, "", "");

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
        var (code, output) = Run("powercfg", "/query", "SCHEME_CURRENT", SubGroup, Setting);
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
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(UsbRoot);
            if (usb == null) return 0;

            foreach (var device in usb.GetSubKeyNames())
            {
                using var dev = usb.OpenSubKey(device);
                if (dev == null) continue;

                foreach (var instance in dev.GetSubKeyNames())
                {
                    var path = $@"{UsbRoot}\{device}\{instance}\Device Parameters";
                    try
                    {
                        using var probe = Registry.LocalMachine.OpenSubKey(path);
                        if (probe?.GetValue("EnhancedPowerManagementEnabled") == null) continue;

                        SetRegistry(store, Registry.LocalMachine, path,
                            "EnhancedPowerManagementEnabled", 0, RegistryValueKind.DWord);
                        n++;
                    }
                    catch (Exception ex) { Log.Warn($"[{Id}] Périphérique ignoré : {path}", ex); }
                }
            }
        }
        catch (Exception ex) { Log.Warn($"[{Id}] Énumération USB impossible.", ex); }
        return n;
    }
}

/// <summary>Coupe la Xbox Game Bar et la capture en arrière-plan (Game DVR).</summary>
public class GameBarOffAction : OptimizationAction
{
    public override string Id => "gamebar";

    private static readonly HashSet<string> Targets = new(StringComparer.Ordinal)
    {
        @"HKCU|System\GameConfigStore|GameDVR_Enabled",
        @"HKCU|Software\Microsoft\Windows\CurrentVersion\GameDVR|AppCaptureEnabled",
        @"HKCU|Software\Microsoft\GameBar|UseNexusForGameBarEnabled",
        @"HKCU|Software\Microsoft\GameBar|ShowStartupPanel",
        @"HKLM|SOFTWARE\Policies\Microsoft\Windows\GameDVR|AllowGameDVR",
    };

    protected override bool AllowsRegistryTarget(string hive, string subKey, string valueName) =>
        Targets.Contains($"{hive}|{subKey}|{valueName}");

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
        catch (Exception ex)
        {
            Log.Warn($"[{Id}] Application partielle.", ex);
            return ActionResult.Failed(ex.Message);
        }
    }

    public override ActionResult Revert(RestoreStore store) =>
        FinishRevert(store, RevertRegistry(store), "Xbox Game Bar rétablie.", "Rien à rétablir.");
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

/// <summary>
/// Ancien module « Service Trimmer », DÉPLACÉ dans l'onglet Services (profil « Services rarement
/// utiles ») : deux mécanismes modifiant les mêmes services avec deux journaux distincts
/// affichaient des états contradictoires. La classe ne sert plus qu'à annuler une application
/// faite par une version précédente.
/// </summary>
public class ServiceTrimmerAction : OptimizationAction
{
    public override string Id => "services";
    public override bool RequiresAdmin => true;

    private static readonly string[] Targets =
        { "Fax", "RemoteRegistry", "RetailDemo", "MapsBroker", "WMPNetworkSvc", "PhoneSvc" };

    protected override bool AllowsRegistryTarget(string hive, string subKey, string valueName) =>
        hive == "HKLM" && valueName == "Start" &&
        Targets.Any(t => subKey.Equals($@"SYSTEM\CurrentControlSet\Services\{t}", StringComparison.Ordinal));

    public override ActionResult Apply(RestoreStore store, CancellationToken ct) =>
        ActionResult.Skipped("Module déplacé : onglet Services, profil « Services rarement utiles ».");

    public override ActionResult Revert(RestoreStore store)
    {
        if (!IsElevated) return ActionResult.Skipped("Droits administrateur requis.");
        var reg = RevertRegistry(store);
        return FinishRevert(store, reg, $"{reg.Restored} service(s) remis dans leur type de démarrage d'origine.",
                            "Aucun service à rétablir.");
    }
}

/// <summary>
/// Ancien module « Telemetry Block », DÉPLACÉ dans l'onglet Services (profil « Télémétrie
/// Windows »). Conservé uniquement pour annuler une application faite par une version précédente
/// (services, tâches planifiées et stratégie AllowTelemetry, qui n'avait d'effet que sur les
/// éditions Entreprise et Éducation).
/// </summary>
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

    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";

    protected override bool AllowsRegistryTarget(string hive, string subKey, string valueName) =>
        hive == "HKLM" && (
            (subKey == PolicyKey && valueName == "AllowTelemetry") ||
            (valueName == "Start" && Services.Any(s =>
                subKey.Equals($@"SYSTEM\CurrentControlSet\Services\{s}", StringComparison.Ordinal))));

    public override ActionResult Apply(RestoreStore store, CancellationToken ct) =>
        ActionResult.Skipped("Module déplacé : onglet Services, profil « Télémétrie Windows ».");

    public override ActionResult Revert(RestoreStore store)
    {
        if (!IsElevated) return ActionResult.Skipped("Droits administrateur requis.");

        var reg = RevertRegistry(store);

        // Seules les tâches connues de ce module sont réactivées, quel que soit le contenu du journal.
        var saved = store.Read<string[]>("telemetry/tasks") ?? Array.Empty<string>();
        foreach (var task in saved.Where(t => Tasks.Contains(t, StringComparer.Ordinal)))
            Run("schtasks", "/Change", "/TN", task, "/Enable");

        return FinishRevert(store, reg, "Télémétrie Windows rétablie dans son état d'origine.", "Rien à rétablir.");
    }
}
