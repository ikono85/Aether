using System.Diagnostics;
using System.Net.Http;

namespace Aether.Services;

/// <summary>Résultat d'une mesure de débit. NaN = mesure non effectuée ou échouée.</summary>
public record SpeedTestResult(double DownloadMbps, double UploadMbps, double LatencyMs, string Message);

/// <summary>
/// Mesure la CAPACITÉ du lien, à ne pas confondre avec le débit affiché en permanence par
/// <see cref="NetworkService"/> : ce dernier observe le trafic qui passe réellement (donc
/// proche de zéro quand rien ne télécharge). Ici on génère volontairement du trafic.
///
/// Les points de mesure sont ceux de Cloudflare (speed.cloudflare.com), utilisés par leur
/// propre test de débit : anycast, donc proche géographiquement, et sans clé d'API.
/// </summary>
public class SpeedTestService
{
    private const string DownUrl = "https://speed.cloudflare.com/__down?bytes=";
    private const string UpUrl = "https://speed.cloudflare.com/__up";

    /// <summary>Durée maximale de chaque sens. Au-delà, on calcule sur ce qui a transité.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Taille d'un bloc téléchargé. Cloudflare répond 403 au-delà d'environ 50 Mo par
    /// requête : on enchaîne donc des blocs jusqu'à épuiser le budget de temps.
    /// </summary>
    private const long DownloadChunkBytes = 25L * 1024 * 1024;
    /// <summary>
    /// Envoi par blocs de taille adaptative : on démarre petit pour qu'une connexion lente
    /// termine au moins un bloc dans le budget, puis on double tant qu'un bloc part en
    /// moins d'une seconde. Plafond aligné sur le bloc descendant.
    /// </summary>
    private const int UploadFirstChunkBytes = 1 * 1024 * 1024;
    private const int UploadMaxChunkBytes = 25 * 1024 * 1024;

    /// <summary>Progression 0-100 et libellé de l'étape en cours.</summary>
    public event Action<double, string>? Progress;

    public async Task<SpeedTestResult> RunAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Aether/1.0");

        double latency, down, up;

        try
        {
            Progress?.Invoke(5, "Mesure de la latence…");
            latency = await MeasureLatencyAsync(http, ct);

            Progress?.Invoke(15, "Mesure du débit descendant…");
            down = await MeasureDownloadAsync(http, ct);

            Progress?.Invoke(60, "Mesure du débit montant…");
            up = await MeasureUploadAsync(http, ct);
        }
        catch (OperationCanceledException)
        {
            return new SpeedTestResult(double.NaN, double.NaN, double.NaN, "Test annulé.");
        }
        catch (Exception ex)
        {
            return new SpeedTestResult(double.NaN, double.NaN, double.NaN,
                $"Test impossible : {ex.Message}");
        }

        Progress?.Invoke(100, "Terminé.");
        return new SpeedTestResult(down, up, latency, "Mesure terminée.");
    }

    /// <summary>Latence applicative (aller-retour HTTP), plus représentative qu'un ping ICMP.</summary>
    private static async Task<double> MeasureLatencyAsync(HttpClient http, CancellationToken ct)
    {
        var samples = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            using var r = await http.GetAsync(DownUrl + "0", HttpCompletionOption.ResponseHeadersRead, ct);
            r.EnsureSuccessStatusCode();
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        // Médiane : une seule requête ralentie par l'établissement TLS ne doit pas décider du résultat.
        samples.Sort();
        return Math.Round(samples[samples.Count / 2], 1);
    }

    private async Task<double> MeasureDownloadAsync(HttpClient http, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(Budget);

        var buffer = new byte[128 * 1024];
        long total = 0;
        var sw = Stopwatch.StartNew();

        try
        {
            // Blocs successifs jusqu'au budget : une connexion rapide avale un bloc en
            // moins d'une seconde, trop court pour une mesure stable.
            while (!linked.IsCancellationRequested)
            {
                using var response = await http.GetAsync(DownUrl + DownloadChunkBytes,
                    HttpCompletionOption.ResponseHeadersRead, linked.Token);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);

                int read;
                while ((read = await stream.ReadAsync(buffer, linked.Token)) > 0)
                {
                    total += read;
                    Progress?.Invoke(15 + Math.Min(44, sw.Elapsed.TotalSeconds / Budget.TotalSeconds * 45),
                        $"Descendant… {total / 1_048_576.0:0} Mo");
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Budget de temps épuisé : c'est le cas nominal sur une connexion rapide,
            // le débit se calcule sur ce qui a été reçu.
        }

        sw.Stop();
        return Rate(total, sw.Elapsed);
    }

    private async Task<double> MeasureUploadAsync(HttpClient http, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(Budget);

        var payload = new byte[UploadMaxChunkBytes];
        Random.Shared.NextBytes(payload);   // données incompressibles : sinon on mesurerait gzip

        int chunk = UploadFirstChunkBytes;
        long sent = 0;

        // Temps arrêté à la fin du dernier bloc CONFIRMÉ : un bloc coupé par le budget n'est
        // compté ni en octets ni en durée, puisqu'on ignore ce qui est réellement parti.
        TimeSpan confirmed = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();

        try
        {
            while (!linked.IsCancellationRequested)
            {
                var started = sw.Elapsed;

                using var content = new ByteArrayContent(payload, 0, chunk);
                using var r = await http.PostAsync(UpUrl, content, linked.Token);
                r.EnsureSuccessStatusCode();

                sent += chunk;
                confirmed = sw.Elapsed;

                Progress?.Invoke(60 + Math.Min(39, confirmed.TotalSeconds / Budget.TotalSeconds * 40),
                    $"Montant… {sent / 1_048_576.0:0} Mo");

                if ((confirmed - started).TotalSeconds < 1 && chunk < UploadMaxChunkBytes)
                    chunk = Math.Min(chunk * 2, UploadMaxChunkBytes);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Budget épuisé pendant un bloc : cas nominal, on garde les blocs confirmés.
        }

        return Rate(sent, confirmed);
    }

    private static double Rate(long bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0.05 || bytes == 0
            ? double.NaN
            : Math.Round(bytes * 8.0 / elapsed.TotalSeconds / 1_000_000, 1);
}
