using System.ServiceProcess;
using Aether.Services.Infrastructure;
using Aether.Services.WindowsServices;

namespace Aether.Services.History;

/// <summary>
/// Restauration d'un service sans passer par l'onglet Services (Historique, ligne de commande).
/// Agit sur les objets partagés du catalogue : si l'onglet Services est ouvert, sa ligne se met
/// à jour d'elle-même.
/// </summary>
public sealed class ServiceRestorer
{
    private readonly WindowsServiceManager _manager;
    private readonly ServiceChangeLog _log;

    public ServiceRestorer(WindowsServiceManager manager, ServiceChangeLog log)
    {
        _manager = manager;
        _log = log;
    }

    public async Task<(bool Success, string Message)> RestoreAsync(string serviceName)
    {
        if (!_manager.IsElevated) return (false, "Droits administrateur requis.");

        var info = WindowsServiceCatalog.All
            .FirstOrDefault(s => s.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
        if (info == null) return (false, "Service hors catalogue.");

        await _manager.RefreshAllAsync(new[] { info });

        if (!info.IsInstalled)
        {
            _log.Forget(info.ServiceName);
            return (true, "service absent de ce PC, historique retiré.");
        }

        var target = _log.OriginalStartType(info.ServiceName) ?? info.DefaultStartType;
        var result = await _manager.SetStartTypeAsync(info.ResolvedName, target, info.IsPerUserService);
        if (!result.Success)
        {
            Log.Warn($"Restauration du service {info.ResolvedName} refusée : {result.Message}");
            return (false, result.Message);
        }

        if (target == ServiceStartMode.Automatic) await _manager.StartServiceAsync(info.ResolvedName);

        info.StartType = target;
        _log.Forget(info.ServiceName);
        Log.Audit($"Service {info.ResolvedName} restauré : démarrage {ChangeHistoryService.Label(target)}.");

        return (true, $"démarrage {ChangeHistoryService.Label(target)} rétabli" +
                      (result.Message.Length > 0 ? $" ({result.Message})" : "") + ".");
    }
}
