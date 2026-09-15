using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Aether.Models;
using Aether.Services.Infrastructure;

namespace Aether.Services;

/// <summary>
/// Connexions TCP ouvertes (IPv4 et IPv6), lues directement dans la table de la pile Windows
/// via <c>GetExtendedTcpTable</c> (IP Helper) — la même source que <c>netstat -ano</c>, mais
/// sans lancer de processus externe ni parser sa sortie localisée.
///
/// La lecture des tables et l'énumération des processus se font sur un thread de fond ;
/// seule la fusion dans la collection liée à l'interface revient sur le Dispatcher.
/// </summary>
public class ConnectionService
{
    public ObservableCollection<ActiveConnection> Connections { get; } = new();

    /// <summary>Dernière erreur rencontrée, affichable telle quelle. Vide si tout va bien.</summary>
    public string LastError { get; private set; } = "";

    /// <summary>Cache des résolutions inverses : une IP n'est interrogée qu'une fois par session.</summary>
    private readonly ConcurrentDictionary<string, string> _ptrCache = new();
    private readonly HashSet<string> _ptrPending = new();
    private bool _refreshing;

    /// <summary>
    /// Relit les tables et met à jour la collection en place : les connexions inchangées
    /// gardent leur instance, donc la sélection de l'utilisateur et les noms déjà résolus
    /// survivent au rafraîchissement. À appeler depuis le thread d'interface.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            List<ActiveConnection> fresh;
            try
            {
                fresh = await Task.Run(ReadTables);
                LastError = "";
            }
            catch (Exception ex)
            {
                LastError = $"Lecture de la table TCP impossible : {ex.Message}";
                Log.Warn(LastError, ex);
                return;
            }

            // La table peut contenir deux lignes de même clé (sockets en écoute partagée).
            var existing = new HashSet<string>(Connections.Select(c => c.Key));
            var seen = new HashSet<string>();

            foreach (var c in fresh)
            {
                if (!seen.Add(c.Key)) continue;
                if (existing.Contains(c.Key)) continue;

                if (_ptrCache.TryGetValue(c.RemoteAddress, out var name)) c.RemoteHost = name;
                Connections.Add(c);
                QueueReverseLookup(c);
            }

