using System.ServiceProcess;
using System.Text.Json;
using Aether.Services.Infrastructure;
using Aether.Services.Optimization;
using Aether.Services.WindowsServices;

namespace Aether.Services.History;

public enum ChangeKind { Optimisation, Service, Dns }

/// <summary>Une modification de Windows actuellement en place, et restaurable.</summary>
public sealed record ChangeItem(ChangeKind Kind, string Key, string Title, string Detail, DateTime? When)
{
    public string KindLabel => Kind switch
    {
        ChangeKind.Optimisation => "OPTIMISATION",
        ChangeKind.Service => "SERVICE",
        _ => "DNS"
    };

    public string WhenText => When is { } w ? w.ToString("dd/MM/yyyy HH:mm") : "date non enregistrée";
}

/// <summary>
/// Vue unifiée de tout ce qu'AETHER a modifié : optimisations (restore.json), services
/// (service-changes.json) et DNS (dns-backups.json). Sert l'onglet Historique, la ligne de
/// commande <c>--restore-all</c> et donc la désinstallation.
/// </summary>
public sealed class ChangeHistoryService
{
    private static readonly Dictionary<string, string> ModuleTitles = new()
    {
        ["gaming_boost"] = "Gaming Boost — plan d'alimentation et mode Jeu",
        ["startup"] = "Startup Manager — programmes lancés au démarrage",
        ["network"] = "Network Accelerator — auto-tuning TCP (ancien module)",
        ["visualfx"] = "Visual FX Off — effets visuels de Windows",
        ["usb_power"] = "USB Power Keep — gestion d'énergie USB",
        ["gamebar"] = "Game Bar Off — Xbox Game Bar",
        ["services"] = "Service Trimmer (ancien module)",
        ["telemetry"] = "Telemetry Block (ancien module)",
    };

    private readonly OptimizationEngine _engine;
    private readonly ServiceChangeLog _serviceLog;
    private readonly ServiceRestorer _serviceRestorer;

    /// <summary>Une modification a été appliquée ou restaurée, ici ou dans un autre onglet.</summary>
    public event Action? Changed;

    public ChangeHistoryService(OptimizationEngine engine, ServiceChangeLog serviceLog, ServiceRestorer serviceRestorer)
    {
        _engine = engine;
        _serviceLog = serviceLog;
        _serviceRestorer = serviceRestorer;
    }

    public void NotifyChanged() => Changed?.Invoke();

    public IReadOnlyList<ChangeItem> List()
    {
        var items = new List<ChangeItem>();

        foreach (var id in _engine.AppliedModuleIds())
            items.Add(new ChangeItem(ChangeKind.Optimisation, id, ModuleTitles.GetValueOrDefault(id, id),
                                     "État d'origine sauvegardé", null));

        foreach (var group in _serviceLog.Entries.GroupBy(e => e.ServiceName, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var last = group.Last();
            var display = WindowsServiceCatalog.All
                .FirstOrDefault(s => s.ServiceName.Equals(group.Key, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? group.Key;
            items.Add(new ChangeItem(ChangeKind.Service, group.Key, display,
                                     $"Démarrage {Label(first.OldStartType)} → {Label(last.NewStartType)}", last.Timestamp));
        }

        foreach (var (key, backup) in NetworkToolbox.ListDnsBackups())
            items.Add(new ChangeItem(ChangeKind.Dns, key, $"DNS de « {backup.InterfaceName} »",
                                     backup.Dhcp ? "Origine : attribué automatiquement (DHCP)"
                                                 : $"Origine : {string.Join(", ", backup.Servers)}", null));

        return items;
    }

    /// <summary>Restaure une modification et renvoie le message à afficher.</summary>
    public async Task<string> RestoreAsync(ChangeItem item)
    {
        var (success, message) = await RestoreCoreAsync(item);
        Log.Audit($"Historique : {item.Title} — {(success ? "restauré" : "échec")} : {message}");
        Changed?.Invoke();
        return $"{item.Title} — {message}";
    }

    /// <summary>Restaure tout ce qui est en place. Chaque échec laisse sa sauvegarde intacte.</summary>
    public async Task<(int Restored, int Failed)> RestoreAllAsync(Action<string>? report = null)
    {
        int restored = 0, failed = 0;

        foreach (var item in List())
        {
            report?.Invoke($"Restauration : {item.Title}…");
            bool success;
            string message;
            try { (success, message) = await RestoreCoreAsync(item); }
            catch (Exception ex)
            {
                Log.Error($"Restauration de « {item.Title} » interrompue.", ex);
                (success, message) = (false, ex.Message);
            }

            Log.Audit($"Historique : {item.Title} — {(success ? "restauré" : "échec")} : {message}");
            if (success) restored++; else failed++;
        }

        Changed?.Invoke();
        return (restored, failed);
    }

    private async Task<(bool Success, string Message)> RestoreCoreAsync(ChangeItem item)
    {
        switch (item.Kind)
        {
            case ChangeKind.Optimisation:
                var result = await _engine.RevertOneAsync(item.Key);
                return (result.Outcome == ActionOutcome.Reverted ||
                        (result.Outcome == ActionOutcome.Skipped && !_engine.IsApplied(item.Key)), result.Message);

            case ChangeKind.Service:
                return await _serviceRestorer.RestoreAsync(item.Key);

            default:
                var message = await NetworkToolbox.RevertDnsByKeyAsync(item.Key);
                return (NetworkToolbox.ListDnsBackups().All(b => b.Key != item.Key), message);
        }
    }

    public string ExportJson() => JsonSerializer.Serialize(new
    {
        ExportedAt = DateTime.Now,
        Version = typeof(ChangeHistoryService).Assembly.GetName().Version?.ToString(3),
        Changes = List(),
        ServiceJournal = _serviceLog.Entries
    }, new JsonSerializerOptions { WriteIndented = true });

    internal static string Label(ServiceStartMode mode) => mode switch
    {
        ServiceStartMode.Automatic => "automatique",
        ServiceStartMode.Manual => "manuel",
        ServiceStartMode.Disabled => "désactivé",
        ServiceStartMode.Boot => "noyau",
        _ => "système"
    };
}
