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

    // --- Zone de notification et alertes ---
    /// <summary>Le bouton fermer masque la fenêtre dans la zone de notification au lieu de quitter.</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Interrupteur général, aussi accessible depuis le menu de l'icône.</summary>
    public bool AlertsEnabled { get; set; } = true;
    public bool AlertTemperature { get; set; } = true;
    /// <summary>Seuil d'alerte CPU/GPU (°C). Distinct du seuil de confort, qui ne sert qu'au score.</summary>
    public int AlertTempC { get; set; } = 85;
    public bool AlertConnection { get; set; } = true;
    public bool AlertNetwork { get; set; } = true;

    // --- Sécurité ---
    /// <summary>Demande une confirmation avant toute optimisation irréversible sans point de restauration.</summary>
    public bool ConfirmRiskyActions { get; set; } = true;
    /// <summary>Point de restauration Windows avant la première modification durable de la session.</summary>
    public bool CreateRestorePoint { get; set; } = true;

    // --- Mises à jour ---
    /// <summary>Désactivé par défaut : interroge GitHub, donc proposé explicitement à l'accueil.</summary>
    public bool CheckForUpdates { get; set; }
    public DateTime LastUpdateCheckUtc { get; set; }

    // --- Accueil ---
    public bool OnboardingCompleted { get; set; }

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aether");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()).Normalized();
        }
        catch { /* fichier corrompu : on repart des valeurs par défaut */ }
        return new AppSettings();
    }

    /// <summary>
    /// Ramène chaque valeur numérique dans la plage de son curseur. Un fichier modifié à la main
    /// (seuil d'alerte à 0 °C, échelle à 5 %) ne doit pas rendre l'application inutilisable.
    /// </summary>
    public AppSettings Normalized()
    {
        SampleIntervalMs = Math.Clamp(SampleIntervalMs, 250, 5000);
        UiScale = Math.Clamp(UiScale, 80, 150);
        ThermalComfortC = Math.Clamp(ThermalComfortC, 45, 85);
        AlertTempC = Math.Clamp(AlertTempC, 60, 100);
        return this;
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
