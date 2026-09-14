using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Aether.Models;

namespace Aether.Services;

/// <summary>
/// Connexions TCP ouvertes, lues directement dans la table de la pile Windows via
/// <c>GetExtendedTcpTable</c> (IP Helper) — la même source que <c>netstat -ano</c>, mais
/// sans lancer de processus externe ni parser sa sortie localisée.
///
/// La table donne le PID propriétaire de chaque connexion : c'est ce qui permet de dire
/// QUEL programme parle à QUELLE machine, ce qu'aucune autre mesure de l'application ne fait.
/// </summary>
public class ConnectionService
{
    public ObservableCollection<ActiveConnection> Connections { get; } = new();

    /// <summary>Dernière erreur rencontrée, affichable telle quelle. Vide si tout va bien.</summary>
    public string LastError { get; private set; } = "";

    /// <summary>Cache des résolutions inverses : une IP n'est interrogée qu'une fois par session.</summary>
    private readonly ConcurrentDictionary<string, string> _ptrCache = new();
    private readonly HashSet<string> _ptrPending = new();

    /// <summary>
    /// Relit la table et met à jour la collection en place : les connexions inchangées
    /// gardent leur instance, donc la sélection de l'utilisateur et les noms déjà résolus
    /// survivent au rafraîchissement.
    /// </summary>
    public void Refresh()
    {
        List<ActiveConnection> fresh;
        try
        {
            fresh = ReadTcpTable();
            LastError = "";
        }
        catch (Exception ex)
        {
            LastError = $"Lecture de la table TCP impossible : {ex.Message}";
            return;
        }

        var byKey = Connections.ToDictionary(c => c.Key);
        var seen = new HashSet<string>();

        foreach (var c in fresh)
        {
            seen.Add(c.Key);
            if (byKey.ContainsKey(c.Key)) continue;

            if (_ptrCache.TryGetValue(c.RemoteAddress, out var name)) c.RemoteHost = name;
            Connections.Add(c);
            QueueReverseLookup(c);
        }

        for (int i = Connections.Count - 1; i >= 0; i--)
            if (!seen.Contains(Connections[i].Key))
                Connections.RemoveAt(i);
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
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var conn in Connections)
                    if (conn.RemoteAddress == ip) conn.RemoteHost = name;
            });
        });
    }

    private static bool IsLocal(string ip) =>
        ip.StartsWith("127.") || ip.StartsWith("10.") || ip.StartsWith("192.168.") ||
        ip == "0.0.0.0" || ip.StartsWith("169.254.") ||
        (ip.StartsWith("172.") && int.TryParse(ip.Split('.')[1], out var b) && b >= 16 && b <= 31);

    /// <summary>Termine le processus propriétaire d'une connexion. Retourne le message à afficher.</summary>
    public static string KillProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName;
            p.Kill();
            return $"Processus {name} (PID {pid}) terminé.";
        }
        catch (Exception ex)
        {
            return $"Impossible de terminer le PID {pid} : {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ IP Helper

    private const int AF_INET = 2;

    /// <summary>TCP_TABLE_OWNER_PID_ALL : toutes les connexions, avec le PID propriétaire.</summary>
    private const int TCP_TABLE_OWNER_PID_ALL = 5;

    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder,
                                                   int ulAf, int tableClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    private static List<ActiveConnection> ReadTcpTable()
    {
        var result = new List<ActiveConnection>();
        int size = 0;

        uint ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (ret != ERROR_INSUFFICIENT_BUFFER && ret != 0)
            throw new InvalidOperationException($"GetExtendedTcpTable a retourné {ret}.");

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            ret = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != 0) throw new InvalidOperationException($"GetExtendedTcpTable a retourné {ret}.");

            int rows = Marshal.ReadInt32(buffer);
            var cursor = IntPtr.Add(buffer, 4);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();

            var names = ProcessNames();

            for (int i = 0; i < rows; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(cursor);
                cursor = IntPtr.Add(cursor, rowSize);

                int pid = (int)row.OwningPid;
                result.Add(new ActiveConnection
                {
                    Pid = pid,
                    ProcessName = names.TryGetValue(pid, out var n) ? n : $"PID {pid}",
                    LocalAddress = new IPAddress(row.LocalAddr).ToString(),
                    LocalPort = DecodePort(row.LocalPort),
                    RemoteAddress = new IPAddress(row.RemoteAddr).ToString(),
                    RemotePort = DecodePort(row.RemotePort),
                    State = StateName(row.State)
                });
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }

        return result;
    }

    /// <summary>
    /// Le port occupe 4 octets mais n'en utilise que deux, en ordre réseau (gros-boutiste) :
    /// les lire tels quels donnerait des numéros de port fantaisistes.
    /// </summary>
    private static int DecodePort(uint raw)
    {
        var b = BitConverter.GetBytes(raw);
        return (b[0] << 8) + b[1];
    }

    private static Dictionary<int, string> ProcessNames()
    {
        var map = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            try { map[p.Id] = p.ProcessName; } catch { }
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
