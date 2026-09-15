using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace Aether.Services.Infrastructure;

/// <summary>Vérification unique des droits administrateur.</summary>
public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}

/// <summary>
/// Lancement unique des utilitaires console (powercfg, netsh, schtasks, sc, defrag…).
///
/// - Les arguments passent par <see cref="ProcessStartInfo.ArgumentList"/> : chaque argument est
///   échappé séparément, un nom d'interface ou de tâche ne peut donc pas en injecter d'autres.
/// - Les exécutables système sont résolus dans System32 : l'application tourne en administrateur,
///   un « netsh.exe » placé dans un dossier du PATH ne doit pas être exécuté à sa place.
/// - stdout et stderr sont lus en parallèle (pas d'interblocage), le délai est réellement
///   appliqué et l'annulation tue l'arbre de processus.
/// - La sortie est décodée dans la page de code OEM, celle qu'emploient ces outils.
/// </summary>
public static class ProcessRunner
{
    public readonly record struct Result(int Code, string Output)
    {
        public bool Ok => Code == 0;
    }

    private static readonly Encoding ConsoleEncoding = ResolveOemEncoding();

    private static Encoding ResolveOemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch { return Encoding.UTF8; }
    }

    public static Result Run(string exe, IEnumerable<string> args, int timeoutMs = 30_000,
                             CancellationToken ct = default)
    {
        var argList = args.ToList();
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo(ResolveExecutable(exe))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = ConsoleEncoding,
                StandardErrorEncoding = ConsoleEncoding
            };
            foreach (var a in argList) psi.ArgumentList.Add(a);

            process = Process.Start(psi);
            if (process == null) return new Result(-1, "Processus non démarré.");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            var p = process;
            using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
            {
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    Log.Warn($"Délai dépassé ({timeoutMs} ms) : {exe} {string.Join(' ', argList)}");
                    return new Result(-1, "Délai dépassé.");
                }
                process.WaitForExit();   // vide les tampons de sortie asynchrones
            }

            ct.ThrowIfCancellationRequested();
            return new Result(process.ExitCode, (stdout.Result + " " + stderr.Result).Trim());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Échec du lancement de {exe}", ex);
            return new Result(-1, ex.Message);
        }
        finally { process?.Dispose(); }
    }

    /// <summary>Chemin complet d'un utilitaire Windows ; tout autre nom est laissé tel quel.</summary>
    private static string ResolveExecutable(string exe)
    {
        if (Path.IsPathRooted(exe)) return exe;

        var system = Environment.SystemDirectory;
        if (exe.Equals("powershell", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");

        var name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe";
        var candidate = Path.Combine(system, name);
        return File.Exists(candidate) ? candidate : exe;
    }
}
