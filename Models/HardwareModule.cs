using CommunityToolkit.Mvvm.ComponentModel;

namespace Aether.Models;

/// <summary>Un composant surveillé (CPU, GPU, RAM, SSD, NETWORK) affiché comme module holographique.</summary>
public partial class HardwareModule : ObservableObject
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";

    // NaN = aucun capteur ne fournit cette mesure sur ce PC. Jamais remplacé par une
    // valeur inventée : l'interface affiche « — » plutôt qu'un chiffre faux.
    [ObservableProperty] private double _temperature = double.NaN;
    [ObservableProperty] private double _usage = double.NaN;        // 0..100
    [ObservableProperty] private double _performance = double.NaN;   // 0..100
    [ObservableProperty] private string _detail = "";

    public bool HasTemperature => !double.IsNaN(Temperature);
    public bool HasUsage => !double.IsNaN(Usage);

    /// <summary>Valeur affichée : le chiffre, ou « — » si le capteur est absent.</summary>
    public string TemperatureText => HasTemperature ? $"{Temperature:0}" : "—";
    public string UsageText => HasUsage ? $"{Usage:0}% actif" : "mesure indisponible";

    /// <summary>Valeurs pour les jauges : une mesure absente affiche une barre vide.</summary>
    public double TemperatureBar => HasTemperature ? Temperature : 0;
    public double UsageBar => HasUsage ? Usage : 0;

    /// <summary>
    /// État dérivé pour la couleur (0 optimal, 1 attention, 2 critique).
    /// Une mesure absente ne déclenche aucune alerte.
    /// </summary>
    public int Health
    {
        get
        {
            if (HasUsage && Usage > 92) return 2;
            if (HasTemperature && Temperature > 85) return 2;
            if (HasUsage && Usage > 75) return 1;
            if (HasTemperature && Temperature > 72) return 1;
            return 0;
        }
    }

    /// <summary>État en toutes lettres : la couleur seule ne suffit pas (daltonisme, lecteur d'écran).</summary>
    public string HealthLabel => Health switch { 2 => "CRITIQUE", 1 => "ÉLEVÉ", _ => "NORMAL" };

    partial void OnTemperatureChanged(double value)
    {
        OnPropertyChanged(nameof(Health));
        OnPropertyChanged(nameof(HealthLabel));
        OnPropertyChanged(nameof(HasTemperature));
        OnPropertyChanged(nameof(TemperatureText));
        OnPropertyChanged(nameof(TemperatureBar));
    }

    partial void OnUsageChanged(double value)
    {
        OnPropertyChanged(nameof(Health));
        OnPropertyChanged(nameof(HealthLabel));
        OnPropertyChanged(nameof(HasUsage));
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(UsageBar));
    }
}
