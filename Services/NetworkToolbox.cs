using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;

namespace Aether.Services;

/// <summary>Un serveur DNS candidat et sa latence de résolution mesurée.</summary>
public record DnsCandidate(string Name, string Primary, string Secondary)
{
    /// <summary>Médiane en ms, NaN si le serveur n'a jamais répondu.</summary>
    public double LatencyMs { get; init; } = double.NaN;

    public string LatencyText => double.IsNaN(LatencyMs) ? "sans réponse" : $"{LatencyMs:0} ms";

    /// <summary>Vrai pour le DNS actuellement configuré (celui de la box ou du FAI en général).</summary>
    public bool IsCurrent { get; init; }
}

/// <summary>État DNS d'origine, sauvegardé avant toute modification.</summary>
public record DnsBackup(string InterfaceName, bool Dhcp, string[] Servers);

/// <summary>
/// Actions réseau ponctuelles. Contrairement au reste de l'onglet, elles MODIFIENT le
/// système : seul le changement de DNS est persistant, et son état d'origine est
/// sauvegardé dans %AppData%\Aether\dns-backup.json, donc annulable après redémarrage.
///
/// Fichier distinct de restore.json à dessein : le moteur d'optimisation garde ce journal
/// en mémoire et le réécrit en entier, ce qui effacerait une entrée ajoutée à côté de lui.
/// </summary>
public static class NetworkToolbox
{
    private static string DnsBackupPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aether", "dns-backup.json");

    /// <summary>Domaine interrogé : très populaire, donc en cache chez tous les résolveurs publics.</summary>
    private const string ProbeDomain = "www.google.com";

    // ------------------------------------------------------------------ Actions ponctuelles

    public static Task<string> FlushDnsAsync() => Task.Run(() =>
    {
        var (code, output) = Run("ipconfig", "/flushdns");
        return code == 0
            ? "Cache DNS vidé : les prochaines résolutions interrogeront à nouveau le serveur."
            : $"Échec du vidage du cache DNS. {Tail(output)}";
    });

    /// <summary>
    /// Renouvelle le bail DHCP sans le libérer au préalable : <c>/release</c> couperait la
    /// connexion le temps de la renégociation, <c>/renew</c> seul la conserve.
    /// </summary>
    public static Task<string> RenewDhcpAsync(string interfaceName) => Task.Run(() =>
    {
        if (string.IsNullOrEmpty(interfaceName)) return "Aucune interface active détectée.";

        var (code, output) = Run("ipconfig", $"/renew \"{interfaceName}\"", 60_000);
        return code == 0
            ? $"Bail DHCP renouvelé sur « {interfaceName} »."
            : $"Échec du renouvellement (interface en IP fixe ?). {Tail(output)}";
    });

    /// <summary>Réinitialise le catalogue Winsock. Ne prend effet qu'après redémarrage.</summary>
    public static Task<string> ResetWinsockAsync() => Task.Run(() =>
    {
        if (!StartupManager.IsElevated) return "Droits administrateur requis.";

        var (code, output) = Run("netsh", "winsock reset");
        return code == 0
            ? "Catalogue Winsock réinitialisé. Redémarrez Windows pour l'appliquer."
            : $"Échec de la réinitialisation Winsock. {Tail(output)}";
    });

    // ------------------------------------------------------------------ Comparateur DNS

    public static IReadOnlyList<DnsCandidate> Candidates(string currentDns) => new[]
    {
        new DnsCandidate("Actuel", currentDns, "") { IsCurrent = true },
        new DnsCandidate("Cloudflare", "1.1.1.1", "1.0.0.1"),
        new DnsCandidate("Google", "8.8.8.8", "8.8.4.4"),
        new DnsCandidate("Quad9", "9.9.9.9", "149.112.112.112"),
    };

