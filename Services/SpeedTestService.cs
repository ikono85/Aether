using System.Diagnostics;
using System.Net;
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
///
/// À exécuter hors du thread d'interface (Task.Run) : la boucle de lecture ne doit pas être
/// cadencée par le Dispatcher, sinon c'est lui qu'on mesurerait sur un lien rapide.
/// </summary>
public class SpeedTestService
{
    private const string DownUrl = "https://speed.cloudflare.com/__down?bytes=";
    private const string UpUrl = "https://speed.cloudflare.com/__up";

    /// <summary>Durée maximale de chaque sens. Au-delà, on calcule sur ce qui a transité.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>Délai maximal d'une requête de latence : un serveur muet ne bloque pas le test.</summary>
    private static readonly TimeSpan LatencyTimeout = TimeSpan.FromSeconds(10);

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

    /// <summary>Au plus 10 notifications de progression par seconde.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private long _lastProgress;

    /// <summary>Progression 0-100 et libellé de l'étape en cours (levé sur un thread de fond).</summary>
    public event Action<double, string>? Progress;

    public async Task<SpeedTestResult> RunAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Aether/1.0");

        double latency, down, up;

        try
        {
            Report(5, "Mesure de la latence…", force: true);
            latency = await MeasureLatencyAsync(http, ct).ConfigureAwait(false);

            Report(15, "Mesure du débit descendant…", force: true);
            down = await MeasureDownloadAsync(http, ct).ConfigureAwait(false);

            Report(60, "Mesure du débit montant…", force: true);
            up = await MeasureUploadAsync(http, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Failed("Test annulé.");
        }
        catch (OperationCanceledException)
        {
            return Failed("Le serveur de mesure ne répond pas (délai dépassé).");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            return Failed($"Le serveur de mesure limite les tests (code {(int)ex.StatusCode!}). Réessayez dans quelques minutes.");
        }
        catch (HttpRequestException ex)
        {
            return Failed($"Serveur de mesure injoignable : {ex.Message}");
        }
        catch (Exception ex)
        {
            Aether.Services.Infrastructure.Log.Warn("Test de débit impossible.", ex);
            return Failed($"Test impossible : {ex.Message}");
        }

        Report(100, "Terminé.", force: true);
        return new SpeedTestResult(down, up, latency, "Mesure terminée.");
    }

    private static SpeedTestResult Failed(string message) =>
        new(double.NaN, double.NaN, double.NaN, message);

    private void Report(double percent, string label, bool force = false)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastProgress), now) < ProgressInterval) return;
        Interlocked.Exchange(ref _lastProgress, now);
        Progress?.Invoke(percent, label);
    }

    /// <summary>Latence applicative (aller-retour HTTP), plus représentative qu'un ping ICMP.</summary>
    private static async Task<double> MeasureLatencyAsync(HttpClient http, CancellationToken ct)
    {
        var samples = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LatencyTimeout);

            var sw = Stopwatch.StartNew();
            using var r = await http.GetAsync(DownUrl + "0", HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                                    .ConfigureAwait(false);
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
                    HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);

                int read;
                while ((read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    Report(15 + Math.Min(44, sw.Elapsed.TotalSeconds / Budget.TotalSeconds * 45),
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
                using var r = await http.PostAsync(UpUrl, content, linked.Token).ConfigureAwait(false);
                r.EnsureSuccessStatusCode();

                sent += chunk;
                confirmed = sw.Elapsed;

                Report(60 + Math.Min(39, confirmed.TotalSeconds / Budget.TotalSeconds * 40),
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
