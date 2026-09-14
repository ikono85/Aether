using System.IO;
using System.Text.Json;

namespace Aether.Services;

/// <summary>
/// Réglages persistés dans %AppData%\Aether\settings.json, à côté de restore.json et
/// service-changes.json. Volontairement un POCO : la sauvegarde est un simple dump JSON,
/// et une clé absente du fichier garde sa valeur par défaut ci-dessous (les anciens
/// fichiers restent donc lisibles après l'ajout d'un réglage).
/// </summary>
public class AppSettings
{
    // --- Général ---
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }

    // --- Capteurs ---
    /// <summary>Période d'échantillonnage des capteurs, en millisecondes.</summary>
    public int SampleIntervalMs { get; set; } = 1000;
    /// <summary>Suspend les sondes quand la fenêtre est réduite (rend du CPU au système).</summary>
    public bool PauseWhenMinimized { get; set; } = true;

    // --- Apparence ---
    public bool VisualEffects { get; set; } = true;
    /// <summary>Accent coloré suivant l'état système ; sinon accent vert figé.</summary>
    public bool DynamicAccent { get; set; } = true;
    /// <summary>Échelle de l'interface en pourcent (90 à 130).</summary>
    public int UiScale { get; set; } = 100;

    // --- Seuils ---
    /// <summary>Température (°C) à partir de laquelle le score de santé se dégrade.</summary>
    public int ThermalComfortC { get; set; } = 60;

    // --- Sécurité ---
    /// <summary>Demande une confirmation avant toute optimisation irréversible sans point de restauration.</summary>
    public bool ConfirmRiskyActions { get; set; } = true;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aether");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* fichier corrompu : on repart des valeurs par défaut */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* disque en lecture seule : le réglage reste actif pour la session */ }
    }

    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    public static void OpenDataFolder()
    {
        Directory.CreateDirectory(Dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Dir) { UseShellExecute = true });
    }
}
