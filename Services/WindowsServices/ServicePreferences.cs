using System.IO;
using System.Text.Json;

namespace Aether.Services.WindowsServices;

/// <summary>Préférences du module « Services Windows », persistées entre deux sessions.</summary>
public class ServicePreferences
{
    private readonly string _path;

    /// <summary>Ne plus demander confirmation pour les services classés « sans risque ».</summary>
    public bool SkipConfirmationForSafe { get; set; }

    private ServicePreferences(string path) => _path = path;

    public static ServicePreferences Load()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aether");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "service-preferences.json");

        var prefs = new ServicePreferences(path);
        try
        {
            if (File.Exists(path))
            {
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
                if (stored != null) prefs.SkipConfirmationForSafe = stored.SkipConfirmationForSafe;
            }
        }
        catch { /* préférences illisibles : valeurs par défaut */ }

        return prefs;
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(new Stored(SkipConfirmationForSafe),
                                                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch { /* non bloquant */ }
    }

    private record Stored(bool SkipConfirmationForSafe);
}
