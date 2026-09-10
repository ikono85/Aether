using CommunityToolkit.Mvvm.ComponentModel;

namespace Aether.Models;

/// <summary>Un capteur individuel remonté par LibreHardwareMonitor (température, ventilateur, puissance).</summary>
public partial class HardwareSensor : ObservableObject
{
    /// <summary>Composant porteur (ex. « AMD Ryzen 7 5800X », « ASUS B550 · Nuvoton NCT6798D »).</summary>
    public string Component { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Unité affichée : « °C », « tr/min » ou « W ».</summary>
    public string Unit { get; init; } = "";

    // NaN = capteur muet ou valeur hors plage physique (entrée non branchée).
    [ObservableProperty] private double _value = double.NaN;

    public string ValueText => double.IsNaN(Value)
        ? "—"
        : Unit == "tr/min" ? $"{Value:0} {Unit}" : $"{Value:0.0} {Unit}";

    /// <summary>
    /// 0 optimal, 1 attention, 2 critique. Seules les températures déclenchent une alerte,
    /// avec les mêmes seuils que <see cref="HardwareModule"/>.
    /// </summary>
    public int Health => Unit != "°C" || double.IsNaN(Value) ? 0 : Value > 85 ? 2 : Value > 72 ? 1 : 0;

    partial void OnValueChanged(double value)
    {
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(Health));
    }
}
