using System.Diagnostics;
using System.Security.Principal;

namespace Aether.Services;

/// <summary>
/// Lancement au démarrage de Windows via le Planificateur de tâches, et non la clé
/// HKCU\...\Run : Aether exige une élévation (voir app.manifest), et une entrée Run
/// déclencherait une invite UAC à chaque ouverture de session — ou serait simplement
/// bloquée. Une tâche « ONLOGON » avec /RL HIGHEST démarre l'application élevée et
/// sans invite. Créer ou supprimer cette tâche demande des droits administrateur.
/// </summary>
public static class StartupManager
{
    private const string TaskName = "AetherAutoStart";

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

    public static bool IsEnabled() => Run($"/Query /TN \"{TaskName}\"").code == 0;

    /// <summary>Active ou désactive le démarrage automatique. Retourne l'état réellement obtenu.</summary>
    public static bool Set(bool enabled, out string message)
    {
        if (!IsElevated)
        {
            message = "Droits administrateur requis pour modifier le démarrage automatique.";
            return IsEnabled();
        }

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                message = "Chemin de l'exécutable introuvable.";
                return false;
            }

            var (code, err) = Run($"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST");
            message = code == 0
                ? "Aether démarrera automatiquement à l'ouverture de session."
                : $"Échec de la création de la tâche planifiée. {err}".Trim();
            return code == 0;
        }

        var (delCode, delErr) = Run($"/Delete /F /TN \"{TaskName}\"");
        message = delCode == 0
            ? "Démarrage automatique désactivé."
            : $"Échec de la suppression de la tâche planifiée. {delErr}".Trim();
        return delCode != 0 && IsEnabled();
    }

    private static (int code, string err) Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p == null) return (-1, "Processus schtasks non démarré.");
            var err = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.ExitCode, err.Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
