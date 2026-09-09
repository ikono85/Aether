using System.IO;
using System.Text.Json;

namespace Aether.Services.Optimization;

/// <summary>
/// Journal de restauration persistant. Avant chaque modification système, l'état
/// précédent est écrit ici (%AppData%\Aether\restore.json). Tant qu'une entrée
/// existe, l'action correspondante peut être annulée — y compris après un
/// redémarrage de l'application.
/// </summary>
public class RestoreStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _entries;
    private readonly object _gate = new();

    public RestoreStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aether");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "restore.json");

        _entries = Load(_path);
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                       ?? new Dictionary<string, string>();
        }
        catch { /* journal illisible : on repart d'un journal vide */ }
        return new Dictionary<string, string>();
    }

    /// <summary>Vrai si le module a été appliqué et peut donc être annulé.</summary>
    public bool HasBackup(string moduleId)
    {
        lock (_gate) return _entries.Keys.Any(k => k.StartsWith(moduleId + "/", StringComparison.Ordinal));
    }

    public IReadOnlyList<string> KeysFor(string moduleId)
    {
        lock (_gate)
            return _entries.Keys.Where(k => k.StartsWith(moduleId + "/", StringComparison.Ordinal)).ToList();
    }

    public void Write<T>(string key, T value)
    {
        lock (_gate) _entries[key] = JsonSerializer.Serialize(value);
        Persist();
    }

    public T? Read<T>(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var raw)) return default;
            try { return JsonSerializer.Deserialize<T>(raw); } catch { return default; }
        }
    }

    public bool Contains(string key)
    {
        lock (_gate) return _entries.ContainsKey(key);
    }

    public void Remove(string key)
    {
        lock (_gate) _entries.Remove(key);
        Persist();
    }

    /// <summary>Efface toutes les entrées d'un module (après une annulation réussie).</summary>
    public void Clear(string moduleId)
    {
        lock (_gate)
        {
            foreach (var k in _entries.Keys.Where(k => k.StartsWith(moduleId + "/", StringComparison.Ordinal)).ToList())
                _entries.Remove(k);
        }
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
