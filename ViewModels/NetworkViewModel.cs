using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aether.Models;
using Aether.Services;

namespace Aether.ViewModels;

public partial class NetworkViewModel : ObservableObject
{
    private readonly NetworkService _net;
    private readonly ConnectionService _connections = new();
    private readonly SpeedTestService _speed = new();

    /// <summary>
    /// Rafraîchissement de la table TCP. Séparé du timer de <see cref="NetworkService"/> :
    /// la lecture est locale et instantanée, elle n'a pas à attendre le cycle des pings.
    /// </summary>
    private readonly DispatcherTimer _connTimer;

    public NetworkNode Gateway => _net.Gateway;
    public NetworkNode Dns => _net.Dns;
    public NetworkNode Isp => _net.Isp;
    public NetworkNode Peer => _net.Peer;
    public NetworkNode Cdn => _net.Cdn;
    public NetworkNode Cloud => _net.Cloud;

    /// <summary>Les six maillons de la cartographie, pour la liste des courbes de latence.</summary>
    public IReadOnlyList<NetworkNode> Nodes => _net.Nodes;

    /// <summary>Chemin complet découvert par traceroute.</summary>
    public System.Collections.ObjectModel.ObservableCollection<NetworkNode> Hops => _net.Hops;

    /// <summary>Vue filtrée de la table TCP (le filtre porte sur le processus et l'hôte distant).</summary>
    public ICollectionView ConnectionsView { get; }

    [ObservableProperty] private double _ping;
    [ObservableProperty] private double _download;
    [ObservableProperty] private double _upload;
    [ObservableProperty] private double _packetLoss;

    public NetworkViewModel(NetworkService net)
    {
        _net = net;
        net.Updated += Refresh;

        ConnectionsView = CollectionViewSource.GetDefaultView(_connections.Connections);
        ConnectionsView.Filter = MatchesFilter;
        ConnectionsView.SortDescriptions.Add(
            new SortDescription(nameof(ActiveConnection.ProcessName), ListSortDirection.Ascending));

        _connTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _connTimer.Tick += (_, _) => RefreshConnections();

        _speed.Progress += (pct, label) => Application.Current?.Dispatcher.Invoke(() =>
        {
            SpeedProgress = pct;
            SpeedStatus = label;
        });

        net.Start();
    }

    // ------------------------------------------------------------------ Sections

    /// <summary>« Map », « Connections » ou « Route ». Pilote l'affichage des trois panneaux.</summary>
    [ObservableProperty] private string _section = "Map";

    partial void OnSectionChanged(string value)
    {
        OnPropertyChanged(nameof(ShowMap));
        OnPropertyChanged(nameof(ShowConnections));
        OnPropertyChanged(nameof(ShowRoute));

        // La table TCP n'est relue que lorsqu'elle est visible : inutile d'énumérer les
        // processus toutes les 3 s pendant que l'utilisateur regarde la cartographie.
        if (value == "Connections") { RefreshConnections(); _connTimer.Start(); }
        else _connTimer.Stop();
    }

    public bool ShowMap => Section == "Map";
    public bool ShowConnections => Section == "Connections";
    public bool ShowRoute => Section == "Route";

    [RelayCommand]
    private void SelectSection(string section) => Section = section;

    // ------------------------------------------------------------------ Connexions actives

    [ObservableProperty] private string _connectionFilter = "";

    partial void OnConnectionFilterChanged(string value) => ConnectionsView.Refresh();

    private bool MatchesFilter(object item)
    {
        if (item is not ActiveConnection c) return false;
        if (ConnectionFilter.Length == 0) return true;

        return c.ProcessName.Contains(ConnectionFilter, StringComparison.OrdinalIgnoreCase)
            || c.RemoteDisplay.Contains(ConnectionFilter, StringComparison.OrdinalIgnoreCase)
            || c.RemoteAddress.Contains(ConnectionFilter, StringComparison.OrdinalIgnoreCase)
            || c.State.Contains(ConnectionFilter, StringComparison.OrdinalIgnoreCase)
            || c.Pid.ToString() == ConnectionFilter;
    }

    [ObservableProperty] private ActiveConnection? _selectedConnection;

    partial void OnSelectedConnectionChanged(ActiveConnection? value) =>
        KillSelectedProcessCommand.NotifyCanExecuteChanged();

    [ObservableProperty] private string _connectionStatus = "";

    [RelayCommand]
    private void RefreshConnections()
    {
        _connections.Refresh();
        ConnectionStatus = _connections.LastError.Length > 0
            ? _connections.LastError
            : $"{_connections.Connections.Count} connexion(s) TCP · "
              + $"{_connections.Connections.Select(c => c.Pid).Distinct().Count()} processus";
    }

