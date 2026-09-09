using System.Net;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using Aether.Models;

namespace Aether.Services;

/// <summary>
/// Mesure RÉELLE de la connexion : gateway/DNS du système, ping + packet loss vers
/// chaque maillon (dont ISP et un peer découverts par traceroute), et débit réseau
/// actif mesuré sur l'interface active. Tout est best-effort et non bloquant.
/// </summary>
public class NetworkService
{
    public NetworkNode Gateway { get; } = new() { Label = "GATEWAY" };
    public NetworkNode Dns { get; } = new() { Label = "DNS" };
    public NetworkNode Isp { get; } = new() { Label = "ISP" };
    public NetworkNode Peer { get; } = new() { Label = "PEER" };
    public NetworkNode Cdn { get; } = new() { Label = "CDN" };
    public NetworkNode Cloud { get; } = new() { Label = "CLOUD" };

    public IReadOnlyList<NetworkNode> Nodes { get; }

    public double PingMs { get; private set; } = -1;

    // NaN tant qu'aucun relevé n'a eu lieu : on n'affiche pas « 0 Mb/s » avant de savoir.
    public double DownloadMbps { get; private set; } = double.NaN;
    public double UploadMbps { get; private set; } = double.NaN;
    /// <summary>Perte de paquets en %, -1 tant qu'aucun maillon mesurable n'a répondu.</summary>
    public double PacketLoss { get; private set; } = -1;

    /// <summary>Nombre de maillons réellement exploitables pour la mesure de perte.</summary>
    public int MeasurableNodes => Nodes.Count(n => n.IsMeasurable);

    /// <summary>Nom de l'interface réellement utilisée, vide tant qu'elle n'est pas trouvée.</summary>
    public string InterfaceName { get; private set; } = "";

    /// <summary>Débit théorique du lien en Mb/s (NaN si l'interface ne le déclare pas).</summary>
    public double LinkSpeedMbps { get; private set; } = double.NaN;

    /// <summary>
    /// Taux d'occupation du lien en %, dérivé du débit mesuré et de la vitesse négociée.
    /// NaN si l'un des deux est inconnu — jamais estimé.
    /// </summary>
    public double UtilizationPercent
    {
        get
        {
            if (double.IsNaN(LinkSpeedMbps) || LinkSpeedMbps <= 0) return double.NaN;
            if (double.IsNaN(DownloadMbps) && double.IsNaN(UploadMbps)) return double.NaN;

            double busiest = Math.Max(double.IsNaN(DownloadMbps) ? 0 : DownloadMbps,
                                      double.IsNaN(UploadMbps) ? 0 : UploadMbps);
            return Math.Clamp(busiest / LinkSpeedMbps * 100, 0, 100);
        }
    }

    public event Action? Updated;

    private readonly DispatcherTimer _timer;
    private NetworkInterface? _iface;
    private long _lastRx, _lastTx;
    private DateTime _lastSample;
    private bool _busy;

