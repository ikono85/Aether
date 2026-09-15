using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;
using Aether.Services.Infrastructure;

namespace Aether.Services;

/// <summary>Un serveur DNS candidat et sa latence de résolution mesurée.</summary>
public record DnsCandidate(string Name, string Primary, string Secondary)
{
    /// <summary>Adresses IPv6 du même fournisseur, vides si inconnues.</summary>
    public string Primary6 { get; init; } = "";
    public string Secondary6 { get; init; } = "";

    /// <summary>Médiane en ms, NaN si le serveur n'a jamais répondu.</summary>
    public double LatencyMs { get; init; } = double.NaN;

    public string LatencyText => double.IsNaN(LatencyMs) ? "sans réponse" : $"{LatencyMs:0} ms";

    /// <summary>Vrai pour le DNS actuellement configuré (celui de la box ou du FAI en général).</summary>
    public bool IsCurrent { get; init; }
}

/// <summary>État DNS d'origine d'une interface, sauvegardé avant toute modification.</summary>
public record DnsBackup(string InterfaceName, bool Dhcp, string[] Servers)
{
    /// <summary>Configuration IPv6 d'origine ; null dans les sauvegardes antérieures à sa prise en charge.</summary>
    public bool? Dhcp6 { get; init; }
    public string[]? Servers6 { get; init; }
}

/// <summary>Fichier de sauvegarde : une entrée par interface (clé = GUID de l'interface).</summary>
public record DnsBackupFile(Dictionary<string, DnsBackup> Interfaces);

/// <summary>
/// Actions réseau ponctuelles. Contrairement au reste de l'onglet, elles MODIFIENT le
/// système : seul le changement de DNS est persistant, et son état d'origine est
/// sauvegardé PAR INTERFACE dans %ProgramData%\Aether\dns-backups.json (accès
/// administrateurs), donc annulable après redémarrage — y compris si l'on a changé le DNS du
/// Wi-Fi puis celui de l'Ethernet. Ce fichier est relu en administrateur : son contenu est
/// revalidé avant d'être transmis à netsh.
///
/// IPv4 et IPv6 sont modifiés ensemble : sur un réseau double pile, ne changer que l'IPv4
/// laisserait Windows continuer à interroger les DNS IPv6 annoncés par la box.
/// </summary>
public static class NetworkToolbox
{
    private static string BackupsPath => AppPaths.SecureFile("dns-backups.json");

    /// <summary>Ancien format (une seule sauvegarde, toutes interfaces confondues).</summary>
    private static string LegacyBackupPath => AppPaths.SecureFile("dns-backup.json");

    /// <summary>Domaine interrogé : très populaire, donc en cache chez tous les résolveurs publics.</summary>
    private const string ProbeDomain = "www.google.com";

    // ------------------------------------------------------------------ Actions ponctuelles

    public static Task<string> FlushDnsAsync() => Task.Run(() =>
    {
        var r = ProcessRunner.Run("ipconfig", new[] { "/flushdns" });
        return r.Ok
            ? "Cache DNS vidé : les prochaines résolutions interrogeront à nouveau le serveur."
            : $"Échec du vidage du cache DNS. {Tail(r.Output)}";
    });

    /// <summary>
    /// Renouvelle le bail DHCP sans le libérer au préalable : <c>/release</c> couperait la
    /// connexion le temps de la renégociation, <c>/renew</c> seul la conserve.
    /// </summary>
    public static Task<string> RenewDhcpAsync(string interfaceName) => Task.Run(() =>
    {
        if (!IsValidInterfaceName(interfaceName)) return "Aucune interface active valide détectée.";

        var r = ProcessRunner.Run("ipconfig", new[] { "/renew", interfaceName }, 60_000);
        if (r.Ok) Log.Audit($"Bail DHCP renouvelé sur « {interfaceName} ».");
        return r.Ok
            ? $"Bail DHCP renouvelé sur « {interfaceName} »."
            : $"Échec du renouvellement (interface en IP fixe ?). {Tail(r.Output)}";
    });