    /// <summary>
    /// Mesure chaque serveur par une vraie requête DNS en UDP adressée directement à lui.
    /// <see cref="Dns"/> passerait par le résolveur système, donc toujours par le même
    /// serveur — impossible de comparer quoi que ce soit ainsi.
    /// </summary>
    public static async Task<List<DnsCandidate>> BenchmarkAsync(IEnumerable<DnsCandidate> candidates,
                                                                CancellationToken ct)
    {
        var results = new List<DnsCandidate>();
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(c with { LatencyMs = await MeasureAsync(c.Primary, ct) });
        }
        return results;
    }

    private static async Task<double> MeasureAsync(string server, CancellationToken ct)
    {
        if (!IPAddress.TryParse(server, out var ip)) return double.NaN;

        var samples = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            try
            {
                using var udp = new UdpClient(ip.AddressFamily);
                udp.Connect(ip, 53);

                var query = BuildQuery(ProbeDomain);
                var sw = Stopwatch.StartNew();
                await udp.SendAsync(query, ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(1500);
                var reply = await udp.ReceiveAsync(timeout.Token);
                sw.Stop();

                // Même identifiant de transaction = c'est bien la réponse à notre requête.
                if (reply.Buffer.Length >= 2 && reply.Buffer[0] == query[0] && reply.Buffer[1] == query[1])
                    samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* timeout */ }
            catch (SocketException) { /* serveur injoignable */ }
        }

        if (samples.Count == 0) return double.NaN;
        // Médiane : la première requête paie parfois un ARP ou une route à établir.
        samples.Sort();
        return Math.Round(samples[samples.Count / 2], 1);
    }

    /// <summary>Requête DNS minimale : type A, classe IN, récursion demandée.</summary>
    private static byte[] BuildQuery(string domain)
    {
        var packet = new List<byte>();
        var id = (ushort)Random.Shared.Next(ushort.MaxValue);
        packet.AddRange(new[] { (byte)(id >> 8), (byte)id });
        packet.AddRange(new byte[] { 0x01, 0x00 });  // RD = 1
        packet.AddRange(new byte[] { 0x00, 0x01 });  // 1 question
        packet.AddRange(new byte[6]);                 // aucune réponse, autorité ni additionnel

        foreach (var label in domain.Split('.'))
        {
            packet.Add((byte)label.Length);
            packet.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        packet.Add(0);
        packet.AddRange(new byte[] { 0x00, 0x01, 0x00, 0x01 });  // type A, classe IN
        return packet.ToArray();
    }

    // ------------------------------------------------------------------ Changement de DNS

    public static bool HasDnsBackup() => File.Exists(DnsBackupPath);

    private static DnsBackup? ReadBackup()
    {
        try { return JsonSerializer.Deserialize<DnsBackup>(File.ReadAllText(DnsBackupPath)); }
        catch { return null; }
    }

    private static void WriteBackup(DnsBackup backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DnsBackupPath)!);
        File.WriteAllText(DnsBackupPath, JsonSerializer.Serialize(backup));
    }

    /// <summary>
    /// Bascule l'interface active sur un DNS. L'état d'origine n'est sauvegardé qu'au
    /// PREMIER changement : passer de Cloudflare à Google puis annuler doit ramener au DNS
    /// d'avant Aether, pas à Cloudflare.
    /// </summary>
    public static Task<string> ApplyDnsAsync(string interfaceName, string interfaceId,
                                             DnsCandidate target) => Task.Run(() =>
    {
        if (!StartupManager.IsElevated) return "Droits administrateur requis.";
        if (target.IsCurrent) return "C'est déjà le DNS configuré.";
        if (string.IsNullOrEmpty(interfaceName)) return "Aucune interface active détectée.";

        if (!HasDnsBackup())
        {
            try { WriteBackup(ReadCurrentDns(interfaceName, interfaceId)); }
            catch (Exception ex)
            {
                // Sans sauvegarde, le changement ne serait pas annulable : on refuse.
                return $"Sauvegarde du DNS d'origine impossible, rien n'a été modifié. {ex.Message}";
            }
        }

        var (code, output) = Run("netsh",
            $"interface ipv4 set dnsservers name=\"{interfaceName}\" source=static address={target.Primary} register=primary validate=no");
        if (code != 0) return $"Échec du changement de DNS. {Tail(output)}";

        if (target.Secondary.Length > 0)
            Run("netsh", $"interface ipv4 add dnsservers name=\"{interfaceName}\" address={target.Secondary} index=2 validate=no");

        Run("ipconfig", "/flushdns");
        return $"DNS {target.Name} ({target.Primary}) appliqué sur « {interfaceName} ». Annulable.";
    });

    public static Task<string> RevertDnsAsync() => Task.Run(() =>
    {
        if (!StartupManager.IsElevated) return "Droits administrateur requis.";

        var backup = ReadBackup();
        if (backup == null) return "Aucun changement de DNS à annuler.";

        int code;
        string output;
        if (backup.Dhcp || backup.Servers.Length == 0)
        {
            (code, output) = Run("netsh",
                $"interface ipv4 set dnsservers name=\"{backup.InterfaceName}\" source=dhcp");
        }
        else
        {
            (code, output) = Run("netsh",
                $"interface ipv4 set dnsservers name=\"{backup.InterfaceName}\" source=static address={backup.Servers[0]} register=primary validate=no");
            for (int i = 1; i < backup.Servers.Length && code == 0; i++)
                Run("netsh", $"interface ipv4 add dnsservers name=\"{backup.InterfaceName}\" address={backup.Servers[i]} index={i + 1} validate=no");
        }

        if (code != 0) return $"Échec de la restauration du DNS. {Tail(output)}";

        try { File.Delete(DnsBackupPath); } catch { }
        Run("ipconfig", "/flushdns");
        return backup.Dhcp
            ? $"DNS d'origine rétabli : attribué automatiquement (DHCP) sur « {backup.InterfaceName} »."
            : $"DNS d'origine rétabli : {string.Join(", ", backup.Servers)}.";
    });

    /// <summary>
    /// Distingue un DNS fixé à la main d'un DNS reçu par DHCP. Les deux apparaissent
    /// identiques dans <c>GetIPProperties().DnsAddresses</c> ; seul le registre les sépare :
    /// <c>NameServer</c> n'est renseigné que pour une configuration statique.
    /// </summary>
    private static DnsBackup ReadCurrentDns(string interfaceName, string interfaceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{interfaceId}");
            var raw = key?.GetValue("NameServer") as string ?? "";
            var servers = raw.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return new DnsBackup(interfaceName, servers.Length == 0, servers);
        }
        catch
        {
            // Registre illisible : DHCP est l'hypothèse la plus sûre, c'est le réglage par défaut.
            return new DnsBackup(interfaceName, true, Array.Empty<string>());
        }
    }

    // ------------------------------------------------------------------ Utilitaires

    private static (int Code, string Output) Run(string exe, string args, int timeoutMs = 30_000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p == null) return (-1, "");

            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                return (-1, "Délai dépassé.");
            }
            return (p.ExitCode, (stdout.Result + " " + stderr.Result).Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    /// <summary>Dernière ligne non vide d'une sortie console : c'est là que figure l'erreur.</summary>
    private static string Tail(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
}