            for (int i = Connections.Count - 1; i >= 0; i--)
                if (!seen.Contains(Connections[i].Key))
                    Connections.RemoveAt(i);
        }
        finally { _refreshing = false; }
    }

    /// <summary>
    /// Résolution inverse en arrière-plan. Elle peut prendre plusieurs secondes et échouer
    /// souvent : elle ne doit jamais retarder l'affichage de la table.
    /// </summary>
    private void QueueReverseLookup(ActiveConnection c)
    {
        var ip = c.RemoteAddress;
        if (_ptrCache.ContainsKey(ip) || IsLocal(ip)) return;

        lock (_ptrPending)
        {
            if (!_ptrPending.Add(ip)) return;
        }

        _ = Task.Run(async () =>
        {
            string name = "";
            try
            {
                var entry = await Dns.GetHostEntryAsync(ip);
                if (!string.IsNullOrWhiteSpace(entry.HostName) && entry.HostName != ip) name = entry.HostName;
            }
            catch { /* pas de PTR : on garde l'IP */ }

            _ptrCache[ip] = name;
            lock (_ptrPending) { _ptrPending.Remove(ip); }

            if (name.Length == 0) return;

            // Retour sur le thread d'interface : la collection est liée à la vue.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                foreach (var conn in Connections)
                    if (conn.RemoteAddress == ip) conn.RemoteHost = name;
            });
        });
    }

    /// <summary>Adresse locale, privée ou non routable : inutile de chercher son nom sur Internet.</summary>
    internal static bool IsLocal(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 169 && b[1] == 254) ||
                   (b[0] == 100 && b[1] >= 64 && b[1] <= 127);   // CGNAT
        }

        // IPv6 : lien local, site local (obsolète) et adresses uniques locales fc00::/7.
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }

    // ------------------------------------------------------------------ Terminaison

    /// <summary>Processus dont la terminaison rendrait Windows instable ou le planterait.</summary>
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "lsaiso", "svchost", "dwm", "fontdrvhost", "Memory Compression", "MsMpEng", "MsSense", "SecurityHealthService"
    };

    /// <summary>
    /// Termine le processus propriétaire d'une connexion. Vérifie d'abord que le PID désigne
    /// toujours le même processus (un PID est réutilisé dès qu'un processus se termine) et
    /// refuse les processus système critiques. Retourne le message à afficher.
    /// </summary>
    public static string KillProcess(ActiveConnection c)
    {
        if (c.Pid <= 4) return "Processus système : AETHER refuse de le terminer.";
        if (c.Pid == Environment.ProcessId) return "AETHER ne peut pas se terminer depuis cette liste.";

        try
        {
            using var p = Process.GetProcessById(c.Pid);
            var name = p.ProcessName;

            bool sameName = name.Equals(c.ProcessName, StringComparison.OrdinalIgnoreCase);
            var start = TryStartTime(p);
            bool sameStart = c.ProcessStartTime is null || start is null || start == c.ProcessStartTime;
            if (!sameName || !sameStart)
                return $"Le PID {c.Pid} désigne désormais un autre processus ({name}) : rien n'a été terminé. Actualisez la liste.";

            if (ProtectedProcesses.Contains(name))
                return $"{name} est un processus système critique : AETHER refuse de le terminer.";

            p.Kill();
            Log.Audit($"Processus {name} (PID {c.Pid}) terminé par l'utilisateur.");
            return $"Processus {name} (PID {c.Pid}) terminé.";
        }
        catch (ArgumentException)
        {
            return $"Le processus {c.ProcessName} (PID {c.Pid}) est déjà terminé.";
        }
        catch (Exception ex)
        {
            Log.Warn($"Terminaison du PID {c.Pid} impossible.", ex);
            return $"Impossible de terminer le PID {c.Pid} : {ex.Message}";
        }
    }

    private static DateTime? TryStartTime(Process p)
    {
        try { return p.StartTime; } catch { return null; }   // processus protégé
    }

    // ------------------------------------------------------------------ IP Helper

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;

    /// <summary>TCP_TABLE_OWNER_PID_ALL : toutes les connexions, avec le PID propriétaire.</summary>
    private const int TCP_TABLE_OWNER_PID_ALL = 5;

    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>MIB_TCPROW_OWNER_PID : 6 DWORD.</summary>
    private const int Row4Size = 24;

    /// <summary>MIB_TCP6ROW_OWNER_PID : adresse(16) + scope + port, ×2, puis état et PID.</summary>
    private const int Row6Size = 56;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder,
                                                   int ulAf, int tableClass, int reserved);

    private static List<ActiveConnection> ReadTables()
    {
        var processes = ProcessInfos();
        var result = ReadTable(AF_INET, processes);

        // L'échec IPv6 (pile désactivée) ne doit pas masquer les connexions IPv4.
        try { result.AddRange(ReadTable(AF_INET6, processes)); }
        catch (Exception ex) { Log.Info($"Table TCP IPv6 indisponible : {ex.Message}"); }

        return result;
    }

    private static List<ActiveConnection> ReadTable(int family, Dictionary<int, (string Name, DateTime? Start)> processes)
    {
        var result = new List<ActiveConnection>();
        int size = 0;

        uint ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
        if (ret != ERROR_INSUFFICIENT_BUFFER && ret != 0)
            throw new InvalidOperationException($"GetExtendedTcpTable a retourné {ret}.");

        // La table peut grossir entre les deux appels : marge de sécurité.
        size += 16 * Row6Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            ret = GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != 0) throw new InvalidOperationException($"GetExtendedTcpTable a retourné {ret}.");

            int rows = Marshal.ReadInt32(buffer);
            int rowSize = family == AF_INET ? Row4Size : Row6Size;

            for (int i = 0; i < rows; i++)
            {
                var row = IntPtr.Add(buffer, 4 + i * rowSize);
                result.Add(family == AF_INET ? ParseRow4(row, processes) : ParseRow6(row, processes));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }

        return result;
    }

    private static ActiveConnection ParseRow4(IntPtr row, Dictionary<int, (string Name, DateTime? Start)> processes)
    {
        uint state = (uint)Marshal.ReadInt32(row, 0);
        uint localAddr = (uint)Marshal.ReadInt32(row, 4);
        uint localPort = (uint)Marshal.ReadInt32(row, 8);
        uint remoteAddr = (uint)Marshal.ReadInt32(row, 12);
        uint remotePort = (uint)Marshal.ReadInt32(row, 16);
        int pid = Marshal.ReadInt32(row, 20);

        return Build(pid, processes, new IPAddress(localAddr), localPort, new IPAddress(remoteAddr), remotePort, state);
    }

    private static ActiveConnection ParseRow6(IntPtr row, Dictionary<int, (string Name, DateTime? Start)> processes)
    {
        var localBytes = new byte[16];
        var remoteBytes = new byte[16];
        Marshal.Copy(row, localBytes, 0, 16);
        uint localScope = (uint)Marshal.ReadInt32(row, 16);
        uint localPort = (uint)Marshal.ReadInt32(row, 20);
        Marshal.Copy(IntPtr.Add(row, 24), remoteBytes, 0, 16);
        uint remoteScope = (uint)Marshal.ReadInt32(row, 40);
        uint remotePort = (uint)Marshal.ReadInt32(row, 44);
        uint state = (uint)Marshal.ReadInt32(row, 48);
        int pid = Marshal.ReadInt32(row, 52);

        return Build(pid, processes,
            new IPAddress(localBytes, localScope), localPort,
            new IPAddress(remoteBytes, remoteScope), remotePort, state);
    }

    private static ActiveConnection Build(int pid, Dictionary<int, (string Name, DateTime? Start)> processes,
                                          IPAddress local, uint localPort, IPAddress remote, uint remotePort, uint state)
    {
        var known = processes.TryGetValue(pid, out var info);
        return new ActiveConnection
        {
            Pid = pid,
            ProcessName = known ? info.Name : $"PID {pid}",
            ProcessStartTime = known ? info.Start : null,
            LocalAddress = local.ToString(),
            LocalPort = DecodePort(localPort),
            RemoteAddress = remote.ToString(),
            RemotePort = DecodePort(remotePort),
            State = StateName(state)
        };
    }

    /// <summary>
    /// Le port occupe 4 octets mais n'en utilise que deux, en ordre réseau (gros-boutiste) :
    /// les lire tels quels donnerait des numéros de port fantaisistes.
    /// </summary>
    internal static int DecodePort(uint raw)
    {
        var b = BitConverter.GetBytes(raw);
        return (b[0] << 8) + b[1];
    }

    private static Dictionary<int, (string Name, DateTime? Start)> ProcessInfos()
    {
        var map = new Dictionary<int, (string, DateTime?)>();
        foreach (var p in Process.GetProcesses())
        {
            try { map[p.Id] = (p.ProcessName, TryStartTime(p)); }
            catch { }
            finally { p.Dispose(); }
        }
        return map;
    }

    private static string StateName(uint state) => state switch
    {
        1 => "CLOSED",
        2 => "LISTEN",
        3 => "SYN_SENT",
        4 => "SYN_RCVD",
        5 => "ESTABLISHED",
        6 => "FIN_WAIT1",
        7 => "FIN_WAIT2",
        8 => "CLOSE_WAIT",
        9 => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _ => "?"
    };
}