    /// <summary>Réinitialise le catalogue Winsock. Ne prend effet qu'après redémarrage.</summary>
    public static Task<string> ResetWinsockAsync() => Task.Run(() =>
    {
        if (!StartupManager.IsElevated) return "Droits administrateur requis.";

        var r = ProcessRunner.Run("netsh", new[] { "winsock", "reset" });
        if (r.Ok) Log.Audit("Catalogue Winsock réinitialisé.");
        return r.Ok
            ? "Catalogue Winsock réinitialisé. Redémarrez Windows pour l'appliquer."
            : $"Échec de la réinitialisation Winsock. {Tail(r.Output)}";
    });

    // ------------------------------------------------------------------ Comparateur DNS

    public static IReadOnlyList<DnsCandidate> Candidates(string currentDns) => new[]
    {
        new DnsCandidate("Actuel", currentDns, "") { IsCurrent = true },
        new DnsCandidate("Cloudflare", "1.1.1.1", "1.0.0.1")
            { Primary6 = "2606:4700:4700::1111", Secondary6 = "2606:4700:4700::1001" },
        new DnsCandidate("Google", "8.8.8.8", "8.8.4.4")
            { Primary6 = "2001:4860:4860::8888", Secondary6 = "2001:4860:4860::8844" },
        new DnsCandidate("Quad9", "9.9.9.9", "149.112.112.112")
            { Primary6 = "2620:fe::fe", Secondary6 = "2620:fe::9" },
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

    // ------------------------------------------------------------------ Sauvegardes

    /// <summary>Vrai si le DNS de CETTE interface a été modifié par AETHER et peut être rétabli.</summary>
    public static bool HasDnsBackup(string interfaceId, string interfaceName) =>
        Find(ReadBackups(), interfaceId, interfaceName) != null;

    /// <summary>Sauvegardes relues et revalidées ; l'ancien fichier unique est repris s'il existe.</summary>
    private static Dictionary<string, DnsBackup> ReadBackups()
    {
        var result = new Dictionary<string, DnsBackup>(StringComparer.OrdinalIgnoreCase);

        var (file, _) = SafeFile.ReadJson<DnsBackupFile>(BackupsPath);
        foreach (var (key, backup) in file?.Interfaces ?? new Dictionary<string, DnsBackup>())
        {
            if (IsValid(backup)) result[key] = backup;
            else Log.Warn($"Sauvegarde DNS invalide rejetée ({key}).");
        }

        var (legacy, _) = SafeFile.ReadJson<DnsBackup>(LegacyBackupPath);
        if (legacy != null && IsValid(legacy) &&
            !result.Values.Any(b => b.InterfaceName.Equals(legacy.InterfaceName, StringComparison.OrdinalIgnoreCase)))
        {
            result[NameKey(legacy.InterfaceName)] = legacy;
        }

        return result;
    }

    private static void WriteBackups(Dictionary<string, DnsBackup> backups)
    {
        if (backups.Count == 0) SafeFile.Delete(BackupsPath);
        else SafeFile.WriteJson(BackupsPath, new DnsBackupFile(backups));

        // L'éventuelle sauvegarde de l'ancien format est désormais reprise dans le nouveau fichier.
        SafeFile.Delete(LegacyBackupPath);
    }

    private static string NameKey(string interfaceName) => "name:" + interfaceName;

    private static string? FindKey(Dictionary<string, DnsBackup> backups, string interfaceId, string interfaceName)
    {
        if (interfaceId.Length > 0 && backups.ContainsKey(interfaceId)) return interfaceId;
        var byName = NameKey(interfaceName);
        return backups.ContainsKey(byName) ? byName : null;
    }

    private static DnsBackup? Find(Dictionary<string, DnsBackup> backups, string interfaceId, string interfaceName) =>
        FindKey(backups, interfaceId, interfaceName) is { } key ? backups[key] : null;

    private static bool IsValid(DnsBackup b) =>
        IsValidInterfaceName(b.InterfaceName) &&
        b.Servers is { Length: <= 8 } && b.Servers.All(IsIPv4) &&
        (b.Servers6 is null || (b.Servers6.Length <= 8 && b.Servers6.All(IsIPv6)));

    // ------------------------------------------------------------------ Changement de DNS

    /// <summary>
    /// Bascule l'interface active sur un DNS (IPv4, et IPv6 si l'interface l'utilise). L'état
    /// d'origine n'est sauvegardé qu'au PREMIER changement de cette interface : passer de
    /// Cloudflare à Google puis annuler doit ramener au DNS d'avant AETHER, pas à Cloudflare.
    /// </summary>
    public static Task<string> ApplyDnsAsync(string interfaceName, string interfaceId, bool ipv6Enabled,
                                             DnsCandidate target) => Task.Run(() =>
    {
        if (!StartupManager.IsElevated) return "Droits administrateur requis.";
        if (target.IsCurrent) return "C'est déjà le DNS configuré.";
        if (!IsValidInterfaceName(interfaceName)) return "Aucune interface active valide détectée.";
        if (!IsIPv4(target.Primary) || (target.Secondary.Length > 0 && !IsIPv4(target.Secondary)) ||
            (target.Primary6.Length > 0 && !IsIPv6(target.Primary6)) ||
            (target.Secondary6.Length > 0 && !IsIPv6(target.Secondary6)))
            return "Adresse de serveur DNS invalide.";

        var backups = ReadBackups();
        if (Find(backups, interfaceId, interfaceName) == null)
        {
            try
            {
                backups[interfaceId.Length > 0 ? interfaceId : NameKey(interfaceName)] =
                    ReadCurrentDns(interfaceName, interfaceId);
                WriteBackups(backups);
            }
            catch (Exception ex)
            {
                // Sans sauvegarde, le changement ne serait pas annulable : on refuse.
                Log.Error("Sauvegarde du DNS d'origine impossible.", ex);
                return $"Sauvegarde du DNS d'origine impossible, rien n'a été modifié. {ex.Message}";
            }
        }

        // Nom d'interface en argument positionnel : ArgumentList l'échappe comme un seul argument.
        var r = SetStatic("ipv4", interfaceName, new[] { target.Primary, target.Secondary });
        if (!r.Ok) return $"Échec du changement de DNS. {Tail(r.Output)}";

        string note = "";
        if (ipv6Enabled)
        {
            if (target.Primary6.Length > 0)
            {
                var r6 = SetStatic("ipv6", interfaceName, new[] { target.Primary6, target.Secondary6 });
                note = r6.Ok ? " (IPv4 et IPv6)" : $" — IPv6 inchangé : {Tail(r6.Output)}";
            }
            else
            {
                note = " — les DNS IPv6 d'origine restent actifs et peuvent encore être utilisés par Windows.";
            }
        }

        ProcessRunner.Run("ipconfig", new[] { "/flushdns" });
        Log.Audit($"DNS {target.Name} appliqué sur « {interfaceName} »{note}.");
        return $"DNS {target.Name} ({target.Primary}) appliqué sur « {interfaceName} »{note}. Annulable.";
    });

    /// <summary>Rétablit le DNS d'origine de l'interface indiquée (IPv4 et, si sauvegardé, IPv6).</summary>
    public static Task<string> RevertDnsAsync(string interfaceId, string interfaceName) => Task.Run(() =>
    {
        var key = FindKey(ReadBackups(), interfaceId, interfaceName);
        return key == null ? "Aucun changement de DNS à annuler sur cette interface." : RevertKey(key);
    });

    /// <summary>Rétablit une sauvegarde précise (onglet Historique, ligne de commande).</summary>
    public static Task<string> RevertDnsByKeyAsync(string key) => Task.Run(() => RevertKey(key));

    /// <summary>Toutes les sauvegardes DNS en place, toutes interfaces confondues.</summary>
    public static IReadOnlyList<(string Key, DnsBackup Backup)> ListDnsBackups() =>
        ReadBackups().Select(p => (p.Key, p.Value)).ToList();

    private static string RevertKey(string key)
    {
        if (!StartupManager.IsElevated) return "Droits administrateur requis.";

        var backups = ReadBackups();
        if (!backups.TryGetValue(key, out var backup)) return "Aucun changement de DNS à annuler.";

        var r = backup.Dhcp || backup.Servers.Length == 0
            ? SetDhcp("ipv4", backup.InterfaceName)
            : SetStatic("ipv4", backup.InterfaceName, backup.Servers);
        if (!r.Ok) return $"Échec de la restauration du DNS. {Tail(r.Output)}";

        if (backup.Dhcp6 is { } dhcp6)
        {
            var servers6 = backup.Servers6 ?? Array.Empty<string>();
            var r6 = dhcp6 || servers6.Length == 0
                ? SetDhcp("ipv6", backup.InterfaceName)
                : SetStatic("ipv6", backup.InterfaceName, servers6);
            if (!r6.Ok) return $"IPv4 rétabli, mais échec de la restauration IPv6 (sauvegarde conservée). {Tail(r6.Output)}";
        }

        backups.Remove(key);
        try { WriteBackups(backups); }
        catch (Exception ex) { Log.Warn("Mise à jour des sauvegardes DNS impossible.", ex); }

        ProcessRunner.Run("ipconfig", new[] { "/flushdns" });
        Log.Audit($"DNS d'origine rétabli sur « {backup.InterfaceName} ».");
        return backup.Dhcp
            ? $"DNS d'origine rétabli : attribué automatiquement (DHCP) sur « {backup.InterfaceName} »."
            : $"DNS d'origine rétabli : {string.Join(", ", backup.Servers)}.";
    }

    private static ProcessRunner.Result SetStatic(string family, string interfaceName, IEnumerable<string> servers)
    {
        var list = servers.Where(s => s.Length > 0).ToList();
        var r = ProcessRunner.Run("netsh", new[]
            { "interface", family, "set", "dnsservers", interfaceName, "static", list[0], "primary", "validate=no" });

        for (int i = 1; i < list.Count && r.Ok; i++)
            ProcessRunner.Run("netsh", new[]
                { "interface", family, "add", "dnsservers", interfaceName, list[i], $"index={i + 1}", "validate=no" });

        return r;
    }

    private static ProcessRunner.Result SetDhcp(string family, string interfaceName) =>
        ProcessRunner.Run("netsh", new[] { "interface", family, "set", "dnsservers", interfaceName, "dhcp" });

    /// <summary>
    /// Distingue un DNS fixé à la main d'un DNS reçu par DHCP. Les deux apparaissent
    /// identiques dans <c>GetIPProperties().DnsAddresses</c> ; seul le registre les sépare :
    /// <c>NameServer</c> n'est renseigné que pour une configuration statique.
    /// </summary>
    private static DnsBackup ReadCurrentDns(string interfaceName, string interfaceId)
    {
        var v4 = ReadStaticServers("Tcpip", interfaceId, IsIPv4);
        var v6 = ReadStaticServers("Tcpip6", interfaceId, IsIPv6);

        return new DnsBackup(interfaceName, v4.Length == 0, v4)
        {
            Dhcp6 = v6.Length == 0,
            Servers6 = v6
        };
    }

    private static string[] ReadStaticServers(string stack, string interfaceId, Func<string, bool> valid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{stack}\Parameters\Interfaces\{interfaceId}");
            var raw = key?.GetValue("NameServer") as string ?? "";
            return raw.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(valid).ToArray();
        }
        catch
        {
            // Registre illisible : DHCP est l'hypothèse la plus sûre, c'est le réglage par défaut.
            return Array.Empty<string>();
        }
    }

    // ------------------------------------------------------------------ Utilitaires

    internal static bool IsIPv4(string s) =>
        IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;

    internal static bool IsIPv6(string s) =>
        IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>Nom d'interface Windows plausible : ni guillemet, ni caractère de contrôle.</summary>
    internal static bool IsValidInterfaceName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 256 &&
        !name.Any(c => c == '"' || char.IsControl(c));

    /// <summary>Dernière ligne non vide d'une sortie console : c'est là que figure l'erreur.</summary>
    private static string Tail(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
}
