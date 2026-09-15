using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using Aether.Models;
using Aether.Services.Infrastructure;

namespace Aether.Services;

/// <summary>
/// Mesure RÉELLE de la connexion : gateway/DNS du système, ping + packet loss vers
/// chaque maillon (dont ISP et un peer découverts par traceroute), et débit réseau
/// actif mesuré sur l'interface active. Tout est best-effort et non bloquant.
///
/// La topologie est redécouverte automatiquement quand Windows signale un changement
/// d'adresse (Wi-Fi, VPN, câble) ou une sortie de veille ; pendant la période de
/// stabilisation qui suit, <see cref="InGracePeriod"/> permet aux alertes de se taire.
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

    /// <summary>GUID de l'interface active : clé de sa configuration TCP/IP dans le registre.</summary>
    public string InterfaceId { get; private set; } = "";

    /// <summary>Vrai si IPv6 est actif sur l'interface : ses DNS IPv6 doivent aussi être gérés.</summary>
    public bool InterfaceSupportsIPv6 { get; private set; }

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

    /// <summary>
    /// Vrai pendant une redécouverte et pendant la stabilisation qui suit un changement de
    /// réseau ou une sortie de veille : les coupures observées alors ne sont pas des pannes.
    /// </summary>
    public bool InGracePeriod => IsDiscovering || DateTime.UtcNow < _graceUntil;

    public event Action? Updated;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _rediscoverDebounce;
    private NetworkInterface? _iface;
    private long _lastRx, _lastTx;
    private DateTime _lastSample;
    private bool _busy;
    private bool _started;
    private DateTime _graceUntil = DateTime.MinValue;

    public NetworkService()
    {
        Nodes = new[] { Gateway, Dns, Isp, Peer, Cdn, Cloud };
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Cibles fixes fiables pour CDN / Cloud (anycast public).
        SetNode(Cdn, "1.1.1.1");     // Cloudflare
        SetNode(Cloud, "8.8.8.8");   // Google

        // Un relevé complet dure ~2,5 s (sondes espacées) : 5 s laisse une marge.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) =>
        {
            try { await RefreshAsync(); }
            catch (Exception ex) { Log.Error("Relevé réseau interrompu.", ex); }
        };

        // Un changement d'adresse arrive souvent en rafale (IPv4, IPv6, DHCP) : on attend
        // que la configuration se stabilise avant de relancer une découverte complète.
        _rediscoverDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _rediscoverDebounce.Tick += async (_, _) =>
        {
            _rediscoverDebounce.Stop();
            try { await RediscoverAsync(); }
            catch (Exception ex) { Log.Error("Redécouverte réseau automatique impossible.", ex); }
        };
    }

    public async void Start()
    {
        if (_started) return;
        _started = true;

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // async void (lancé depuis un constructeur) : sans ce try, la moindre exception de
        // Discover() fermerait l'application.
        try
        {
            Discover();
            await DiscoverHopsAsync();
            await RefreshAsync(force: true);
        }
        catch (Exception ex) { Log.Error("Découverte réseau initiale impossible.", ex); }
        finally { _timer.Start(); }
    }

    public void Stop()
    {
        _timer.Stop();
        _rediscoverDebounce.Stop();
        if (!_started) return;
        _started = false;
        // Événements statiques : sans désabonnement, ils retiendraient ce service.
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() => ScheduleRediscover("changement d'adresse réseau", graceSeconds: 45));

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            _dispatcher.BeginInvoke(() => ScheduleRediscover("sortie de veille", graceSeconds: 60));
    }

    private void ScheduleRediscover(string reason, int graceSeconds)
    {
        if (!_started) return;

        var until = DateTime.UtcNow.AddSeconds(graceSeconds);
        if (until > _graceUntil) _graceUntil = until;

        Log.Info($"Redécouverte réseau programmée ({reason}).");
        _rediscoverDebounce.Stop();
        _rediscoverDebounce.Start();
    }

    // ------------------------------------------------------------------ Découverte

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    /// <summary>
    /// Trouve l'interface qui porte réellement la route vers Internet, sa gateway et ses DNS.
    /// Si aucune n'est active, l'état précédent est effacé au lieu de rester affiché.
    /// </summary>
    private void Discover()
    {
        var ni = FindActiveInterface(out var gateway);

        if (ni == null)
        {
            if (_iface != null) Log.Info("Plus aucune interface réseau active.");
            _iface = null;
            InterfaceName = InterfaceId = "";
            InterfaceSupportsIPv6 = false;
            LinkSpeedMbps = DownloadMbps = UploadMbps = double.NaN;
            foreach (var n in new[] { Gateway, Dns, Isp, Peer }) n.Reset();
            Gateway.Host = Dns.Host = "—";
            return;
        }

        if (_iface?.Id != ni.Id || Gateway.Address != gateway)
        {
            Log.Info($"Interface active : {ni.Name} (passerelle {gateway}).");
            // Nouveau réseau : l'historique, la gigue et « a déjà répondu » ne valent plus rien.
            foreach (var n in new[] { Gateway, Dns, Isp, Peer }) n.Reset();
        }

        _iface = ni;
        InterfaceName = ni.Name;
        InterfaceId = ni.Id;
        LinkSpeedMbps = ni.Speed > 0 ? ni.Speed / 1_000_000.0 : double.NaN;

        try { InterfaceSupportsIPv6 = ni.Supports(NetworkInterfaceComponent.IPv6); }
        catch { InterfaceSupportsIPv6 = false; }

        SetNode(Gateway, gateway);

        try
        {
            var dns = ni.GetIPProperties().DnsAddresses
                        .FirstOrDefault(d => d.AddressFamily == AddressFamily.InterNetwork);
            SetNode(Dns, dns?.ToString() ?? "");
        }
        catch { SetNode(Dns, ""); }

        try
        {
            var s = ni.GetIPv4Statistics();
            _lastRx = s.BytesReceived; _lastTx = s.BytesSent; _lastSample = DateTime.UtcNow;
        }
        catch (Exception ex) { Log.Warn($"Compteurs de l'interface {ni.Name} illisibles.", ex); }
    }

    private static NetworkInterface? FindActiveInterface(out string gateway)
    {
        gateway = "";

        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception ex)
        {
            Log.Warn("Énumération des interfaces réseau impossible.", ex);
            return null;
        }

        // Interface choisie par la table de routage pour joindre Internet : c'est elle qui
        // porte le trafic, et non la première carte venue (VPN, Hyper-V, carte secondaire).
        int? bestIndex = null;
        try
        {
            uint destination = BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes(), 0);
            if (GetBestInterface(destination, out var index) == 0) bestIndex = (int)index;
        }
        catch { /* repli sur l'énumération */ }

        var candidates = all
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderByDescending(n => bestIndex != null && Ipv4Index(n) == bestIndex);

        foreach (var ni in candidates)
        {
            try
            {
                var gw = ni.GetIPProperties().GatewayAddresses.FirstOrDefault(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                if (gw == null) continue;

                gateway = gw.Address.ToString();
                return ni;
            }
            catch { /* interface disparue entre-temps */ }
        }
        return null;
    }

    private static int? Ipv4Index(NetworkInterface ni)
    {
        try { return ni.GetIPProperties().GetIPv4Properties()?.Index; }
        catch { return null; }
    }

    /// <summary>Change l'adresse sondée d'un maillon ; son historique repart à zéro s'il change de cible.</summary>
    private static void SetNode(NetworkNode node, string address)
    {
        if (node.Address == address && address.Length > 0) return;
        node.Reset();
        node.Address = address;
        node.Host = address.Length > 0 ? address : "—";
    }

    /// <summary>
    /// Chemin complet vers l'Internet public, un maillon par saut. Alimenté par le
    /// traceroute : c'est la même sonde qui sert à désigner l'ISP et le peer, mais tous
    /// les sauts intermédiaires sont conservés.
    /// </summary>
    public ObservableCollection<NetworkNode> Hops { get; } = new();

    /// <summary>Vrai pendant une découverte : la vue désactive le bouton de relance.</summary>
    public bool IsDiscovering { get; private set; }

    /// <summary>Nombre maximal de sauts sondés. Au-delà, on est déjà dans le cœur de réseau.</summary>
    private const int MaxHops = 15;

    /// <summary>
    /// Relance une découverte complète : interface active, gateway, DNS et traceroute.
    /// Déclenchée automatiquement sur changement de réseau, ou à la demande.
    /// </summary>
    public async Task RediscoverAsync()
    {
        if (IsDiscovering) return;
        IsDiscovering = true;
        Updated?.Invoke();
        try
        {
            // Un relevé en cours pingerait encore les anciennes adresses : on le laisse finir.
            for (int i = 0; i < 100 && _busy; i++) await Task.Delay(100);

            Discover();
            await DiscoverHopsAsync();
            await RefreshAsync(force: true);
        }
        finally
        {
            IsDiscovering = false;
            Updated?.Invoke();
        }
    }

    /// <summary>Traceroute (TTL croissant) : chemin complet, dont le routeur ISP et un peer.</summary>
    private async Task DiscoverHopsAsync()
    {
        var hops = new List<string>();
        var discovered = new List<NetworkNode>();

        if (_iface != null)
        {
            try
            {
                using var ping = new Ping();
                var buffer = new byte[32];
                for (int ttl = 1; ttl <= MaxHops; ttl++)
                {
                    var reply = await ping.SendPingAsync("8.8.8.8", 1000, buffer, new PingOptions(ttl, true));

                    var node = new NetworkNode { Label = $"SAUT {ttl}" };
                    if (reply.Address != null && !reply.Address.Equals(IPAddress.Any))
                    {
                        var ip = reply.Address.ToString();
                        hops.Add(ip);
                        node.Address = node.Host = ip;
                        node.Unresolved = false;
                        // TimedOut avec une adresse = le routeur a bien renvoyé un TTL expiré :
                        // c'est une réponse valide pour un traceroute, pas une perte.
                        node.Reachable = true;
                        node.EverAnswered = true;
                        node.Rtt = reply.RoundtripTime;
                        node.RecordSample();
                    }
                    else
                    {
                        // Saut muet : il ne renvoie pas de TTL expiré. Fréquent et sans gravité.
                        node.Host = "* * *";
                        node.Unresolved = true;
                    }

                    discovered.Add(node);
                    if (reply.Status == IPStatus.Success) break;
                }
            }
            catch (Exception ex) { Log.Info($"Traceroute incomplet : {ex.Message}"); }
        }

        Hops.Clear();
        foreach (var h in discovered) Hops.Add(h);

        // hop 1 = gateway, hop 2 ≈ ISP, un hop du milieu ≈ peer
        var external = hops.Where(h => h != Gateway.Address).Distinct().ToList();
        SetNode(Isp, external.ElementAtOrDefault(0) ?? "");
        SetNode(Peer, external.ElementAtOrDefault(Math.Min(2, Math.Max(0, external.Count - 1))) ?? "");

        // Noms inverses en parallèle, avec délai : ils ne servent qu'à l'affichage (l'adresse
        // sondée reste l'IP, beaucoup de noms de routeurs ne se résolvent pas dans l'autre sens).
        var toResolve = new List<NetworkNode> { Isp, Peer };
        toResolve.AddRange(Hops.Where(h => !h.Unresolved));
        await Task.WhenAll(toResolve.Select(TryResolveName));
    }

    private static async Task TryResolveName(NetworkNode n)
    {
        if (!IPAddress.TryParse(n.Address, out var ip)) return;

        var lookup = System.Net.Dns.GetHostEntryAsync(ip);
        _ = lookup.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        if (await Task.WhenAny(lookup, Task.Delay(3000)) != lookup || lookup.IsFaulted) return;

        var name = lookup.Result.HostName;
        if (!string.IsNullOrWhiteSpace(name) && n.Address == ip.ToString()) n.Host = name;
    }

    private async Task RefreshAsync(bool force = false)
    {
        if (_busy || (IsDiscovering && !force)) return;
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
        var target = node.Address;
        if (!IPAddress.TryParse(target, out _))
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

        // L'adresse a pu changer pendant la mesure (redécouverte) : le résultat est périmé.
        if (node.Address != target) return;

        if (ok > 0) node.EverAnswered = true;

        node.Reachable = ok > 0;
        node.Loss = node.EverAnswered ? Math.Round((tries - ok) / (double)tries * 100) : 0;
        node.Rtt = ok > 0 ? Math.Round(sum / ok) : -1;
        node.RecordSample();
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
        catch { /* interface disparue : la prochaine redécouverte la remplacera */ }
    }
}
