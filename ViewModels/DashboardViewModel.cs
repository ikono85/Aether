using CommunityToolkit.Mvvm.ComponentModel;
using Aether.Models;
using Aether.Services;

namespace Aether.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly HardwareService _hw;
    public HardwareModule Cpu => _hw.Cpu;
    public HardwareModule Gpu => _hw.Gpu;
    public HardwareModule Ram => _hw.Ram;
    public HardwareModule Ssd => _hw.Ssd;
    public HardwareModule Net => _hw.Net;

    [ObservableProperty] private double _temperatureScore = 90;
    [ObservableProperty] private double _performanceScore = 100;
    [ObservableProperty] private double _securityScore = 82;

    public DashboardViewModel(HardwareService hw)
    {
        _hw = hw;
        hw.Updated += () =>
        {
            // Un capteur absent ne doit ni fausser le score ni le transformer en NaN :
            // on ne moyenne que ce qui est réellement mesuré.
            var hottest = Max(Cpu.Temperature, Gpu.Temperature);
            if (!double.IsNaN(hottest))
                TemperatureScore = System.Math.Clamp(100 - (hottest - 45) * 1.5, 0, 100);

            var perf = Average(Cpu.Performance, Gpu.Performance);
            if (!double.IsNaN(perf)) PerformanceScore = System.Math.Round(perf);
        };
    }

    /// <summary>Plus grande valeur disponible, NaN si aucune ne l'est.</summary>
    private static double Max(params double[] values)
    {
        var known = values.Where(v => !double.IsNaN(v)).ToList();
        return known.Count == 0 ? double.NaN : known.Max();
    }

    /// <summary>Moyenne des seules valeurs disponibles, NaN si aucune ne l'est.</summary>
    private static double Average(params double[] values)
    {
        var known = values.Where(v => !double.IsNaN(v)).ToList();
        return known.Count == 0 ? double.NaN : known.Average();
    }
}
