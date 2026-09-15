using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Aether.Services.Infrastructure;

/// <summary>
/// Rapport de diagnostic et détection des fins de session anormales.
///
/// Rien n'est envoyé : le rapport est copié ou exporté par l'utilisateur, qui choisit à qui le
/// transmettre. Les adresses IP publiques, le nom du PC et le nom d'utilisateur sont masqués.
/// </summary>
public static class Diagnostics
{
    private static string SessionMarker => Path.Combine(Log.Folder, "session.lock");
    private static string CrashSeenMarker => Path.Combine(Log.Folder, "crash-seen.txt");

    // ------------------------------------------------------------------ Sessions

    /// <summary>
    /// À appeler au démarrage de l'interface. Retourne une explication si la session précédente
    /// s'est mal terminée (plantage journalisé, ou arrêt forcé / coupure de courant), sinon null.
    /// </summary>
    public static string? BeginSession()
    {
        string? issue = null;
        try
        {
            Directory.CreateDirectory(Log.Folder);

            var seen = File.Exists(CrashSeenMarker) ? File.GetLastWriteTime(CrashSeenMarker) : DateTime.MinValue;
            var crash = Directory.EnumerateFiles(Log.Folder, "crash-*.txt")
                                 .Select(f => new FileInfo(f))
                                 .Where(f => f.LastWriteTime > seen)
                                 .OrderByDescending(f => f.LastWriteTime)
                                 .FirstOrDefault();

            if (crash != null)
                issue = $"AETHER s'est arrêté à cause d'une erreur le {crash.LastWriteTime:dd/MM/yyyy à HH:mm}.";
            else if (File.Exists(SessionMarker))
                issue = $"La session précédente d'AETHER (démarrée le {File.GetLastWriteTime(SessionMarker):dd/MM/yyyy à HH:mm}) " +
                        "ne s'est pas terminée normalement : arrêt forcé, blocage ou coupure de courant.";

            File.WriteAllText(SessionMarker, Environment.ProcessId.ToString());
        }
        catch (Exception ex) { Log.Warn("Marqueur de session illisible.", ex); }

        if (issue != null) Log.Warn($"Fin de session précédente anormale : {issue}");
        return issue;
    }

    public static void EndSession()
    {
        try { File.Delete(SessionMarker); } catch { }
    }

    public static void MarkCrashesSeen()
    {
        try { File.WriteAllText(CrashSeenMarker, DateTime.Now.ToString("O")); } catch { }
    }

