using System.Text.Json;
using Aether.Services.Infrastructure;

namespace Aether.Services.Optimization;

/// <summary>
/// Journal de restauration persistant. Avant chaque modification système, l'état
/// précédent est écrit ici (%ProgramData%\Aether\restore.json, accès administrateurs). Tant
/// qu'une entrée existe, l'action correspondante peut être annulée — y compris après un
/// redémarrage de l'application.
///
/// Garanties : écriture atomique avec copie .bak ; une sauvegarde qui ne peut pas être écrite
/// sur disque fait échouer <see cref="Write{T}"/>, donc la modification n'a pas lieu ; un
/// journal illisible passe le magasin en lecture seule plutôt que d'être écrasé par un vide.
/// </summary>
public class RestoreStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _entries;
    private readonly object _gate = new();

    /// <summary>Renseigné si le journal existant est illisible : toute nouvelle écriture est refusée.</summary>
    public string? LoadError { get; }

    public bool IsReadOnly => LoadError != null;

    public RestoreStore() : this(AppPaths.SecureFile("restore.json")) { }

    /// <summary>Emplacement explicite (tests).</summary>
    internal RestoreStore(string path)
    {
        _path = path;

        var (entries, status) = SafeFile.ReadJson<Dictionary<string, string>>(_path);
        _entries = entries ?? new Dictionary<string, string>();

        if (status == LoadStatus.Corrupt)
        {
            LoadError = "Journal de restauration illisible (une copie « .corrupt » a été conservée). " +
                        "Les optimisations sont bloquées pour ne pas écraser les sauvegardes existantes.";
        }
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

    /// <summary>Enregistre une sauvegarde. Lève si elle ne peut pas être persistée sur disque.</summary>
    public void Write<T>(string key, T value)
    {
        if (LoadError != null) throw new InvalidOperationException(LoadError);

        string? previous;
        lock (_gate)
        {
            _entries.TryGetValue(key, out previous);
            _entries[key] = JsonSerializer.Serialize(value);
        }

        try { Persist(); }
        catch
        {
            lock (_gate)
            {
                if (previous is null) _entries.Remove(key);
                else _entries[key] = previous;
            }
            throw;
        }
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
        lock (_gate)
        {
            if (!_entries.Remove(key)) return;
        }
        TryPersist();
    }

    /// <summary>Efface toutes les entrées d'un module (après une annulation entièrement réussie).</summary>
    public void Clear(string moduleId)
    {
        lock (_gate)
        {
            foreach (var k in _entries.Keys.Where(k => k.StartsWith(moduleId + "/", StringComparison.Ordinal)).ToList())
                _entries.Remove(k);
        }
        TryPersist();
    }

    private void Persist()
    {
        Dictionary<string, string> snapshot;
        lock (_gate) snapshot = new Dictionary<string, string>(_entries);
        SafeFile.WriteJson(_path, snapshot);
    }

    /// <summary>
    /// Une suppression non persistée est sans danger : l'entrée réapparaîtra au prochain lancement
    /// et l'annulation, idempotente, restaurera la même valeur d'origine.
    /// </summary>
    private void TryPersist()
    {
        if (LoadError != null) return;
        try { Persist(); }
        catch (Exception ex) { Log.Warn("Mise à jour du journal de restauration impossible.", ex); }
    }
}
