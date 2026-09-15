using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aether.Models;
using Aether.Services;
using Aether.Services.Dialogs;
using Aether.Services.History;
using Aether.Services.Infrastructure;

namespace Aether.ViewModels;

public partial class NetworkViewModel : ObservableObject
{
    private readonly NetworkService _net;
    private readonly ConnectionService _connections;
    private readonly SpeedTestService _speed;
    private readonly IDialogService _dialogs;
    private readonly ChangeHistoryService _history;

    /// <summary>
    /// Rafraîchissement de la table TCP. Séparé du timer de <see cref="NetworkService"/> :
    /// la lecture est locale, elle n'a pas à attendre le cycle des pings.
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

    /// <summary>Créé à la première ouverture de l'onglet ; NetworkService tourne déjà depuis le démarrage.</summary>
    public NetworkViewModel(NetworkService net, ConnectionService connections, SpeedTestService speed,
                            IDialogService dialogs, ChangeHistoryService history)
    {
        _net = net;
        _connections = connections;
        _speed = speed;
        _dialogs = dialogs;
        _history = history;
        net.Updated += Refresh;

        ConnectionsView = CollectionViewSource.GetDefaultView(_connections.Connections);
        ConnectionsView.Filter = MatchesFilter;
        ConnectionsView.SortDescriptions.Add(
            new SortDescription(nameof(ActiveConnection.ProcessName), ListSortDirection.Ascending));

        _connTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _connTimer.Tick += async (_, _) => await SafeRefreshConnections();

        // Levé depuis un thread de fond, déjà limité à 10 Hz : BeginInvoke ne bloque jamais la mesure.
        _speed.Progress += (pct, label) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            SpeedProgress = pct;
            SpeedStatus = label;
        });

        // Relevés déjà effectués avant l'ouverture de l'onglet : affichés immédiatement.
        Refresh();
    }

    // ------------------------------------------------------------------ Sections

    /// <summary>« Map », « Connections », « Route » ou « Tools ». Pilote l'affichage des panneaux.</summary>
    [ObservableProperty] private string _section = "Map";

    partial void OnSectionChanged(string value)
    {
        OnPropertyChanged(nameof(ShowMap));
        OnPropertyChanged(nameof(ShowConnections));
        OnPropertyChanged(nameof(ShowRoute));
        OnPropertyChanged(nameof(ShowTools));

        // La table TCP n'est relue que lorsqu'elle est visible : inutile d'énumérer les
        // processus toutes les 3 s pendant que l'utilisateur regarde la cartographie.
        if (value == "Connections") { _ = SafeRefreshConnections(); _connTimer.Start(); }
        else _connTimer.Stop();
    }

    public bool ShowMap => Section == "Map";
    public bool ShowConnections => Section == "Connections";
    public bool ShowRoute => Section == "Route";
    public bool ShowTools => Section == "Tools";

    // ------------------------------------------------------------------ Boîte à outils

    [ObservableProperty] private string _toolStatus = "";

    /// <summary>Une seule action système à la fois : elles partagent l'interface active.</summary>
    [ObservableProperty] private bool _isToolBusy;

    public System.Collections.ObjectModel.ObservableCollection<DnsCandidate> DnsResults { get; } = new();

    [ObservableProperty] private DnsCandidate? _selectedDns;

    /// <summary>Le DNS de l'interface ACTIVE a-t-il été modifié par AETHER ?</summary>
    public bool HasDnsBackup => NetworkToolbox.HasDnsBackup(_net.InterfaceId, _net.InterfaceName);

    private string _lastInterfaceId = "";

    private bool Ask(string title, string message) => _dialogs.Confirm($"AETHER — {title}", message);

    /// <summary>Encadre une action : verrou, statut, puis relecture de la topologie si elle a pu changer.</summary>
    private async Task RunTool(Func<Task<string>> action, bool rediscover)
    {
        if (IsToolBusy) return;
        IsToolBusy = true;
        ToolStatus = "En cours…";
        try
        {
            ToolStatus = await action();
            if (rediscover) await _net.RediscoverAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Action de la boîte à outils réseau interrompue.", ex);
            ToolStatus = $"Erreur : {ex.Message}";
        }
        finally
        {
            IsToolBusy = false;
            OnPropertyChanged(nameof(HasDnsBackup));
            _history.NotifyChanged();   // un changement de DNS apparaît dans l'onglet Historique
        }
    }

    [RelayCommand]
    private Task FlushDns() => RunTool(NetworkToolbox.FlushDnsAsync, rediscover: false);

    [RelayCommand]
    private Task RenewDhcp()
    {
        if (!Ask("renouveler le bail DHCP",
                "La box va réattribuer une configuration IP à cette interface. La connexion peut " +
                "décrocher quelques secondes, et l'adresse IP locale peut changer. Continuer ?"))
            return Task.CompletedTask;

        return RunTool(() => NetworkToolbox.RenewDhcpAsync(_net.InterfaceName), rediscover: true);
    }

    [RelayCommand]
    private Task ResetWinsock()
    {
        if (!Ask("réinitialiser Winsock",
                "Remet le catalogue Winsock à son état d'origine. Corrige une connexion cassée par " +
                "un VPN, un antivirus ou un logiciel désinstallé, mais retire aussi les composants " +
                "réseau qu'ils avaient ajoutés. Un redémarrage est nécessaire. Continuer ?"))
            return Task.CompletedTask;

        return RunTool(NetworkToolbox.ResetWinsockAsync, rediscover: false);
    }

    [RelayCommand]
    private async Task BenchmarkDns()
    {
        if (IsToolBusy) return;
        IsToolBusy = true;
        ToolStatus = "Comparaison des serveurs DNS…";
        try
        {
            var results = await NetworkToolbox.BenchmarkAsync(
                NetworkToolbox.Candidates(_net.Dns.Address), CancellationToken.None);

            DnsResults.Clear();
            foreach (var r in results.OrderBy(r => double.IsNaN(r.LatencyMs) ? double.MaxValue : r.LatencyMs))
                DnsResults.Add(r);

            var best = DnsResults.FirstOrDefault(r => !double.IsNaN(r.LatencyMs));
            var current = DnsResults.FirstOrDefault(r => r.IsCurrent);
            SelectedDns = best;

            ToolStatus = best is null ? "Aucun serveur DNS n'a répondu (port 53 filtré ?)."
                : best.IsCurrent ? $"Le DNS actuel est déjà le plus rapide ({best.LatencyText})."
                : current is null || double.IsNaN(current.LatencyMs)
                    ? $"{best.Name} est le plus rapide ({best.LatencyText})."
                    : $"{best.Name} répond en {best.LatencyText} contre {current.LatencyText} pour le DNS actuel.";
        }
        catch (Exception ex)
        {
            Log.Error("Comparaison DNS interrompue.", ex);
            ToolStatus = $"Erreur : {ex.Message}";
        }
        finally { IsToolBusy = false; }
    }

    [RelayCommand]
    private Task ApplyDns()
    {
        if (SelectedDns is not { } target) { ToolStatus = "Sélectionnez un serveur DNS dans la liste."; return Task.CompletedTask; }

        var nl = Environment.NewLine;
        var scope = _net.InterfaceSupportsIPv6 ? "IPv4 et IPv6" : "IPv4";
        if (!Ask("changer de DNS",
                $"Configurer {target.Name} ({target.Primary}) sur « {_net.InterfaceName} » ({scope}) ?{nl}{nl}" +
                "Le DNS d'origine de cette interface est sauvegardé et reste rétablissable avec " +
                "« Rétablir le DNS d'origine », même après un redémarrage."))
            return Task.CompletedTask;

        return RunTool(() => NetworkToolbox.ApplyDnsAsync(_net.InterfaceName, _net.InterfaceId,
                                                          _net.InterfaceSupportsIPv6, target), rediscover: true);
    }

    [RelayCommand]
    private Task RevertDns() =>
        RunTool(() => NetworkToolbox.RevertDnsAsync(_net.InterfaceId, _net.InterfaceName), rediscover: true);

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

    private async Task SafeRefreshConnections()
    {
        try { await RefreshConnections(); }
        catch (Exception ex)
        {
            Log.Error("Rafraîchissement des connexions interrompu.", ex);
            ConnectionStatus = $"Erreur : {ex.Message}";
        }
    }

    /// <summary>Lecture de la table TCP et des processus hors du thread d'interface.</summary>
    [RelayCommand]
    private async Task RefreshConnections()
    {
        await _connections.RefreshAsync();
        ConnectionStatus = _connections.LastError.Length > 0
            ? _connections.LastError
            : $"{_connections.Connections.Count} connexion(s) TCP · "
              + $"{_connections.Connections.Select(c => c.Pid).Distinct().Count()} processus";
    }

    private bool HasSelection() => SelectedConnection is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task KillSelectedProcess()
    {
        if (SelectedConnection is not { } c) return;

        if (!Ask("terminer le processus",
                $"Terminer {c.ProcessName} (PID {c.Pid}) ?{Environment.NewLine}{Environment.NewLine}" +
                "Le processus est tué sans enregistrer son travail en cours."))
            return;

        var message = ConnectionService.KillProcess(c);
        await SafeRefreshConnections();
        ConnectionStatus = message;
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
        var token = _speedCts.Token;

        try
        {
            // Hors du thread d'interface : sinon chaque lecture réseau repasserait par le Dispatcher.
            var r = await Task.Run(() => _speed.RunAsync(token));
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
        catch (Exception ex) { Log.Error("Redécouverte réseau impossible.", ex); }
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

        if (_net.IsDiscovering)
        {
            VerdictTitle = "ANALYSE EN COURS";
            VerdictDetail = "Changement de réseau détecté : découverte du nouveau chemin…";
            VerdictSeverity = 1;
        }
        else
        {
            var v = NetworkDiagnosis.Evaluate(_net);
            VerdictTitle = v.Title;
            VerdictDetail = v.Detail;
            VerdictSeverity = v.Severity;
        }

        OnPropertyChanged(nameof(InterfaceName));
        OnPropertyChanged(nameof(LinkSpeedText));

        if (_net.InterfaceId != _lastInterfaceId)
        {
            _lastInterfaceId = _net.InterfaceId;
            OnPropertyChanged(nameof(HasDnsBackup));
        }
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
