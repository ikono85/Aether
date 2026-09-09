using System.Windows.Threading;
using Aether.Models;
using LibreHardwareMonitor.Hardware;

namespace Aether.Services;

/// <summary>
/// Télémétrie matérielle RÉELLE via LibreHardwareMonitor. Aucune valeur n'est simulée :
/// une mesure qu'aucun capteur ne fournit reste à <see cref="double.NaN"/> et l'interface
/// affiche « — ».
///
/// La lecture des températures CPU passe par un pilote noyau : sans droits administrateur,
/// LibreHardwareMonitor ne peut pas le charger et les températures processeur restent
/// indisponibles. <see cref="SensorsAvailable"/> et <see cref="StatusMessage"/> le disent.
/// </summary>
public class HardwareService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private Computer? _computer;
    private readonly UpdateVisitor _visitor = new();
    private readonly List<string> _disks = new();

    /// <summary>
    /// Source unique des mesures réseau. LibreHardwareMonitor sait aussi les lire, mais
    /// <see cref="NetworkService"/> mesure sur les compteurs de l'interface réellement
    /// active : garder deux sources pour la même grandeur donnerait deux chiffres différents
    /// entre le Dashboard et l'onglet Network.
    /// </summary>
    private readonly NetworkService? _network;

    public HardwareModule Cpu { get; } = new() { Name = "CPU", Icon = "" };
    public HardwareModule Gpu { get; } = new() { Name = "GPU", Icon = "" };
    public HardwareModule Ram { get; } = new() { Name = "RAM", Icon = "" };
    public HardwareModule Ssd { get; } = new() { Name = "SSD", Icon = "" };
    public HardwareModule Net { get; } = new() { Name = "NETWORK", Icon = "" };

    public IReadOnlyList<HardwareModule> Modules { get; }

    /// <summary>Débits réseau lus sur le matériel (Mb/s), NaN si indisponibles.</summary>
    public double DownloadMbps { get; private set; } = double.NaN;
    public double UploadMbps { get; private set; } = double.NaN;

    /// <summary>Faux si la couche capteurs n'a pas pu démarrer du tout.</summary>
    public bool SensorsAvailable { get; private set; }

    /// <summary>Diagnostic affichable : ce qui est mesuré, ce qui ne l'est pas et pourquoi.</summary>
    public string StatusMessage { get; private set; } = "Capteurs non initialisés.";

    public event Action? Updated;

    public HardwareService(NetworkService? network = null)
    {
        _network = network;
        Modules = new[] { Cpu, Gpu, Ram, Ssd, Net };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _timer.Tick += (_, _) => Sample();
    }

    public void Start()
    {
        if (!Initialize()) return;
        Sample();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private bool Initialize()
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = true
            };
            _computer.Open();
            SensorsAvailable = true;

            DescribeHardware();
            return true;
        }
        catch (Exception ex)
        {
            // Pilote noyau refusé, antivirus, plateforme non prise en charge…
            SensorsAvailable = false;
            StatusMessage = $"Capteurs matériels indisponibles : {ex.Message}";
            _computer = null;
            return false;
        }
    }

    /// <summary>Renseigne le libellé de chaque module avec le matériel réellement détecté.</summary>
    private void DescribeHardware()
    {
        if (_computer == null) return;

        var found = new List<string>();

        foreach (var hw in _computer.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    Cpu.Detail = hw.Name;
                    found.Add("CPU");
                    break;

                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    Gpu.Detail = hw.Name;
                    found.Add("GPU");
                    break;

                case HardwareType.Memory:
                    Ram.Detail = "Mémoire système";
                    found.Add("RAM");
                    break;

                case HardwareType.Storage:
                    _disks.Add(hw.Name);
                    found.Add("Stockage");
                    break;

            }
        }

        Ssd.Detail = _disks.Count switch
        {
            0 => "",
            1 => _disks[0],
            _ => $"{_disks.Count} disques · le plus chaud"
        };

        if (_network != null) found.Add("Réseau");

        StatusMessage = found.Count == 0
            ? "Aucun capteur matériel détecté sur ce PC."
            : $"Capteurs actifs : {string.Join(", ", found.Distinct())}.";
    }

    /// <summary>Un cycle de lecture. Toute mesure absente est remise à NaN, jamais devinée.</summary>
    private void Sample()
    {
        if (_computer == null) return;

        double cpuTemp = double.NaN, cpuLoad = double.NaN;
        double gpuTemp = double.NaN, gpuLoad = double.NaN;
        double ramLoad = double.NaN, ramUsedGb = double.NaN, ramTotalGb = double.NaN, ramTemp = double.NaN;
        double ssdTemp = double.NaN, ssdLoad = double.NaN;

        try
        {
            foreach (var hw in _computer.Hardware)
            {
                hw.Accept(_visitor);

                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        cpuTemp = Prefer(cpuTemp,
                            PickTemperature(hw, "Core (Tctl/Tdie)", "CPU Package", "Core Max", "Core Average"));
                        cpuLoad = Prefer(cpuLoad, Pick(hw, SensorType.Load, "CPU Total"));
                        break;

                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel:
                        gpuTemp = Prefer(gpuTemp, PickTemperature(hw, "GPU Core", "GPU Hot Spot", "GPU Package"));
                        gpuLoad = Prefer(gpuLoad, Pick(hw, SensorType.Load, "GPU Core", "D3D 3D"));
                        break;

                    case HardwareType.Memory:
                        // LibreHardwareMonitor expose plusieurs composants mémoire :
                        // « Total Memory » (la RAM physique), « Virtual Memory » (le fichier
                        // d'échange, à ignorer) et une entrée par barrette pour la température.
                        bool physical = hw.Name.Contains("Total", StringComparison.OrdinalIgnoreCase);
                        bool virtualMem = hw.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

                        if (physical || !virtualMem)
                        {
                            ramLoad = Prefer(ramLoad, Pick(hw, SensorType.Load, "Memory"));
                            ramUsedGb = Prefer(ramUsedGb, Pick(hw, SensorType.Data, "Memory Used"));
                            ramTotalGb = Prefer(ramTotalGb,
                                Sum(hw, SensorType.Data, "Memory Used", "Memory Available"));
                        }

                        // Barrettes : on retient la plus chaude.
                        ramTemp = Hotter(ramTemp, PickTemperature(hw));
                        break;

                    case HardwareType.Storage:
                        // Plusieurs disques possibles : on retient le plus chaud et le plus sollicité.
                        ssdTemp = Hotter(ssdTemp, PickTemperature(hw, "Temperature"));
                        ssdLoad = Hotter(ssdLoad, Pick(hw, SensorType.Load, "Total Activity", "Used Space"));
                        break;

                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lecture des capteurs interrompue : {ex.Message}";
        }

        Cpu.Temperature = cpuTemp;
        Cpu.Usage = cpuLoad;
        Cpu.Performance = Headroom(cpuTemp, ceiling: 95);

        Gpu.Temperature = gpuTemp;
        Gpu.Usage = gpuLoad;
        Gpu.Performance = Headroom(gpuTemp, ceiling: 90);

        Ram.Usage = ramLoad;
        Ram.Temperature = ramTemp;
        if (!double.IsNaN(ramUsedGb) && !double.IsNaN(ramTotalGb) && ramTotalGb > 0)
            Ram.Detail = $"{ramUsedGb:0.0} / {ramTotalGb:0.0} Go";

        Ssd.Temperature = ssdTemp;
        Ssd.Usage = ssdLoad;

        SampleNetwork();

        Updated?.Invoke();
    }

    /// <summary>Recopie les mesures réseau depuis leur source unique.</summary>
    private void SampleNetwork()
    {
        if (_network == null) return;

        Net.Usage = _network.UtilizationPercent;
        DownloadMbps = _network.DownloadMbps;
        UploadMbps = _network.UploadMbps;

        if (_network.InterfaceName.Length > 0)
            Net.Detail = double.IsNaN(_network.LinkSpeedMbps)
                ? _network.InterfaceName
                : $"{_network.InterfaceName} · {_network.LinkSpeedMbps:0} Mb/s";
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Première valeur trouvée parmi des noms de capteurs candidats, sinon NaN.</summary>
    private static double Pick(IHardware hw, SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            var sensor = hw.Sensors.FirstOrDefault(s =>
                s.SensorType == type && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (sensor?.Value is float v && !float.IsNaN(v)) return v;
        }
        return double.NaN;
    }

    /// <summary>
    /// Température par nom candidat ; à défaut, le capteur de température le plus chaud du
    /// composant — les noms varient selon le fabricant (AMD, Intel, NVIDIA).
    /// </summary>
    private static double PickTemperature(IHardware hw, params string[] names)
    {
        var byName = Pick(hw, SensorType.Temperature, names);
        if (!double.IsNaN(byName)) return byName;

        var values = hw.Sensors
            .Where(s => s.SensorType == SensorType.Temperature && s.Value is float f && !float.IsNaN(f))
            .Select(s => (double)s.Value!.Value)
            .ToList();

        return values.Count > 0 ? values.Max() : double.NaN;
    }

    private static double Sum(IHardware hw, SensorType type, params string[] names)
    {
        double total = double.NaN;
        foreach (var name in names)
        {
            var v = Pick(hw, type, name);
            if (double.IsNaN(v)) continue;
            total = double.IsNaN(total) ? v : total + v;
        }
        return total;
    }

    /// <summary>Garde la première mesure trouvée : un capteur absent n'écrase jamais une valeur.</summary>
    private static double Prefer(double existing, double candidate) =>
        double.IsNaN(existing) ? candidate : existing;

    /// <summary>Retient la plus grande des deux valeurs en ignorant les NaN.</summary>
    private static double Hotter(double a, double b) =>
        double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Max(a, b);

    /// <summary>
    /// Marge thermique restante en %, dérivée de la température réelle.
    /// Sans capteur de température, il n'y a rien à en déduire : NaN.
    /// </summary>
    private static double Headroom(double temperature, double ceiling)
    {
        if (double.IsNaN(temperature)) return double.NaN;
        return Math.Clamp((ceiling - temperature) / (ceiling - 30) * 100, 0, 100);
    }

    public void Dispose()
    {
        _timer.Stop();
        try { _computer?.Close(); } catch { /* pilote déjà déchargé */ }
        _computer = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>Force LibreHardwareMonitor à rafraîchir un composant et ses sous-composants.</summary>
    private class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
