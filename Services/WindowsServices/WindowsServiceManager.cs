using System.Management;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using Aether.Models;

namespace Aether.Services.WindowsServices;

/// <summary>
/// Accès réel aux services Windows : lecture d'état via <see cref="ServiceController"/>,
/// changement du type de démarrage via WMI (Win32_Service.ChangeStartMode).
/// Toutes les opérations sont non bloquantes et ne lèvent jamais : un service absent de
/// l'édition de Windows installée est simplement signalé comme non installé.
/// </summary>
public class WindowsServiceManager
{
    /// <summary>Vrai si AETHER dispose des droits administrateur (obligatoires pour écrire).</summary>
    public bool IsElevated { get; } = CheckElevation();

    private static bool CheckElevation()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Message affiché tant qu'AETHER n'est pas élevé.</summary>
    public const string ElevationMessage =
        "AETHER n'est pas lancé en administrateur : l'état des services est affiché en lecture seule. " +
        "Relancez avec élévation pour pouvoir les modifier.";

    // ------------------------------------------------------------------ lecture

    /// <summary>
    /// Confronte le catalogue à la machine : résout les noms des services par utilisateur,
    /// lit statut et type de démarrage, et marque comme non installé ce qui n'existe pas ici.
    /// </summary>
    public async Task RefreshAllAsync(IEnumerable<WindowsServiceInfo> services, CancellationToken ct = default)
    {
        var targets = services.ToList();

        var snapshot = await Task.Run(() =>
        {
            var map = new Dictionary<string, (ServiceControllerStatus Status, ServiceStartMode Start, string Name)>(
                StringComparer.OrdinalIgnoreCase);

            ServiceController[] all;
            try { all = ServiceController.GetServices(); }
            catch { return map; }

            foreach (var sc in all)
            {
                using (sc)
                {
                    ct.ThrowIfCancellationRequested();
                    try { map[sc.ServiceName] = (sc.Status, sc.StartType, sc.ServiceName); }
                    catch { /* service en cours de suppression */ }
                }
            }
            return map;
        }, ct);

        foreach (var info in targets)
        {
            ct.ThrowIfCancellationRequested();

            var resolved = info.IsPerUserService
                ? ResolvePerUserName(snapshot.Keys, info.ServiceName)
                : (snapshot.ContainsKey(info.ServiceName) ? info.ServiceName : null);

            if (resolved == null || !snapshot.TryGetValue(resolved, out var state))
            {
                info.IsInstalled = false;
                continue;
            }

            info.ResolvedName = resolved;
            info.CurrentStatus = state.Status;
            info.StartType = state.Start;
            info.IsInstalled = true;
        }
    }

    /// <summary>
    /// Les services par utilisateur portent un suffixe de session (« CDPUserSvc_4a1b2 »).
    /// On retient l'instance de la session courante, sinon le modèle sans suffixe.
    /// </summary>
    private static string? ResolvePerUserName(IEnumerable<string> installed, string prefix)
    {
        var candidates = installed
            .Where(n => n.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                     || n.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Length)   // le modèle sans suffixe d'abord
            .ToList();

        return candidates.FirstOrDefault(n => n.Contains('_')) ?? candidates.FirstOrDefault();
    }

