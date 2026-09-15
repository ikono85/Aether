using System.IO;
using System.Text;
using System.Windows.Threading;
using Aether.Services.Infrastructure;

namespace Aether.Services.History;

/// <summary>
/// Enregistre chaque relevé réseau (toutes les 5 s) pour garder 24 h d'historique : coupures,
/// latence, perte. Les relevés sont ajoutés par lots d'une minute dans
/// %LocalAppData%\Aether\history\network-AAAAMMJJ.csv (7 jours conservés) et relus au
/// démarrage : un redémarrage d'AETHER ne fait pas perdre l'historique de la journée.
/// </summary>
public sealed class MeasurementHistory : IDisposable
{
    private const int KeepDays = 7;

    private readonly NetworkService _net;
    private readonly DispatcherTimer _flushTimer;
    private readonly List<NetworkSample> _pending = new();
    private readonly object _fileGate = new();
    private bool _disposed;

    public NetworkTimeline Network { get; } = new();

    /// <summary>Levé une fois par minute (sur le thread d'interface) après l'écriture des relevés.</summary>
    public event Action? Updated;

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aether", "history");

    public MeasurementHistory(NetworkService net)
    {
        _net = net;
        LoadRecent();

        _net.Updated += OnNetworkUpdated;

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _flushTimer.Tick += (_, _) =>
        {
            Flush(background: true);
            Updated?.Invoke();
        };
        _flushTimer.Start();
    }

    private void OnNetworkUpdated()
    {
        // Une redécouverte lève aussi Updated sans nouveau relevé : pas de doublon.
        if (_net.IsDiscovering) return;
        var now = DateTime.Now;
        if (Network.Samples.Count > 0 && now - Network.Samples[^1].Time < TimeSpan.FromSeconds(3)) return;

        var references = new[] { _net.Gateway, _net.Cdn, _net.Cloud }.Where(n => n.EverAnswered).ToList();
        bool outage = references.Count > 0 && references.All(n => !n.Reachable) && !_net.InGracePeriod;

        var sample = new NetworkSample(now,
            _net.PingMs < 0 ? double.NaN : _net.PingMs,
            _net.PacketLoss < 0 ? double.NaN : _net.PacketLoss,
            _net.Gateway.IsMeasurable && _net.Gateway.Rtt >= 0 ? _net.Gateway.Rtt : double.NaN,
            _net.DownloadMbps, _net.UploadMbps, outage);

        Network.Add(sample);
        _pending.Add(sample);
    }

    private void Flush(bool background)
    {
        if (_pending.Count == 0) return;
        var batch = _pending.ToList();
        _pending.Clear();

        void Write()
        {
            try
            {
                lock (_fileGate)
                {
                    Directory.CreateDirectory(Folder);
                    foreach (var day in batch.GroupBy(s => s.Time.Date))
                    {
                        var path = FileFor(day.Key);
                        var text = new StringBuilder();
                        if (!File.Exists(path)) text.AppendLine(NetworkTimeline.CsvHeader);
                        foreach (var s in day) text.AppendLine(NetworkTimeline.ToCsvLine(s));
                        File.AppendAllText(path, text.ToString(), Encoding.UTF8);
                    }
                }
            }
            catch (Exception ex) { Log.Warn("Écriture de l'historique réseau impossible.", ex); }
        }

        if (background) _ = Task.Run(Write);
        else Write();
    }

    private static string FileFor(DateTime day) => Path.Combine(Folder, $"network-{day:yyyyMMdd}.csv");

    private void LoadRecent()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;

            var cutoff = DateTime.Now - NetworkTimeline.Window;
            foreach (var day in new[] { DateTime.Today.AddDays(-1), DateTime.Today })
            {
                var path = FileFor(day);
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path).Skip(1))
                    if (NetworkTimeline.TryParseCsvLine(line, out var s) && s.Time >= cutoff)
                        Network.Add(s);
            }

            foreach (var file in Directory.EnumerateFiles(Folder, "network-*.csv"))
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-KeepDays))
                    File.Delete(file);
        }
        catch (Exception ex) { Log.Warn("Lecture de l'historique réseau impossible.", ex); }
    }

    /// <summary>Exporte les 24 dernières heures en CSV.</summary>
    public void ExportCsv(string path)
    {
        var text = new StringBuilder().AppendLine(NetworkTimeline.CsvHeader);
        foreach (var s in Network.Samples) text.AppendLine(NetworkTimeline.ToCsvLine(s));
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Stop();
        _net.Updated -= OnNetworkUpdated;
        Flush(background: false);
    }
}
