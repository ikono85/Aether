using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Aether.Services.Infrastructure;

namespace Aether.Services;

/// <summary>
/// Lancement au démarrage de Windows via le Planificateur de tâches, et non la clé
/// HKCU\...\Run : Aether exige une élévation (voir app.manifest), et une entrée Run
/// déclencherait une invite UAC à chaque ouverture de session — ou serait simplement
/// bloquée. Une tâche « à l'ouverture de session » au niveau le plus élevé démarre
/// l'application élevée et sans invite. Créer ou supprimer cette tâche demande des droits
/// administrateur.
///
/// La tâche est décrite en XML : créée par « schtasks /SC ONLOGON », elle hériterait des
/// réglages par défaut (arrêt après 72 h, pas de démarrage sur batterie).
/// </summary>
public static class StartupManager
{
    private const string TaskName = "AetherAutoStart";

    public static bool IsElevated => Elevation.IsElevated;

    public static bool IsEnabled() => ProcessRunner.Run("schtasks", new[] { "/Query", "/TN", TaskName }, 10_000).Ok;

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

            // Une tâche élevée sans invite exécute ce qui se trouve à ce chemin : si un programme
            // non élevé peut le remplacer (Bureau, Téléchargements…), c'est une élévation gratuite.
            if (!AppPaths.IsAdminOnlyWritable(exe, out var reason))
            {
                message = $"Démarrage automatique refusé : {reason}. Installez AETHER dans « Program Files » " +
                          "pour l'activer sans exposer une élévation de privilèges.";
                Log.Warn(message);
                return IsEnabled();
            }

            string user;
            using (var id = WindowsIdentity.GetCurrent()) user = id.Name;

            var xmlPath = Path.Combine(AppPaths.SecureDataDir, "autostart-task.xml");
            try
            {
                File.WriteAllText(xmlPath, BuildTaskXml(exe, user), Encoding.Unicode);
                var r = ProcessRunner.Run("schtasks", new[] { "/Create", "/F", "/TN", TaskName, "/XML", xmlPath }, 15_000);

                message = r.Ok
                    ? "Aether démarrera automatiquement à l'ouverture de session."
                    : $"Échec de la création de la tâche planifiée. {r.Output}".Trim();

                if (r.Ok) Log.Audit($"Tâche {TaskName} créée pour {user} → {exe}");
                else Log.Warn(message);
                return r.Ok;
            }
            catch (Exception ex)
            {
                message = $"Échec de la création de la tâche planifiée : {ex.Message}";
                Log.Error(message, ex);
                return IsEnabled();
            }
            finally
            {
                try { File.Delete(xmlPath); } catch { }
            }
        }

        var del = ProcessRunner.Run("schtasks", new[] { "/Delete", "/F", "/TN", TaskName }, 15_000);
        message = del.Ok
            ? "Démarrage automatique désactivé."
            : $"Échec de la suppression de la tâche planifiée. {del.Output}".Trim();
        if (del.Ok) Log.Audit($"Tâche {TaskName} supprimée.");
        return !del.Ok && IsEnabled();
    }

    private static string BuildTaskXml(string exe, string user)
    {
        string e(string s) => SecurityElement.Escape(s) ?? "";

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Lance AETHER à l'ouverture de session.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{e(user)}</UserId></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{e(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{e(exe)}</Command></Exec>
              </Actions>
            </Task>
            """;
    }
}
