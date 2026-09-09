using CommunityToolkit.Mvvm.ComponentModel;
using Aether.Models;
using Aether.Services;

namespace Aether.ViewModels;

public partial class NetworkViewModel : ObservableObject
{
    private readonly NetworkService _net;

    public NetworkNode Gateway => _net.Gateway;
    public NetworkNode Dns => _net.Dns;
    public NetworkNode Isp => _net.Isp;
    public NetworkNode Peer => _net.Peer;
    public NetworkNode Cdn => _net.Cdn;
    public NetworkNode Cloud => _net.Cloud;

    [ObservableProperty] private double _ping;
    [ObservableProperty] private double _download;
    [ObservableProperty] private double _upload;
    [ObservableProperty] private double _packetLoss;

    public NetworkViewModel(NetworkService net)
    {
        _net = net;
        net.Updated += Refresh;
        net.Start();
    }

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

    private static string Text(double v) => double.IsNaN(v) ? "—" : $"{v:0.#}";
    private static double Bar(double v) => double.IsNaN(v) ? 0 : v;

    private void Refresh()
    {
        Ping = _net.PingMs < 0 ? 0 : System.Math.Round(_net.PingMs);
        Download = _net.DownloadMbps;
        Upload = _net.UploadMbps;
        PacketLoss = _net.PacketLoss;
        MeasurableNodes = _net.MeasurableNodes;
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