    public NetworkService()
    {
        Nodes = new[] { Gateway, Dns, Isp, Peer, Cdn, Cloud };
        // Cibles fixes fiables pour CDN / Cloud (anycast public).
        Cdn.Host = "1.1.1.1";     // Cloudflare
        Cloud.Host = "8.8.8.8";   // Google
        // Un relevé complet dure ~2,5 s (sondes espacées) : 5 s laisse une marge.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public async void Start()
    {
        Discover();
        await DiscoverHopsAsync();
        await RefreshAsync();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>Trouve l'interface active, sa gateway et ses DNS (valeurs système réelles).</summary>
    private void Discover()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var p = ni.GetIPProperties();
            var gw = p.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (gw == null) continue;

            _iface = ni;
            InterfaceName = ni.Name;
            LinkSpeedMbps = ni.Speed > 0 ? ni.Speed / 1_000_000.0 : double.NaN;
            Gateway.Host = gw.Address.ToString();
            var dns = p.DnsAddresses.FirstOrDefault(d => d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            Dns.Host = dns?.ToString() ?? "—";

            var s = ni.GetIPv4Statistics();
            _lastRx = s.BytesReceived; _lastTx = s.BytesSent; _lastSample = DateTime.UtcNow;
            break;
        }
    }

    /// <summary>Traceroute (TTL croissant) pour découvrir le vrai routeur ISP et un peer.</summary>
    private async Task DiscoverHopsAsync()
    {
        var hops = new List<string>();
        try
        {
            using var ping = new Ping();
            var buffer = new byte[32];
            for (int ttl = 1; ttl <= 8; ttl++)
            {
                var reply = await ping.SendPingAsync("8.8.8.8", 1000, buffer, new PingOptions(ttl, true));
                if (reply.Address != null && !reply.Address.Equals(IPAddress.Any))
                    hops.Add(reply.Address.ToString());
                if (reply.Status == IPStatus.Success) break;
            }
        }
        catch { /* ICMP peut être filtré : best-effort */ }

        // hop 1 = gateway, hop 2 ≈ ISP, un hop du milieu ≈ peer
        var external = hops.Where(h => h != Gateway.Host).Distinct().ToList();
        Isp.Host = external.ElementAtOrDefault(0) ?? "isp";
        Peer.Host = external.ElementAtOrDefault(Math.Min(2, Math.Max(0, external.Count - 1))) ?? "peer";

        // Reverse DNS best-effort pour révéler le FAI (ex: *.orange.fr)
        await TryResolveName(Isp);
        await TryResolveName(Peer);
    }

    private static async Task TryResolveName(NetworkNode n)
    {
        if (!IPAddress.TryParse(n.Host, out var ip)) return;
        try
        {
            var entry = await System.Net.Dns.GetHostEntryAsync(ip);
            if (!string.IsNullOrWhiteSpace(entry.HostName)) n.Host = entry.HostName;
        }
        catch { /* pas de PTR : on garde l'IP */ }
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            // Les maillons sont sondés par petits groupes : lancer les 6 en parallèle
            // saturait la pile ICMP locale et faisait expirer des pings parfaitement
            // valides — la perte affichée mesurait alors la sonde, pas le réseau.
            await MeasureAll();

            var reachable = Nodes.Where(n => n.Reachable && n.Rtt >= 0).ToList();
            PingMs = reachable.Count > 0 ? reachable.Min(n => n.Rtt) : -1;

            // La perte n'est moyennée que sur les maillons qui répondent à l'ICMP.
            // Un hop non découvert ou filtré par le FAI ne compte pas comme 100 % de perte :
            // sinon une connexion parfaitement saine afficherait 30 à 40 % en permanence.
            var measurable = Nodes.Where(n => n.IsMeasurable).ToList();
            PacketLoss = measurable.Count > 0 ? Math.Round(measurable.Average(n => n.Loss), 1) : -1;
            SampleThroughput();
            Updated?.Invoke();
        }
        finally { _busy = false; }
    }

    /// <summary>Nombre de sondes simultanées : au-delà, les timeouts sont des faux positifs.</summary>
    private const int MaxParallelProbes = 2;

    private async Task MeasureAll()
    {
        using var gate = new SemaphoreSlim(MaxParallelProbes);

        await Task.WhenAll(Nodes.Select(async node =>
        {
            await gate.WaitAsync();
            try { await MeasureNode(node); }
            finally { gate.Release(); }
        }));
    }

    /// <summary>Envoie 3 pings espacés à un maillon et calcule RTT moyen + packet loss.</summary>
    private static async Task MeasureNode(NetworkNode node)
    {
        var target = node.Host;
        if (string.IsNullOrWhiteSpace(target) || target is "…" or "—" or "isp" or "peer")
        {
            // Hôte inconnu : aucune mesure possible, et surtout aucune perte à imputer.
            node.Unresolved = true;
            node.Reachable = false;
            node.Loss = 0;
            node.Rtt = -1;
            return;
        }
        node.Unresolved = false;

        const int tries = 3;
        const int timeoutMs = 2000;      // large : un ICMP dépriorisé n'est pas une perte
        const int spacingMs = 250;       // évite les rafales, qui se perdent elles-mêmes

        int ok = 0; double sum = 0;
        using var ping = new Ping();     // une seule instance réutilisée pour les 3 envois

        for (int i = 0; i < tries; i++)
        {
            if (i > 0) await Task.Delay(spacingMs);
            try
            {
                var r = await ping.SendPingAsync(target, timeoutMs);
                if (r.Status == IPStatus.Success) { ok++; sum += r.RoundtripTime; }
            }
            catch { /* échec = perte */ }
        }
        if (ok > 0) node.EverAnswered = true;

        node.Reachable = ok > 0;
        node.Loss = node.EverAnswered ? Math.Round((tries - ok) / (double)tries * 100) : 0;
        node.Rtt = ok > 0 ? Math.Round(sum / ok) : -1;
    }

    /// <summary>Débit réseau ACTIF réel (delta d'octets sur l'interface, converti en Mb/s).</summary>
    private void SampleThroughput()
    {
        if (_iface == null) return;
        try
        {
            var s = _iface.GetIPv4Statistics();
            var now = DateTime.UtcNow;
            var dt = (now - _lastSample).TotalSeconds;
            if (dt > 0.1)
            {
                DownloadMbps = Math.Round(Math.Max(0, s.BytesReceived - _lastRx) * 8.0 / dt / 1_000_000, 1);
                UploadMbps = Math.Round(Math.Max(0, s.BytesSent - _lastTx) * 8.0 / dt / 1_000_000, 1);
            }
            _lastRx = s.BytesReceived; _lastTx = s.BytesSent; _lastSample = now;
        }
        catch { /* interface disparue */ }
    }
}
