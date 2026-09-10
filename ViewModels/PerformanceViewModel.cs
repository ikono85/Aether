using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Aether.Models;
using Aether.Services;

namespace Aether.ViewModels;

public partial class PerformanceViewModel : ObservableObject
{
    private readonly HardwareService _hw;
    private readonly Queue<double> _cpu = new();
    private readonly Queue<double> _gpu = new();
    private const int Cap = 60;
    private const double W = 620, H = 150;

    public HardwareModule Cpu => _hw.Cpu;
    public HardwareModule Gpu => _hw.Gpu;
    public HardwareModule Ram => _hw.Ram;

    /// <summary>Capteurs LibreHardwareMonitor, groupés par composant.</summary>
    public ICollectionView Temperatures { get; }
    public ICollectionView Fans { get; }
    public ICollectionView Powers { get; }

    [ObservableProperty] private PointCollection _cpuPoints = new();
    [ObservableProperty] private PointCollection _gpuPoints = new();

    public PerformanceViewModel(HardwareService hw)
    {
        _hw = hw;
        Temperatures = GroupByComponent(hw.Temperatures);
        Fans = GroupByComponent(hw.Fans);
        Powers = GroupByComponent(hw.Powers);
        for (int i = 0; i < Cap; i++) { _cpu.Enqueue(0); _gpu.Enqueue(0); }
        hw.Updated += Refresh;
    }

    private void Refresh()
    {
        Push(_cpu, _hw.Cpu.Usage);
        Push(_gpu, _hw.Gpu.Usage);
        CpuPoints = Build(_cpu);
        GpuPoints = Build(_gpu);
    }

    private static ICollectionView GroupByComponent(IEnumerable<HardwareSensor> sensors)
    {
        var view = CollectionViewSource.GetDefaultView(sensors);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HardwareSensor.Component)));
        return view;
    }

    /// <summary>Une mesure indisponible est tracée à zéro : un NaN casserait la polyligne.</summary>
    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(double.IsNaN(v) ? 0 : v);
        if (q.Count > Cap) q.Dequeue();
    }

    private static PointCollection Build(Queue<double> q)
    {
        var pts = new PointCollection();
        var arr = q.ToArray();
        for (int i = 0; i < arr.Length; i++)
        {
            double x = i / (double)(Cap - 1) * W;
            double y = H - arr[i] / 100.0 * H;
            pts.Add(new Point(x, y));
        }
        return pts;
    }
}
