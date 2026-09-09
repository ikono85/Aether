using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aether.Models;
using Aether.Services;

namespace Aether.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public HardwareService Hw { get; }
    public NetworkService NetSvc { get; }

    public DashboardViewModel Dashboard { get; }
    public NetworkViewModel Network { get; }
    public SecurityViewModel Security { get; }
    public OptimizationViewModel Optimization { get; }
    public WindowsServicesOptimizationViewModel WindowsServices { get; }
    public PerformanceViewModel Performance { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private ObservableObject _current;
    [ObservableProperty] private string _activePage = "Dashboard";
    [ObservableProperty] private SystemState _state = SystemState.Optimal;
    [ObservableProperty] private double _healthScore = 94;

    public MainViewModel()
    {
        // NetSvc en premier : il est la source unique des mesures réseau, que HardwareService
        // recopie pour le Dashboard au lieu de les mesurer une seconde fois.
        NetSvc = new NetworkService();
        Hw = new HardwareService(NetSvc);
        Dashboard = new DashboardViewModel(Hw);
        Network = new NetworkViewModel(NetSvc);
        Security = new SecurityViewModel(this);
        Optimization = new OptimizationViewModel();
        WindowsServices = new WindowsServicesOptimizationViewModel();
        Performance = new PerformanceViewModel(Hw);
        Settings = new SettingsViewModel();

        _current = Dashboard;
        Hw.Updated += OnTelemetry;
        Hw.Start();
        Recompute();
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        ActivePage = page;
        Current = page switch
        {
            "Network" => Network,
            "Security" => Security,
            "Optimization" => Optimization,
            "WindowsServices" => WindowsServices,
            "Performance" => Performance,
            "Settings" => Settings,
            _ => Dashboard
        };
    }

    private void OnTelemetry() => Recompute();

    /// <summary>
    /// Arrête les sondes et décharge le pilote noyau de LibreHardwareMonitor.
    /// Appelé à la fermeture de la fenêtre : sans cela, le pilote resterait chargé.
    /// </summary>
    public void Shutdown()
    {
        Hw.Updated -= OnTelemetry;
        Hw.Dispose();
        NetSvc.Stop();
    }

    /// <summary>Calcule le score de santé global et l'état, puis diffuse la couleur d'accent.</summary>
    private void Recompute()
    {
        // Analyse IA prioritaire (scan en cours)
        if (Security.IsScanning) { SetState(SystemState.Analyzing); return; }

        // Les capteurs absents sont écartés du calcul : leur poids est redistribué sur les
        // mesures réellement disponibles, plutôt que compté comme un score nul.
        var hottest = Best(Hw.Cpu.Temperature, Hw.Gpu.Temperature);
        double? thermal = hottest is null ? null : 100 - Math.Max(0, hottest.Value - 60) * 2.2;

        double? load = WeightedLoad();
        double security = Security.SecurityScore;

        var parts = new List<(double Value, double Weight)> { (security, 0.30) };
        if (thermal is not null) parts.Add((thermal.Value, 0.35));
        if (load is not null) parts.Add((load.Value, 0.35));

        double totalWeight = parts.Sum(p => p.Weight);
        double score = Math.Clamp(parts.Sum(p => p.Value * p.Weight) / totalWeight, 0, 100);

        // Lissage conservé en interne en virgule flottante : arrondir ici bloquerait le
        // score dès que l'écart avec la cible devient inférieur à un point.
        _healthRaw += (score - _healthRaw) * 0.25;
        HealthScore = Math.Round(_healthRaw);

        SystemState s = HealthScore switch
        {
            < 55 => SystemState.Critical,
            < 78 => SystemState.Elevated,
            _ => SystemState.Optimal
        };
        SetState(s);
    }

    /// <summary>Score lissé non arrondi (voir <see cref="Recompute"/>).</summary>
    private double _healthRaw = 94;

    /// <summary>Plus grande valeur mesurée, null si aucun capteur ne répond.</summary>
    private static double? Best(params double[] values)
    {
        var known = values.Where(v => !double.IsNaN(v)).ToList();
        return known.Count == 0 ? null : known.Max();
    }

    /// <summary>
    /// Charge globale pondérée CPU/GPU/RAM, calculée sur les seules mesures disponibles.
    /// </summary>
    private double? WeightedLoad()
    {
        var parts = new List<(double Value, double Weight)>();
        if (!double.IsNaN(Hw.Cpu.Usage)) parts.Add((Hw.Cpu.Usage, 0.4));
        if (!double.IsNaN(Hw.Gpu.Usage)) parts.Add((Hw.Gpu.Usage, 0.3));
        if (!double.IsNaN(Hw.Ram.Usage)) parts.Add((Hw.Ram.Usage, 0.3));

        if (parts.Count == 0) return null;

        double weighted = parts.Sum(p => p.Value * p.Weight) / parts.Sum(p => p.Weight);
        return 100 - weighted * 0.5;
    }

    private void SetState(SystemState s)
    {
        if (State != s) State = s;
        // Diffuse l'accent dynamique à toutes les ressources qui pointent "AccentBrush".
        var key = s switch
        {
            SystemState.Critical => "AccentDangerColor",
            SystemState.Elevated => "AccentWarnColor",
            SystemState.Analyzing => "AccentAiColor",
            _ => "AccentOptimalColor"
        };
        if (Application.Current?.Resources["AccentBrush"] is SolidColorBrush b &&
            Application.Current.Resources[key] is Color col && b.Color != col)
        {
            // pinceau figé -> on remplace la ressource
            Application.Current.Resources["AccentBrush"] = new SolidColorBrush(col);
        }
    }

    public string StateLabel => State switch
    {
        SystemState.Critical => "PROBLÈME DÉTECTÉ",
        SystemState.Elevated => "CHARGE ÉLEVÉE",
        SystemState.Analyzing => "ANALYSE IA",
        _ => "SYSTÈME OPTIMAL"
    };

    partial void OnStateChanged(SystemState value) => OnPropertyChanged(nameof(StateLabel));
}