    private bool HasSelection() => SelectedConnection is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void KillSelectedProcess()
    {
        if (SelectedConnection is not { } c) return;

        var answer = MessageBox.Show(
            $"Terminer {c.ProcessName} (PID {c.Pid}) ?{Environment.NewLine}{Environment.NewLine}" +
            "Le processus est tué sans enregistrer son travail en cours.",
            "AETHER — terminer le processus", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        ConnectionStatus = ConnectionService.KillProcess(c.Pid);
        RefreshConnections();
    }

    // ------------------------------------------------------------------ Test de débit

    private CancellationTokenSource? _speedCts;

    [ObservableProperty] private bool _isSpeedTesting;

    /// <summary>Le bouton sert à lancer puis à interrompre : son libellé doit le dire.</summary>
    public string SpeedButtonText => IsSpeedTesting ? "Interrompre" : "Mesurer le débit";

    partial void OnIsSpeedTestingChanged(bool value) => OnPropertyChanged(nameof(SpeedButtonText));

    [ObservableProperty] private double _speedProgress;
    [ObservableProperty] private string _speedStatus = "Aucune mesure effectuée.";
    [ObservableProperty] private string _speedDownText = "—";
    [ObservableProperty] private string _speedUpText = "—";
    [ObservableProperty] private string _speedLatencyText = "—";

    [RelayCommand]
    private async Task RunSpeedTest()
    {
        if (IsSpeedTesting) { _speedCts?.Cancel(); return; }

        IsSpeedTesting = true;
        SpeedProgress = 0;
        SpeedDownText = SpeedUpText = SpeedLatencyText = "…";
        _speedCts = new CancellationTokenSource();

        try
        {
            var r = await _speed.RunAsync(_speedCts.Token);
            SpeedDownText = Fmt(r.DownloadMbps);
            SpeedUpText = Fmt(r.UploadMbps);
            SpeedLatencyText = double.IsNaN(r.LatencyMs) ? "—" : $"{r.LatencyMs:0.#}";
            SpeedStatus = r.Message;
        }
        finally
        {
            IsSpeedTesting = false;
            SpeedProgress = 0;
            _speedCts?.Dispose();
            _speedCts = null;
        }

        static string Fmt(double v) => double.IsNaN(v) ? "—" : $"{v:0.#}";
    }

    // ------------------------------------------------------------------ Traceroute

    [ObservableProperty] private bool _isDiscovering;

    [RelayCommand]
    private async Task Rediscover()
    {
        IsDiscovering = true;
        try { await _net.RediscoverAsync(); }
        finally { IsDiscovering = false; }
    }

    public string InterfaceName => _net.InterfaceName.Length > 0 ? _net.InterfaceName : "—";

    public string LinkSpeedText => double.IsNaN(_net.LinkSpeedMbps)
        ? "vitesse de lien non déclarée"
        : $"lien négocié à {_net.LinkSpeedMbps:0} Mb/s";

    // ------------------------------------------------------------------ Verdict

    [ObservableProperty] private string _verdictTitle = "ANALYSE EN COURS";
    [ObservableProperty] private string _verdictDetail = "Premiers relevés en cours…";

    /// <summary>0/1/2, consommé par HealthToBrushConverter comme les maillons.</summary>
    [ObservableProperty] private int _verdictSeverity = 1;

    // ------------------------------------------------------------------ Mesures continues

    /// <summary>Débits affichés : « — » tant qu'aucun relevé n'a eu lieu.</summary>
    public string DownloadText => Text(Download);
    public string UploadText => Text(Upload);

    /// <summary>Valeurs pour les jauges : une mesure absente affiche une barre vide.</summary>
    public double DownloadBar => Bar(Download);
    public double UploadBar => Bar(Upload);

    /// <summary>Perte affichée, ou « — » si aucun maillon n'est mesurable (ICMP filtré).</summary>
    public string PacketLossText => PacketLoss < 0 ? "—" : $"{PacketLoss:0.#}";

    /// <summary>Combien de maillons servent réellement au calcul de la perte.</summary>
    [ObservableProperty] private int _measurableNodes;

    public string PacketLossDetail => PacketLoss < 0
        ? "aucun maillon ne répond à l'ICMP"
        : $"mesuré sur {MeasurableNodes} maillon(s)";

    /// <summary>Gigue du meilleur maillon : la métrique qui explique une connexion qui saccade.</summary>
    [ObservableProperty] private string _jitterText = "—";

    private static string Text(double v) => double.IsNaN(v) ? "—" : $"{v:0.#}";
    private static double Bar(double v) => double.IsNaN(v) ? 0 : v;

    private void Refresh()
    {
        Ping = _net.PingMs < 0 ? 0 : Math.Round(_net.PingMs);
        Download = _net.DownloadMbps;
        Upload = _net.UploadMbps;
        PacketLoss = _net.PacketLoss;
        MeasurableNodes = _net.MeasurableNodes;

        var jittered = _net.Nodes.Where(n => n.IsMeasurable && n.Jitter >= 0).ToList();
        JitterText = jittered.Count == 0 ? "—" : $"{jittered.Min(n => n.Jitter):0.#}";

        var v = NetworkDiagnosis.Evaluate(_net);
        VerdictTitle = v.Title;
        VerdictDetail = v.Detail;
        VerdictSeverity = v.Severity;

        OnPropertyChanged(nameof(InterfaceName));
        OnPropertyChanged(nameof(LinkSpeedText));
    }

    partial void OnPacketLossChanged(double value)
    {
        OnPropertyChanged(nameof(PacketLossText));
        OnPropertyChanged(nameof(PacketLossDetail));
    }

    partial void OnMeasurableNodesChanged(int value) => OnPropertyChanged(nameof(PacketLossDetail));

    partial void OnDownloadChanged(double value)
    {
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(DownloadBar));
    }

    partial void OnUploadChanged(double value)
    {
        OnPropertyChanged(nameof(UploadText));
        OnPropertyChanged(nameof(UploadBar));
    }
}
