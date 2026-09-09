using System.IO;
using System.ServiceProcess;
using System.Text.Json;

namespace Aether.Services.WindowsServices;

/// <summary>Une modification de type de démarrage, conservée pour permettre un retour en arrière.</summary>
public record ServiceChangeEntry(
    string ServiceName,
    ServiceStartMode OldStartType,
    ServiceStartMode NewStartType,
    DateTime Timestamp);

/// <summary>
/// Journal des changements appliqués aux services, persisté à côté du journal
/// d'optimisation (%AppData%\Aether\service-changes.json). Permet de restaurer un
/// service dans l'état exact où AETHER l'a trouvé, même après redémarrage.
/// </summary>
public class ServiceChangeLog
{
    private readonly string _path;
    private readonly List<ServiceChangeEntry> _entries;
    private readonly object _gate = new();

    public ServiceChangeLog()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aether");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "service-changes.json");

        _entries = Load(_path);
    }

    private static List<ServiceChangeEntry> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<ServiceChangeEntry>>(File.ReadAllText(path))
                       ?? new List<ServiceChangeEntry>();
        }
        catch { /* journal illisible : on repart d'un journal vide */ }
        return new List<ServiceChangeEntry>();
    }

    public IReadOnlyList<ServiceChangeEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public void Record(string serviceName, ServiceStartMode oldMode, ServiceStartMode newMode)
    {
        lock (_gate) _entries.Add(new ServiceChangeEntry(serviceName, oldMode, newMode, DateTime.Now));
        Persist();
    }

    /// <summary>
    /// Type de démarrage d'origine d'un service : celui du tout premier changement
    /// enregistré, et non du dernier — sinon un aller-retour ferait perdre l'état initial.
    /// </summary>
    public ServiceStartMode? OriginalStartType(string serviceName)
    {
        lock (_gate)
        {
            var first = _entries.FirstOrDefault(e =>
                e.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
            return first?.OldStartType;
        }
    }

    /// <summary>Services que AETHER a modifiés et qui peuvent donc être restaurés.</summary>
    public IReadOnlyList<string> ChangedServices()
    {
        lock (_gate)
            return _entries.Select(e => e.ServiceName)
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .ToList();
    }

    /// <summary>Oublie l'historique d'un service, une fois celui-ci restauré.</summary>
    public void Forget(string serviceName)
    {
        lock (_gate)
            _entries.RemoveAll(e => e.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
        Persist();
    }

    private void Persist()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch { /* disque plein / droits : la session courante garde l'état en mémoire */ }
    }
}