    public async Task<(ServiceControllerStatus Status, ServiceStartMode StartType)?> GetServiceStatusAsync(
        string serviceName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                return ((ServiceControllerStatus, ServiceStartMode)?)(sc.Status, sc.StartType);
            }
            catch { return null; }   // service inexistant sur cette édition de Windows
        }, ct);
    }

    // ------------------------------------------------------------------ écriture

    /// <summary>Résultat d'une opération : succès, ou message expliquant l'échec.</summary>
    public record ServiceResult(bool Success, string Message)
    {
        public static ServiceResult Ok(string m = "") => new(true, m);
        public static ServiceResult Fail(string m) => new(false, m);
    }

    /// <summary>
    /// Change le type de démarrage via WMI.
    /// Les instances de services par utilisateur (« CDPUserSvc_4a1b2 ») sont bien listées
    /// par Win32_Service, mais ChangeStartMode les refuse (code 21) : Windows ne configure
    /// que le service modèle. Pour celles-ci on écrit directement la valeur Start du modèle
    /// dans le registre, ce que Windows applique aux prochaines sessions.
    /// </summary>
    public async Task<ServiceResult> SetStartTypeAsync(string serviceName, ServiceStartMode mode,
                                                       bool perUserService = false,
                                                       CancellationToken ct = default)
    {
        if (!IsElevated) return ServiceResult.Fail("Droits administrateur requis.");

        if (perUserService)
            return await Task.Run(() => SetStartTypeViaRegistry(serviceName, mode), ct);

        return await Task.Run(() =>
        {
            try
            {
                using var wmi = new ManagementObject($"Win32_Service.Name='{Escape(serviceName)}'");
                wmi.Get();

                using var args = wmi.GetMethodParameters("ChangeStartMode");
                args["StartMode"] = WmiStartMode(mode);

                using var result = wmi.InvokeMethod("ChangeStartMode", args, null);
                var code = Convert.ToUInt32(result?["ReturnValue"] ?? 1u);

                return code == 0 ? ServiceResult.Ok() : ServiceResult.Fail(WmiError(code));
            }
            catch (ManagementException)
            {
                // Service absent de Win32_Service : cas des services par utilisateur.
                return SetStartTypeViaRegistry(serviceName, mode);
            }
            catch (Exception ex) { return ServiceResult.Fail(ex.Message); }
        }, ct);
    }

    /// <summary>Repli registre pour les services que WMI n'expose pas.</summary>
    private static ServiceResult SetStartTypeViaRegistry(string serviceName, ServiceStartMode mode)
    {
        // Un service par utilisateur se configure sur son modèle, donc sans le suffixe de session.
        var template = serviceName.Contains('_') ? serviceName[..serviceName.LastIndexOf('_')] : serviceName;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{template}", writable: true);

            if (key == null) return ServiceResult.Fail($"Service « {template} » introuvable dans le registre.");

            key.SetValue("Start", RegistryStartValue(mode), RegistryValueKind.DWord);
            return ServiceResult.Ok("Appliqué au prochain démarrage de session.");
        }
        catch (Exception ex) { return ServiceResult.Fail(ex.Message); }
    }

    public async Task<ServiceResult> StartServiceAsync(string serviceName, CancellationToken ct = default)
    {
        if (!IsElevated) return ServiceResult.Fail("Droits administrateur requis.");

        return await Task.Run(() =>
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                if (sc.Status == ServiceControllerStatus.Running) return ServiceResult.Ok();

                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                return ServiceResult.Ok();
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                return ServiceResult.Fail("Le service n'a pas démarré dans le délai imparti.");
            }
            catch (Exception ex) { return ServiceResult.Fail(ex.Message); }
        }, ct);
    }

    public async Task<ServiceResult> StopServiceAsync(string serviceName, CancellationToken ct = default)
    {
        if (!IsElevated) return ServiceResult.Fail("Droits administrateur requis.");

        return await Task.Run(() =>
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                if (sc.Status == ServiceControllerStatus.Stopped) return ServiceResult.Ok();

                if (!sc.CanStop)
                    return ServiceResult.Fail("Ce service refuse l'arrêt à chaud ; il restera actif jusqu'au redémarrage.");

                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                return ServiceResult.Ok();
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                return ServiceResult.Fail("Le service ne s'est pas arrêté dans le délai imparti.");
            }
            catch (Exception ex) { return ServiceResult.Fail(ex.Message); }
        }, ct);
    }

    // ------------------------------------------------------------------ mapping

    private static string WmiStartMode(ServiceStartMode mode) => mode switch
    {
        ServiceStartMode.Automatic => "Automatic",
        ServiceStartMode.Disabled => "Disabled",
        ServiceStartMode.Boot => "Boot",
        ServiceStartMode.System => "System",
        _ => "Manual"
    };

    private static int RegistryStartValue(ServiceStartMode mode) => mode switch
    {
        ServiceStartMode.Boot => 0,
        ServiceStartMode.System => 1,
        ServiceStartMode.Automatic => 2,
        ServiceStartMode.Disabled => 4,
        _ => 3
    };

    /// <summary>Codes de retour de Win32_Service.ChangeStartMode.</summary>
    private static string WmiError(uint code) => code switch
    {
        1 => "Opération non prise en charge par ce service.",
        21 => "Paramètre refusé par Windows pour ce type de service.",
        2 => "Accès refusé.",
        3 => "Dépendance active : un autre service a besoin de celui-ci.",
        5 => "Le service est dans un état qui interdit ce changement.",
        8 => "Échec inconnu signalé par Windows.",
        15 => "Le service est verrouillé par Windows.",
        22 => "Compte de service invalide.",
        _ => $"Windows a refusé le changement (code {code})."
    };

    private static string Escape(string name) => name.Replace("\\", "\\\\").Replace("'", "\\'");
}
