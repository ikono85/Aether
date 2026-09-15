using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using Aether.Models;
using Aether.Services.Infrastructure;
using LibreHardwareMonitor.Hardware;

namespace Aether.Services;

/// <summary>
/// Télémétrie matérielle RÉELLE via LibreHardwareMonitor. Aucune valeur n'est simulée :
/// une mesure qu'aucun capteur ne fournit reste à <see cref="double.NaN"/> et l'interface
/// affiche « — ».
///
/// La lecture des capteurs (SMART, Super I/O, SPD…) peut prendre plusieurs dizaines de
/// millisecondes : elle s'exécute sur un thread de fond, et seule la recopie des valeurs dans
/// les objets liés à l'interface passe par le Dispatcher. Le chargement du pilote noyau
/// (plusieurs secondes parfois) est lui aussi fait en fond, au premier cycle.
///
/// Si LibreHardwareMonitor ne démarre pas (pilote refusé, antivirus), la boucle continue
/// quand même : les mesures réseau du Dashboard et le score de santé restent vivants.
/// </summary>
public class HardwareService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly object _lhmGate = new();
    private readonly UpdateVisitor _visitor = new();
    private Computer? _computer;
    private bool _initialized;
    private volatile bool _disposed;
    private HardwareDescription? _description;
    private bool _descriptionApplied;
    private CancellationTokenSource? _loopCts;
    private long _intervalTicks = TimeSpan.FromSeconds(1).Ticks;

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

    /// <summary>Capteurs détectés, composant par composant (carte mère incluse).</summary>
    public ObservableCollection<HardwareSensor> Temperatures { get; } = new();
    public ObservableCollection<HardwareSensor> Fans { get; } = new();
    public ObservableCollection<HardwareSensor> Powers { get; } = new();
    private readonly Dictionary<string, HardwareSensor> _sensorById = new();

    /// <summary>Débits réseau lus sur le matériel (Mb/s), NaN si indisponibles.</summary>
    public double DownloadMbps { get; private set; } = double.NaN;
    public double UploadMbps { get; private set; } = double.NaN;

    /// <summary>Faux si la couche capteurs n'a pas pu démarrer du tout.</summary>
    public bool SensorsAvailable { get; private set; }

    /// <summary>Diagnostic affichable : ce qui est mesuré, ce qui ne l'est pas et pourquoi.</summary>
    public string StatusMessage { get; private set; } = "Initialisation des capteurs…";

    /// <summary>Levé sur le thread d'interface après chaque cycle.</summary>
    public event Action? Updated;

    public HardwareService(NetworkService? network = null)
    {
        _network = network;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Modules = new[] { Cpu, Gpu, Ram, Ssd, Net };
    }

    public void Start() => StartLoop();

    public void Stop()
    {
        var cts = _loopCts;
        _loopCts = null;
        cts?.Cancel();
    }

    /// <summary>Vrai entre <see cref="Start"/> et <see cref="Stop"/> : la boucle échantillonne.</summary>
    public bool IsRunning => _loopCts != null;

    /// <summary>Période d'échantillonnage, réglable à chaud (250 ms minimum).</summary>
    public TimeSpan Interval
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _intervalTicks));
        set
        {
            var v = value < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : value;
            Interlocked.Exchange(ref _intervalTicks, v.Ticks);
        }
    }

    /// <summary>Reprend l'échantillonnage après une pause.</summary>
    public void Resume() => StartLoop();

    private void StartLoop()
    {
        if (_loopCts != null || _disposed) return;
        var cts = new CancellationTokenSource();
        _loopCts = cts;
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                var snapshot = ReadSnapshot();
                await _dispatcher.InvokeAsync(() =>
                {
                    if (!ct.IsCancellationRequested && !_disposed) Apply(snapshot);
                }, DispatcherPriority.Background, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Error("Cycle de télémétrie matérielle interrompu.", ex); }

            var wait = Interval - Stopwatch.GetElapsedTime(started);
            if (wait < TimeSpan.FromMilliseconds(50)) wait = TimeSpan.FromMilliseconds(50);
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ------------------------------------------------------------------ Thread de fond

    private sealed record HardwareDescription(bool Available, string Cpu, string Gpu, bool HasRam,
                                              List<string> Disks, string Status);

    /// <summary>Charge LibreHardwareMonitor au premier cycle. Appelé sous <see cref="_lhmGate"/>.</summary>
    private void EnsureInitialized()
    {
        if (_initialized || _disposed) return;
        _initialized = true;

        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true
            };
            computer.Open();
            _computer = computer;
            _description = Describe(computer);
            Log.Info(_description.Status);
        }
        catch (Exception ex)
        {
            // Pilote noyau refusé, antivirus, plateforme non prise en charge…
            _computer = null;
            _description = new HardwareDescription(false, "", "", false, new List<string>(),
                $"Capteurs matériels indisponibles : {ex.Message}");
            Log.Warn("LibreHardwareMonitor n'a pas pu démarrer.", ex);
        }
    }

    private HardwareDescription Describe(Computer computer)
    {
        string cpu = "", gpu = "";
        bool ram = false;
        var disks = new List<string>();
        var found = new List<string>();

        foreach (var hw in computer.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu: cpu = hw.Name; found.Add("CPU"); break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel: gpu = hw.Name; found.Add("GPU"); break;
                case HardwareType.Memory: ram = true; found.Add("RAM"); break;
                case HardwareType.Storage: disks.Add(hw.Name); found.Add("Stockage"); break;
                case HardwareType.Motherboard: found.Add("Carte mère"); break;
            }
        }

        if (_network != null) found.Add("Réseau");

        var status = found.Count == 0
            ? "Aucun capteur matériel détecté sur ce PC."
            : $"Capteurs actifs : {string.Join(", ", found.Distinct())}.";

        return new HardwareDescription(true, cpu, gpu, ram, disks, status);
    }

    private sealed class Snapshot
    {
        public double CpuTemp = double.NaN, CpuLoad = double.NaN;
        public double GpuTemp = double.NaN, GpuLoad = double.NaN;
        public double RamLoad = double.NaN, RamUsedGb = double.NaN, RamTotalGb = double.NaN, RamTemp = double.NaN;
        public double SsdTemp = double.NaN, SsdLoad = double.NaN;
        public List<SensorReading> Sensors { get; } = new();
        public string? Error;
    }

    private enum SensorKind { Temperature, Fan, Power }

    private readonly record struct SensorReading(string Id, SensorKind Kind, string Component,
                                                 string Name, string Unit, double Value);

    /// <summary>Un cycle de lecture. Toute mesure absente reste à NaN, jamais devinée.</summary>
    private Snapshot ReadSnapshot()
    {
        lock (_lhmGate)
        {
            EnsureInitialized();

            var s = new Snapshot();
            if (_computer == null) return s;

            try
            {
                foreach (var hw in _computer.Hardware)
                {
                    hw.Accept(_visitor);
                    CollectSensors(hw, s.Sensors);

                    switch (hw.HardwareType)
                    {
                        case HardwareType.Cpu:
                            s.CpuTemp = Prefer(s.CpuTemp,
                                PickTemperature(hw, "Core (Tctl/Tdie)", "CPU Package", "Core Max", "Core Average"));
                            s.CpuLoad = Prefer(s.CpuLoad, Pick(hw, SensorType.Load, "CPU Total"));
                            break;

                        case HardwareType.GpuNvidia:
                        case HardwareType.GpuAmd:
                        case HardwareType.GpuIntel:
                            s.GpuTemp = Prefer(s.GpuTemp, PickTemperature(hw, "GPU Core", "GPU Hot Spot", "GPU Package"));
                            s.GpuLoad = Prefer(s.GpuLoad, Pick(hw, SensorType.Load, "GPU Core", "D3D 3D"));
                            break;

                        case HardwareType.Memory:
                            // LibreHardwareMonitor expose plusieurs composants mémoire :
                            // « Total Memory » (la RAM physique), « Virtual Memory » (le fichier
                            // d'échange, à ignorer) et une entrée par barrette pour la température.
                            bool physical = hw.Name.Contains("Total", StringComparison.OrdinalIgnoreCase);
                            bool virtualMem = hw.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

                            if (physical || !virtualMem)
                            {
                                s.RamLoad = Prefer(s.RamLoad, Pick(hw, SensorType.Load, "Memory"));
                                s.RamUsedGb = Prefer(s.RamUsedGb, Pick(hw, SensorType.Data, "Memory Used"));
                                s.RamTotalGb = Prefer(s.RamTotalGb,
                                    Sum(hw, SensorType.Data, "Memory Used", "Memory Available"));
                            }

                            // Barrettes : on retient la plus chaude.
                            s.RamTemp = Hotter(s.RamTemp, PickTemperature(hw));
                            break;

                        case HardwareType.Storage:
                            // Plusieurs disques possibles : on retient le plus chaud et le plus sollicité.
                            s.SsdTemp = Hotter(s.SsdTemp, PickTemperature(hw, "Temperature"));
                            s.SsdLoad = Hotter(s.SsdLoad, Pick(hw, SensorType.Load, "Total Activity", "Used Space"));
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                s.Error = $"Lecture des capteurs interrompue : {ex.Message}";
            }

            return s;
        }
    }

    /// <summary>
    /// Lit les capteurs de température, de ventilateur et de puissance du composant et de
    /// ses sous-composants (la puce Super I/O de la carte mère est un sous-composant).
    /// </summary>
    private static void CollectSensors(IHardware hw, List<SensorReading> readings)
    {
        string component = hw.Parent is null ? hw.Name : $"{hw.Parent.Name} · {hw.Name}";

        foreach (var sensor in hw.Sensors)
        {
            // Plages physiques : les entrées non branchées renvoient -55, 127, 65535…
            (SensorKind kind, string unit, float min, float max)? spec = sensor.SensorType switch
            {
                SensorType.Temperature => (SensorKind.Temperature, "°C", 0f, 125f),
                SensorType.Fan => (SensorKind.Fan, "tr/min", -1f, 10000f),
                SensorType.Power => (SensorKind.Power, "W", -1f, 2000f),
                _ => null
            };
            if (spec is not { } s) continue;

            // « Distance to TjMax » est un écart avant la limite thermique, pas une température.
            if (sensor.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase)) continue;

            double value = sensor.Value is float v && v > s.min && v < s.max ? v : double.NaN;
            readings.Add(new SensorReading(sensor.Identifier.ToString(), s.kind, component, sensor.Name, s.unit, value));
        }

        foreach (var sub in hw.SubHardware) CollectSensors(sub, readings);
    }

    // ------------------------------------------------------------------ Thread d'interface

    private void ApplyDescription(HardwareDescription d)
    {
        SensorsAvailable = d.Available;
        if (d.Cpu.Length > 0) Cpu.Detail = d.Cpu;
        if (d.Gpu.Length > 0) Gpu.Detail = d.Gpu;
        if (d.HasRam) Ram.Detail = "Mémoire système";

        Ssd.Detail = d.Disks.Count switch
        {
            0 => "",
            1 => d.Disks[0],
            _ => $"{d.Disks.Count} disques · le plus chaud"
        };

        StatusMessage = d.Status;
    }

    private void Apply(Snapshot s)
    {
        if (!_descriptionApplied && _description is { } d)
        {
            ApplyDescription(d);
            _descriptionApplied = true;
        }

        foreach (var r in s.Sensors)
        {
            if (!_sensorById.TryGetValue(r.Id, out var entry))
            {
                // Un capteur n'apparaît qu'après une vraie mesure : les en-têtes de ventilateur
                // vides (0 tr/min permanent) et les sondes absentes ne polluent pas la liste.
                if (double.IsNaN(r.Value) || r.Value <= 0) continue;

                entry = new HardwareSensor { Component = r.Component, Name = r.Name, Unit = r.Unit };
                _sensorById[r.Id] = entry;
                (r.Kind switch
                {
                    SensorKind.Temperature => Temperatures,
                    SensorKind.Fan => Fans,
                    _ => Powers
                }).Add(entry);
            }

            entry.Value = r.Value;
        }

        Cpu.Temperature = s.CpuTemp;
        Cpu.Usage = s.CpuLoad;
        Cpu.Performance = Headroom(s.CpuTemp, ceiling: 95);

        Gpu.Temperature = s.GpuTemp;
        Gpu.Usage = s.GpuLoad;
        Gpu.Performance = Headroom(s.GpuTemp, ceiling: 90);

        Ram.Usage = s.RamLoad;
        Ram.Temperature = s.RamTemp;
        if (!double.IsNaN(s.RamUsedGb) && !double.IsNaN(s.RamTotalGb) && s.RamTotalGb > 0)
            Ram.Detail = $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0.0} Go";

        Ssd.Temperature = s.SsdTemp;
        Ssd.Usage = s.SsdLoad;

        if (s.Error != null) StatusMessage = s.Error;

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

        Net.Detail = _network.InterfaceName.Length == 0
            ? ""
            : double.IsNaN(_network.LinkSpeedMbps)
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
        _disposed = true;
        Stop();
        // Attend la fin d'une lecture en cours avant de décharger le pilote.
        lock (_lhmGate)
        {
            try { _computer?.Close(); } catch { /* pilote déjà déchargé */ }
            _computer = null;
        }
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
