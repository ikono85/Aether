using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aether.Models;

/// <summary>Un maillon réel de la chaîne réseau (gateway, DNS, ISP, peer, CDN, cloud).</summary>
public partial class NetworkNode : ObservableObject
{
    public string Label { get; set; } = "";

    /// <summary>Affichage : IP, ou nom PTR quand il est connu.</summary>
    [ObservableProperty] private string _host = "…";

    /// <summary>
    /// Adresse IP réellement sondée. Distincte de <see cref="Host"/> : beaucoup de noms PTR de
    /// routeurs ne se résolvent pas dans l'autre sens, pinger le nom rendrait le maillon muet.
    /// </summary>
    [ObservableProperty] private string _address = "";
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

    // ------------------------------------------------------------ Historique et jitter

    /// <summary>Nombre de relevés conservés, aligné sur les courbes de l'onglet Performance.</summary>
    public const int HistoryCap = 60;

    private readonly List<double> _history = new();

    /// <summary>
    /// Gigue : moyenne des écarts absolus entre relevés consécutifs (RFC 3550 simplifié).
    /// -1 tant qu'on n'a pas deux mesures réussies. C'est elle, et non le ping moyen,
    /// qui explique une connexion « qui saccade » alors que la latence semble bonne.
    /// </summary>
    public double Jitter { get; private set; } = -1;

    public string JitterText => Jitter < 0 ? "—" : $"{Jitter:0.#} ms";

    /// <summary>Courbe du RTT prête à tracer, normalisée dans <see cref="SparkWidth"/> × <see cref="SparkHeight"/>.</summary>
    public PointCollection SparklinePoints { get; private set; } = new();

    public const double SparkWidth = 150;
    public const double SparkHeight = 26;

    /// <summary>
    /// Enregistre le relevé courant. Les timeouts ne sont pas poussés dans l'historique :
    /// une valeur manquante n'est pas une latence nulle, et l'injecter fausserait la gigue.
    /// </summary>
    public void RecordSample()
    {
        if (!Reachable || Rtt < 0) return;

        _history.Add(Rtt);
        if (_history.Count > HistoryCap) _history.RemoveAt(0);

        if (_history.Count >= 2)
        {
            double sum = 0;
            for (int i = 1; i < _history.Count; i++) sum += Math.Abs(_history[i] - _history[i - 1]);
            Jitter = Math.Round(sum / (_history.Count - 1), 1);
            OnPropertyChanged(nameof(Jitter));
            OnPropertyChanged(nameof(JitterText));
        }

        BuildSparkline();
    }

    /// <summary>Oublie tout ce qui a été mesuré : le maillon désigne désormais une autre machine.</summary>
    public void Reset()
    {
        _history.Clear();
        Jitter = -1;
        OnPropertyChanged(nameof(Jitter));
        OnPropertyChanged(nameof(JitterText));
        BuildSparkline();

        Address = "";
        Host = "…";
        Rtt = -1;
        Loss = 0;
        Reachable = false;
        EverAnswered = false;
        Unresolved = true;
    }

    private void BuildSparkline()
    {
        var pts = new PointCollection();
        if (_history.Count >= 2)
        {
            // Échelle plancher à 20 ms : sans elle, une connexion stable à 3-4 ms produirait
            // une courbe en dents de scie spectaculaire pour une variation d'une milliseconde.
            double max = Math.Max(20, _history.Max());
            double step = SparkWidth / (_history.Count - 1);

            for (int i = 0; i < _history.Count; i++)
                pts.Add(new Point(i * step, SparkHeight - Math.Clamp(_history[i] / max, 0, 1) * SparkHeight));
        }
        pts.Freeze();
        SparklinePoints = pts;
        OnPropertyChanged(nameof(SparklinePoints));
    }

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
