using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aether.Models;
using Aether.Services;
using Aether.Services.History;
using Aether.Themes;

namespace Aether.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public HardwareService Hw { get; }
    public NetworkService NetSvc { get; }

    public DashboardViewModel Dashboard { get; }
    public PerformanceViewModel Performance { get; }
    public SettingsViewModel Settings { get; }

    // Onglets créés à la première visite : l'énumération des services Windows, la table TCP
    // ou le journal de restauration ne sont plus lus au démarrage si l'onglet n'est jamais ouvert.
    private readonly Lazy<NetworkViewModel> _network;
    private readonly Lazy<OptimizationViewModel> _optimization;
    private readonly Lazy<WindowsServicesOptimizationViewModel> _windowsServices;
    private readonly Lazy<HistoryViewModel> _historyPage;
    private readonly MeasurementHistory _measures;

    [ObservableProperty] private ObservableObject _current;
    [ObservableProperty] private string _activePage = "Dashboard";
    [ObservableProperty] private SystemState _state = SystemState.Analyzing;
    [ObservableProperty] private double _healthScore;

    public MainViewModel(HardwareService hw, NetworkService net,
                         DashboardViewModel dashboard, PerformanceViewModel performance, SettingsViewModel settings,
                         Lazy<NetworkViewModel> network, Lazy<OptimizationViewModel> optimization,
                         Lazy<WindowsServicesOptimizationViewModel> windowsServices,
                         Lazy<HistoryViewModel> history, MeasurementHistory measures)
    {
        _historyPage = history;
        // Résolu ici pour enregistrer les relevés dès le démarrage, onglet ouvert ou non.
        _measures = measures;
        Hw = hw;
        NetSvc = net;
        Dashboard = dashboard;
        Performance = performance;
        Settings = settings;
        _network = network;
        _optimization = optimization;
        _windowsServices = windowsServices;

        Settings.AccentPolicyChanged += OnAccentPolicyChanged;
        _current = Dashboard;

        Hw.Updated += OnTelemetry;
        // NetSvc est la source unique des mesures réseau (Dashboard, alertes, onglet Network).
        NetSvc.Start();
        Hw.Start();
        ApplyAccent(State);
    }

    [RelayCommand]
    private void Navigate(string page) => ActivePage = page;

    /// <summary>Clic sur la navigation ou raccourci clavier : les deux passent par la page active.</summary>
    partial void OnActivePageChanged(string value)
    {
        Current = value switch
        {
            "Network" => _network.Value,
            "Optimization" => _optimization.Value,
            "WindowsServices" => _windowsServices.Value,
            "History" => _historyPage.Value,
            "Performance" => Performance,
            "Settings" => Settings,
            _ => Dashboard
        };
    }

    private void OnTelemetry() => Recompute();

    /// <summary>
    /// Un réglage d'accent ou de seuil thermique a changé : le score est recalculé avec la
    /// nouvelle valeur, puis l'accent est réappliqué de force (l'état peut être identique
    /// alors que la couleur à afficher, elle, a changé).
    /// </summary>
    private void OnAccentPolicyChanged()
    {
        Recompute();
        ApplyAccent(State);
    }

    /// <summary>
    /// Arrête les sondes et décharge le pilote noyau de LibreHardwareMonitor.
    /// Appelé à la fermeture de la fenêtre : sans cela, le pilote resterait chargé.
    /// </summary>
    public void Shutdown()
    {
        Settings.AccentPolicyChanged -= OnAccentPolicyChanged;
        Hw.Updated -= OnTelemetry;
        Hw.Dispose();
        _measures.Dispose();   // écrit les derniers relevés
        NetSvc.Stop();
    }

    /// <summary>Score lissé non arrondi ; NaN tant qu'aucune mesure n'est arrivée.</summary>
    private double _healthRaw = double.NaN;

    /// <summary>Calcule le score de santé global et l'état, puis diffuse la couleur d'accent.</summary>
    private void Recompute()
    {
        // Les capteurs absents sont écartés du calcul : leur poids est redistribué sur les
        // mesures réellement disponibles, plutôt que compté comme un score nul.
        var hottest = Best(Hw.Cpu.Temperature, Hw.Gpu.Temperature);
        int comfort = Settings.ThermalComfortC;
        double? thermal = hottest is null ? null : 100 - Math.Max(0, hottest.Value - comfort) * 2.2;

        double? load = WeightedLoad();

        var parts = new List<(double Value, double Weight)>();
        if (thermal is not null) parts.Add((thermal.Value, 0.5));
        if (load is not null) parts.Add((load.Value, 0.5));

        if (parts.Count == 0)
        {
            // Aucune mesure : on n'affiche pas un score inventé.
            if (double.IsNaN(_healthRaw)) SetState(SystemState.Analyzing);
            return;
        }

        double score = Math.Clamp(parts.Sum(p => p.Value * p.Weight) / parts.Sum(p => p.Weight), 0, 100);

        // Première mesure prise telle quelle ; ensuite lissage en virgule flottante (arrondir
        // ici bloquerait le score dès que l'écart avec la cible devient inférieur à un point).
        _healthRaw = double.IsNaN(_healthRaw) ? score : _healthRaw + (score - _healthRaw) * 0.25;
        HealthScore = Math.Round(_healthRaw);

        SetState(HealthScore switch
        {
            < 55 => SystemState.Critical,
            < 78 => SystemState.Elevated,
            _ => SystemState.Optimal
        });
    }

    /// <summary>Plus grande valeur mesurée, null si aucun capteur ne répond.</summary>
    private static double? Best(params double[] values)
    {
        var known = values.Where(v => !double.IsNaN(v)).ToList();
        return known.Count == 0 ? null : known.Max();
    }

    /// <summary>Charge globale pondérée CPU/GPU/RAM, calculée sur les seules mesures disponibles.</summary>
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
        ApplyAccent(s);
    }

    /// <summary>
    /// Diffuse l'accent à toutes les ressources qui pointent « AccentBrush ». Si l'accent
    /// dynamique est désactivé dans les réglages, la teinte reste verte quel que soit l'état.
    /// </summary>
    private void ApplyAccent(SystemState s)
    {
        if (!Settings.DynamicAccent && s != SystemState.Analyzing) s = SystemState.Optimal;

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
            VisualEffects.SetCoreColor(col);
        }
    }

    public string StateLabel => State switch
    {
        SystemState.Critical => "PROBLÈME DÉTECTÉ",
        SystemState.Elevated => "CHARGE ÉLEVÉE",
        SystemState.Analyzing => "MESURE EN COURS",
        _ => "SYSTÈME OPTIMAL"
    };

    /// <summary>Phrase d'état du Dashboard, dérivée des mesures (et non un texte figé rassurant).</summary>
    public string StateDescription => State switch
    {
        SystemState.Critical => "Une température ou une charge dépasse nettement les seuils : consultez l'onglet Performance.",
        SystemState.Elevated => "Charge ou température élevée, sans situation critique.",
        SystemState.Analyzing => "Aucune mesure matérielle disponible pour l'instant.",
        _ => "Aucune mesure ne dépasse les seuils de santé."
    };

    /// <summary>« — » tant qu'aucune mesure n'a permis de calculer le score.</summary>
    public string HealthText => State == SystemState.Analyzing ? "—" : $"{HealthScore:0}";

    public string HealthAccessibleText => State == SystemState.Analyzing
        ? "Score de santé : mesure en cours"
        : $"Score de santé : {HealthScore:0} %, {StateLabel.ToLowerInvariant()}";

    partial void OnStateChanged(SystemState value)
    {
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(StateDescription));
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(HealthAccessibleText));
    }

    partial void OnHealthScoreChanged(double value)
    {
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(HealthAccessibleText));
    }
}
