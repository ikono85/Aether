using System.Globalization;

namespace Aether.Services.History;

/// <summary>Un relevé réseau horodaté. NaN = mesure indisponible à cet instant.</summary>
public readonly record struct NetworkSample(DateTime Time, double PingMs, double LossPct, double GatewayRttMs,
                                            double DownMbps, double UpMbps, bool Outage);

/// <summary>Une coupure : du premier relevé coupé au premier relevé rétabli.</summary>
public readonly record struct Outage(DateTime Start, DateTime End)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Fenêtre glissante de 24 h de relevés réseau, sans aucune dépendance système : c'est la partie
/// calculatoire de l'historique (coupures, disponibilité, CSV), testable isolément.
/// </summary>
public sealed class NetworkTimeline
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>Au-delà de cet écart entre deux relevés, AETHER était fermé ou en veille : pas de mesure, pas de coupure.</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(60);

    private readonly List<NetworkSample> _samples = new();

    public IReadOnlyList<NetworkSample> Samples => _samples;

    public void Add(NetworkSample sample)
    {
        if (_samples.Count > 0 && sample.Time < _samples[^1].Time) return;   // horloge reculée : ignoré
        _samples.Add(sample);
        TrimBefore(sample.Time - Window);
    }

    public void TrimBefore(DateTime cutoff)
    {
        int n = 0;
        while (n < _samples.Count && _samples[n].Time < cutoff) n++;
        if (n > 0) _samples.RemoveRange(0, n);
    }

    public IReadOnlyList<Outage> Outages()
    {
        var result = new List<Outage>();
        DateTime? start = null;
        DateTime lastOutage = default;
        NetworkSample? previous = null;

        foreach (var s in _samples)
        {
            bool gap = previous is { } p && s.Time - p.Time > MaxGap;

            if (start != null && (gap || !s.Outage))
            {
                // Une interruption des relevés clôt la coupure au dernier relevé coupé connu.
                result.Add(new Outage(start.Value, gap ? lastOutage : s.Time));
                start = null;
            }

            if (s.Outage)
            {
                start ??= s.Time;
                lastOutage = s.Time;
            }

            previous = s;
        }

        if (start != null) result.Add(new Outage(start.Value, lastOutage));   // coupure en cours
        return result;
    }

    /// <summary>Part des relevés sans coupure, en %. NaN sans relevé.</summary>
    public double AvailabilityPercent =>
        _samples.Count == 0 ? double.NaN : 100.0 * _samples.Count(s => !s.Outage) / _samples.Count;

    public double AveragePingMs => Aggregate(v => v.Average());
    public double MaxPingMs => Aggregate(v => v.Max());

    private double Aggregate(Func<IEnumerable<double>, double> f)
    {
        var values = _samples.Select(s => s.PingMs).Where(v => !double.IsNaN(v)).ToList();
        return values.Count == 0 ? double.NaN : f(values);
    }

    // ------------------------------------------------------------------ CSV

    /// <summary>Séparateur « ; » et décimales « . » : lisible par un tableur quelle que soit la langue.</summary>
    public const string CsvHeader = "time;ping_ms;loss_pct;gateway_rtt_ms;down_mbps;up_mbps;outage";

    public static string ToCsvLine(NetworkSample s) => string.Join(';',
        s.Time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        Number(s.PingMs), Number(s.LossPct), Number(s.GatewayRttMs), Number(s.DownMbps), Number(s.UpMbps),
        s.Outage ? "1" : "0");

    public static bool TryParseCsvLine(string line, out NetworkSample sample)
    {
        sample = default;
        var parts = line.Split(';');
        if (parts.Length != 7) return false;
        if (!DateTime.TryParseExact(parts[0], "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out var time)) return false;

        sample = new NetworkSample(time, Parse(parts[1]), Parse(parts[2]), Parse(parts[3]),
                                   Parse(parts[4]), Parse(parts[5]), parts[6] == "1");
        return true;
    }

    private static string Number(double v) =>
        double.IsNaN(v) ? "" : v.ToString("0.#", CultureInfo.InvariantCulture);

    private static double Parse(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
}
