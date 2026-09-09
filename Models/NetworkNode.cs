using CommunityToolkit.Mvvm.ComponentModel;

namespace Aether.Models;

/// <summary>Un maillon réel de la chaîne réseau (gateway, DNS, ISP, peer, CDN, cloud).</summary>
public partial class NetworkNode : ObservableObject
{
    public string Label { get; set; } = "";

    [ObservableProperty] private string _host = "…";   // IP ou hostname réel
    [ObservableProperty] private double _rtt = -1;      // ms, -1 = inconnu
    [ObservableProperty] private double _loss;          // 0..100 %
    [ObservableProperty] private bool _reachable;

    /// <summary>
    /// Vrai dès que ce maillon a répondu au moins une fois. Un routeur qui ne répond
    /// jamais filtre l'ICMP : son silence n'est pas de la perte de paquets, et sa mesure
    /// doit être exclue des statistiques plutôt que comptée comme 100 % de perte.
    /// </summary>
    [ObservableProperty] private bool _everAnswered;

    /// <summary>Vrai quand l'hôte n'a pas pu être découvert (traceroute incomplet).</summary>
    [ObservableProperty] private bool _unresolved = true;

    /// <summary>Ce maillon fournit-il une mesure de perte exploitable ?</summary>
    public bool IsMeasurable => EverAnswered && !Unresolved;

    /// <summary>0 = bon (vert), 1 = indéterminé ou dégradé (orange), 2 = critique (rouge).</summary>
    public int Health
    {
        get
        {
            // Jamais de rouge sur un maillon qu'on ne sait pas mesurer : on l'ignore.
            if (!IsMeasurable) return 1;
            if (!Reachable || Loss >= 60) return 2;
            if (Loss > 0 || Rtt > 80) return 1;
            return 0;
        }
    }

    public string RttText =>
        Unresolved ? "non découvert"
        : !EverAnswered ? "ICMP filtré"
        : !Reachable ? "timeout"
        : Rtt < 0 ? "…"
        : $"{Rtt:0} ms";

    partial void OnRttChanged(double value) { OnPropertyChanged(nameof(Health)); OnPropertyChanged(nameof(RttText)); }
    partial void OnLossChanged(double value) => OnPropertyChanged(nameof(Health));
    partial void OnReachableChanged(bool value) { OnPropertyChanged(nameof(Health)); OnPropertyChanged(nameof(RttText)); }

    partial void OnEverAnsweredChanged(bool value)
    {
        OnPropertyChanged(nameof(Health));
        OnPropertyChanged(nameof(RttText));
        OnPropertyChanged(nameof(IsMeasurable));
    }

    partial void OnUnresolvedChanged(bool value)
    {
        OnPropertyChanged(nameof(Health));
        OnPropertyChanged(nameof(RttText));
        OnPropertyChanged(nameof(IsMeasurable));
    }
}
