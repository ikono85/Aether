using System.IO;
using System.Text;

namespace Aether.Services.Infrastructure;

/// <summary>
/// Journal fichier minimal, sans dépendance : %LocalAppData%\Aether\logs\aether-AAAAMMJJ.log.
/// Un fichier par jour, 7 jours conservés, 5 Mo maximum par fichier (l'excédent bascule dans
/// un « .old »). Les lignes AUDIT tracent chaque modification système réellement effectuée.
/// Écrire dans le journal ne doit jamais faire tomber l'application : toute erreur est avalée.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private const int KeepDays = 7;
    private const long MaxBytes = 5 * 1024 * 1024;
    private static bool _pruned;

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aether", "logs");

    public static string CurrentFile => Path.Combine(Folder, $"aether-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /// <summary>Modification du système (registre, service, DNS, processus…).</summary>
    public static void Audit(string message) => Write("AUDIT", message, null);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                if (!_pruned) { _pruned = true; Prune(); }

                var path = CurrentFile;
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".old", overwrite: true);

                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
                    .Append(level.PadRight(5)).Append(" [")
                    .Append(Environment.CurrentManagedThreadId).Append("] ")
                    .Append(message);
                if (ex != null) line.AppendLine().Append(ex);
                line.AppendLine();

                File.AppendAllText(path, line.ToString(), Encoding.UTF8);
            }
        }
        catch { /* journal indisponible : sans conséquence pour l'application */ }
    }

    private static void Prune()
    {
        var cutoff = DateTime.Now.AddDays(-KeepDays);
        foreach (var file in Directory.EnumerateFiles(Folder, "aether-*.log*"))
        {
            try { if (File.GetLastWriteTime(file) < cutoff) File.Delete(file); } catch { }
        }
    }
}
