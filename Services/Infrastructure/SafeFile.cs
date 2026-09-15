using System.IO;
using System.Text;
using System.Text.Json;

namespace Aether.Services.Infrastructure;

public enum LoadStatus { Missing, Loaded, RecoveredFromBackup, Corrupt }

/// <summary>
/// Lecture et écriture de fichiers JSON sans risque de perte.
///
/// Écriture : fichier temporaire vidé sur disque, puis <see cref="File.Replace(string,string,string?,bool)"/>
/// qui conserve la version précédente en « .bak ». Un crash ou une coupure de courant laisse
/// donc toujours soit l'ancien fichier complet, soit le nouveau — jamais un fichier tronqué.
///
/// Lecture : fichier principal, sinon « .bak ». Un fichier illisible n'est jamais écrasé en
/// silence : une copie « .corrupt-horodatage » est conservée et l'appelant est prévenu.
/// </summary>
public static class SafeFile
{
    public static void WriteAllText(string path, string content)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            writer.Write(content);
            writer.Flush();
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }

    public static void WriteJson<T>(string path, T value) =>
        WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    public static (T? Value, LoadStatus Status) ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return (null, LoadStatus.Missing);

        if (TryRead<T>(path, out var value)) return (value, LoadStatus.Loaded);

        Quarantine(path);

        if (TryRead<T>(path + ".bak", out var backup))
        {
            Log.Warn($"{Path.GetFileName(path)} illisible : restauré depuis sa copie .bak.");
            return (backup, LoadStatus.RecoveredFromBackup);
        }

        Log.Error($"{path} et sa copie .bak sont illisibles.");
        return (null, LoadStatus.Corrupt);
    }

    /// <summary>Supprime le fichier et sa copie de secours (sinon la lecture la ressusciterait).</summary>
    public static void Delete(string path)
    {
        foreach (var p in new[] { path, path + ".bak", path + ".tmp" })
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch (Exception ex) { Log.Warn($"Suppression impossible : {p}", ex); }
        }
    }

    private static bool TryRead<T>(string path, out T? value) where T : class
    {
        value = null;
        try
        {
            if (!File.Exists(path)) return false;
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            return value != null;
        }
        catch { return false; }
    }

    private static void Quarantine(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Copy(path, $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
        }
        catch { }
    }
}