    /// <summary>Écrit un rapport de plantage à côté des journaux (exception fatale).</summary>
    public static void WriteCrash(Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            var path = Path.Combine(Log.Folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, $"{DescribeEnvironment(null, null)}{System.Environment.NewLine}{exception}", Encoding.UTF8);
        }
        catch { /* le plantage est déjà en cours : ne rien aggraver */ }
    }

    // ------------------------------------------------------------------ Rapport

    public static string BuildReport(HardwareService? hardware, NetworkService? network)
    {
        var nl = System.Environment.NewLine;
        var text = new StringBuilder()
            .AppendLine("=== RAPPORT DE DIAGNOSTIC AETHER ===")
            .Append(DescribeEnvironment(hardware, network)).Append(nl)
            .AppendLine("=== ÉVÉNEMENTS RÉCENTS (avertissements, erreurs, modifications) ===");

        foreach (var line in RecentLogLines(80)) text.AppendLine(Mask(line));
        return text.ToString();
    }

    private static string DescribeEnvironment(HardwareService? hardware, NetworkService? network)
    {
        var version = typeof(Diagnostics).Assembly.GetName().Version?.ToString(3) ?? "?";
        var text = new StringBuilder()
            .AppendLine($"Date           : {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Version        : {version}")
            .AppendLine($"Système        : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})")
            .AppendLine($".NET           : {RuntimeInformation.FrameworkDescription}")
            .AppendLine($"Administrateur : {Elevation.IsElevated}")
            .AppendLine($"Langue         : {System.Globalization.CultureInfo.CurrentUICulture.Name}");

        if (hardware != null)
            text.AppendLine($"Capteurs       : {hardware.StatusMessage}");

        if (network != null)
        {
            text.AppendLine($"Interface      : {(network.InterfaceName.Length > 0 ? network.InterfaceName : "aucune")}" +
                            $" · IPv6 : {network.InterfaceSupportsIPv6}" +
                            $" · lien : {(double.IsNaN(network.LinkSpeedMbps) ? "?" : $"{network.LinkSpeedMbps:0} Mb/s")}");
            text.AppendLine($"Réseau         : ping {network.PingMs:0} ms · perte {network.PacketLoss:0.#} % · " +
                            $"{network.MeasurableNodes} maillon(s) mesurable(s)");
        }

        return text.ToString();
    }

    private static IEnumerable<string> RecentLogLines(int max)
    {
        var lines = new List<string>();
        try
        {
            foreach (var day in new[] { DateTime.Today.AddDays(-1), DateTime.Today })
            {
                var path = Path.Combine(Log.Folder, $"aether-{day:yyyyMMdd}.log");
                if (!File.Exists(path)) continue;
                lines.AddRange(File.ReadLines(path).Where(l =>
                    l.Contains(" WARN ") || l.Contains(" ERROR ") || l.Contains(" AUDIT ")));
            }
        }
        catch (Exception ex) { lines.Add($"(journal illisible : {ex.Message})"); }

        return lines.Skip(Math.Max(0, lines.Count - max));
    }

    /// <summary>Exporte le rapport, les journaux masqués, l'historique et les réglages dans une archive ZIP.</summary>
    public static void ExportZip(string zipPath, string report, string historyJson)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        AddText(zip, "rapport.txt", report);
        AddText(zip, "historique-modifications.json", historyJson);

        if (File.Exists(AppSettings.FilePath))
            AddText(zip, "settings.json", File.ReadAllText(AppSettings.FilePath));

        if (Directory.Exists(Log.Folder))
        {
            foreach (var file in Directory.EnumerateFiles(Log.Folder)
                                          .Where(f => f.EndsWith(".log") || f.EndsWith(".old") || Path.GetFileName(f).StartsWith("crash-")))
            {
                try { AddText(zip, $"journaux/{Path.GetFileName(file)}", Mask(File.ReadAllText(file))); }
                catch { /* fichier en cours d'écriture : ignoré */ }
            }
        }
    }

    private static void AddText(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    // ------------------------------------------------------------------ Masquage

    private static readonly Regex IPv4 = new(@"\b\d{1,3}(?:\.\d{1,3}){3}\b");
    private static readonly Regex IPv6 = new(@"(?<![\w:])(?:[0-9a-fA-F]{0,4}:){2,7}[0-9a-fA-F]{0,4}(?![\w:])");

    /// <summary>
    /// Masque ce qui identifie la personne ou son accès Internet : adresses IP publiques, nom du PC,
    /// nom d'utilisateur. Les adresses locales (box, réseau privé) sont conservées : elles sont
    /// indispensables au diagnostic et n'identifient personne.
    /// </summary>
    public static string Mask(string text)
    {
        text = IPv4.Replace(text, m =>
            IPAddress.TryParse(m.Value, out var ip) && !ConnectionService.IsLocal(m.Value) && ip.GetAddressBytes()[0] != 0
                ? "x.x.x.x" : m.Value);

        text = IPv6.Replace(text, m =>
            IPAddress.TryParse(m.Value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 &&
            !ConnectionService.IsLocal(m.Value)
                ? "[ipv6]" : m.Value);

        var user = System.Environment.UserName;
        if (user.Length > 1) text = Regex.Replace(text, Regex.Escape(user), "<utilisateur>", RegexOptions.IgnoreCase);

        var machine = System.Environment.MachineName;
        if (machine.Length > 1) text = Regex.Replace(text, Regex.Escape(machine), "<pc>", RegexOptions.IgnoreCase);

        return text;
    }
}
