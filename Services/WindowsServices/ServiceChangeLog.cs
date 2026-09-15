using System.ServiceProcess;
using Aether.Services.Infrastructure;

namespace Aether.Services.WindowsServices;

/// <summary>Une modification de type de démarrage, conservée pour permettre un retour en arrière.</summary>
public record ServiceChangeEntry(
    string ServiceName,
    ServiceStartMode OldStartType,
    ServiceStartMode NewStartType,
    DateTime Timestamp);

/// <summary>
/// Journal des changements appliqués aux services (%ProgramData%\Aether\service-changes.json,
/// accès administrateurs). Permet de restaurer un service dans l'état exact où AETHER l'a
/// trouvé, même après redémarrage.
///
/// - Les services sont identifiés par leur nom de CATALOGUE : un service par utilisateur
///   (« CDPUserSvc_4a1b2 ») change de suffixe à chaque ouverture de session, son nom résolu ne
///   permettrait plus de le retrouver.
/// - Seuls les services du catalogue sont acceptés : une entrée forgée désignant un autre
///   service est écartée au chargement au lieu d'être appliquée en administrateur.
/// - Le changement est journalisé AVANT d'être appliqué ; un journal illisible bloque les
///   modifications plutôt que d'être écrasé.
/// </summary>
public class ServiceChangeLog
{
    private readonly string _path;
    private readonly List<ServiceChangeEntry> _entries = new();
    private readonly HashSet<string> _known;
    private readonly object _gate = new();

    public string? LoadError { get; }

    public ServiceChangeLog(IEnumerable<string> catalogServiceNames)
        : this(catalogServiceNames, AppPaths.SecureFile("service-changes.json")) { }

    /// <summary>Emplacement explicite (tests).</summary>
    internal ServiceChangeLog(IEnumerable<string> catalogServiceNames, string path)
    {
        _known = new HashSet<string>(catalogServiceNames, StringComparer.OrdinalIgnoreCase);
        _path = path;

        var (stored, status) = SafeFile.ReadJson<List<ServiceChangeEntry>>(_path);
        if (status == LoadStatus.Corrupt)
            LoadError = "Journal des services illisible (une copie « .corrupt » a été conservée) : " +
                        "modifications bloquées pour ne pas perdre les états d'origine.";

        foreach (var e in stored ?? new List<ServiceChangeEntry>())
        {
            var name = Normalize(e.ServiceName);
            if (name == null || !Enum.IsDefined(e.OldStartType) || !Enum.IsDefined(e.NewStartType))
            {
                Log.Warn($"Entrée du journal des services rejetée : {e.ServiceName}");
                continue;
            }
            _entries.Add(e with { ServiceName = name });
        }
    }

    /// <summary>Nom de catalogue correspondant, y compris pour un ancien nom à suffixe de session.</summary>
    private string? Normalize(string name)
    {
        if (_known.TryGetValue(name, out var exact)) return exact;

        int underscore = name.LastIndexOf('_');
        if (underscore > 0 && _known.TryGetValue(name[..underscore], out var template)) return template;

        return null;
    }

    public IReadOnlyList<ServiceChangeEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    /// <summary>Journalise un changement à venir. Lève si le journal ne peut pas être écrit.</summary>
    public ServiceChangeEntry Record(string serviceName, ServiceStartMode oldMode, ServiceStartMode newMode)
    {
        if (LoadError != null) throw new InvalidOperationException(LoadError);

        var name = Normalize(serviceName)
            ?? throw new ArgumentException($"Service hors catalogue : {serviceName}");

        var entry = new ServiceChangeEntry(name, oldMode, newMode, DateTime.Now);
        lock (_gate) _entries.Add(entry);

        try { Persist(); }
        catch
        {
            lock (_gate) _entries.Remove(entry);
            throw;
        }
        return entry;
    }

    /// <summary>Retire une entrée dont le changement n'a finalement pas pu être appliqué.</summary>
    public void Discard(ServiceChangeEntry entry)
    {
        lock (_gate) _entries.Remove(entry);
        TryPersist();
    }

    /// <summary>
    /// Type de démarrage d'origine d'un service : celui du tout premier changement
    /// enregistré, et non du dernier — sinon un aller-retour ferait perdre l'état initial.
    /// </summary>
    public ServiceStartMode? OriginalStartType(string serviceName)
    {
        var name = Normalize(serviceName);
        if (name == null) return null;

        lock (_gate)
            return _entries.FirstOrDefault(e => e.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase))
                          ?.OldStartType;
    }

    /// <summary>Services que AETHER a modifiés et qui peuvent donc être restaurés.</summary>
    public IReadOnlyList<string> ChangedServices()
    {
        lock (_gate)
            return _entries.Select(e => e.ServiceName)
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .ToList();
    }

    /// <summary>Oublie l'historique d'un service, une fois celui-ci réellement restauré.</summary>
    public void Forget(string serviceName)
    {
        var name = Normalize(serviceName);
        if (name == null) return;

        lock (_gate)
            _entries.RemoveAll(e => e.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase));
        TryPersist();
    }

    private void Persist()
    {
        List<ServiceChangeEntry> snapshot;
        lock (_gate) snapshot = _entries.ToList();
        SafeFile.WriteJson(_path, snapshot);
    }

    private void TryPersist()
    {
        if (LoadError != null) return;
        try { Persist(); }
        catch (Exception ex) { Log.Warn("Mise à jour du journal des services impossible.", ex); }
    }
}
