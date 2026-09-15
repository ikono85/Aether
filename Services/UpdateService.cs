using System.Net;
using System.Net.Http;
using System.Text.Json;
using Aether.Services.Infrastructure;

namespace Aether.Services;

public sealed record UpdateInfo(Version Version, string Tag, string Url, string Name);

/// <summary>
/// Vérification des mises à jour sur les versions publiées du dépôt GitHub. Désactivée par
/// défaut (proposée à l'accueil) et limitée à une requête par jour.
///
/// Volontairement, rien n'est téléchargé ni exécuté : AETHER tourne en administrateur, un
/// installeur récupéré automatiquement devrait d'abord être signé et sa signature vérifiée.
/// L'utilisateur est seulement prévenu et la page de la version est ouverte à sa demande.
/// </summary>
public sealed class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/ikono85/Aether/releases/latest";

    /// <summary>Seules les pages de ce dépôt peuvent être ouvertes depuis une réponse de l'API.</summary>
    public const string AllowedPagePrefix = "https://github.com/ikono85/Aether/releases/";

    public static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);

    public async Task<(UpdateInfo? Update, string Message)> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Aether/{CurrentVersion.ToString(3)}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var response = await http.GetAsync(LatestReleaseApi, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (null, "Aucune version publiée pour l'instant.");
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;

            if (!TryParseTag(tag, out var version)) return (null, "Version publiée illisible.");
            if (!url.StartsWith(AllowedPagePrefix, StringComparison.Ordinal))
            {
                Log.Warn($"Lien de version inattendu ignoré : {url}");
                return (null, "Réponse inattendue du serveur de mises à jour : ignorée.");
            }

            return IsNewer(version, CurrentVersion)
                ? (new UpdateInfo(version, tag, url, name), $"Version {version.ToString(3)} disponible (vous avez la {CurrentVersion.ToString(3)}).")
                : (null, $"AETHER est à jour (v{CurrentVersion.ToString(3)}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Info($"Vérification des mises à jour impossible : {ex.Message}");
            return (null, "Vérification impossible (pas de connexion ou GitHub indisponible).");
        }
    }

    /// <summary>« v1.2.0 », « 1.2 » ou « v1.2.0-beta » → 1.2.0.</summary>
    internal static bool TryParseTag(string tag, out Version version)
    {
        var core = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (Version.TryParse(core, out var parsed)) { version = parsed; return true; }
        version = new Version(0, 0);
        return false;
    }

    internal static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));
}
