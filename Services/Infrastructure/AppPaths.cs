using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Aether.Services.Infrastructure;

/// <summary>
/// Emplacements des données.
///
/// - <see cref="UserDataDir"/> (%AppData%\Aether) : préférences d'interface, sans conséquence
///   sur le système.
/// - <see cref="SecureDataDir"/> (%ProgramData%\Aether, accès réservé aux Administrateurs et à
///   SYSTEM) : journaux de restauration. AETHER les RELIT EN ADMINISTRATEUR pour écrire dans HKLM,
///   configurer des services ou lancer netsh : s'ils restaient dans %AppData%, n'importe quel
///   programme non élevé de l'utilisateur pourrait y glisser une valeur que AETHER appliquerait
///   ensuite avec ses droits.
/// </summary>
public static class AppPaths
{
    public static string UserDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aether");

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const string CreatorOwnerSid = "S-1-3-0";

    private static readonly Lazy<string> _secureDir = new(CreateSecureDir);

    public static string SecureDataDir => _secureDir.Value;

    /// <summary>Chemin d'un fichier protégé, avec migration unique depuis l'ancien emplacement.</summary>
    public static string SecureFile(string fileName)
    {
        var target = Path.Combine(SecureDataDir, fileName);
        MigrateLegacy(Path.Combine(UserDataDir, fileName), target);
        return target;
    }

    private static string CreateSecureDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Aether");
        try
        {
            // %ProgramData% autorise tout utilisateur à créer un dossier : un dossier « Aether »
            // préexistant qui n'appartient pas aux administrateurs a pu être préparé par un tiers.
            if (Directory.Exists(dir) && !HasTrustedOwner(new DirectoryInfo(dir).GetAccessControl()))
            {
                var aside = $"{dir}.untrusted-{DateTime.Now:yyyyMMddHHmmss}";
                Directory.Move(dir, aside);
                Log.Warn($"Dossier de données au propriétaire non fiable, écarté vers {aside}.");
            }

            var info = Directory.CreateDirectory(dir);
            var security = new DirectorySecurity();
            security.SetOwner(Administrators);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { Administrators, LocalSystem })
            {
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }
            info.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Error($"Sécurisation du dossier {dir} impossible.", ex);
        }
        return dir;
    }

    private static void MigrateLegacy(string legacy, string target)
    {
        try
        {
            if (File.Exists(target) || !File.Exists(legacy)) return;

            // Contenu d'origine non fiable : chaque module le revalide (liste blanche) avant usage.
            File.Copy(legacy, target);
            File.Move(legacy, legacy + ".migrated", overwrite: true);
            Log.Info($"{Path.GetFileName(legacy)} migré vers {SecureDataDir}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Migration de {legacy} impossible.", ex);
        }
    }

    // ------------------------------------------------------------------ Contrôle d'emplacement

    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    /// <summary>
    /// Vrai si l'exécutable, son dossier et les dossiers parents (hors racine du lecteur) ne sont
    /// modifiables que par les administrateurs. Condition pour lancer AETHER élevé sans invite
    /// UAC : sinon un programme non élevé pourrait remplacer l'exécutable ou une DLL voisine.
    /// </summary>
    public static bool IsAdminOnlyWritable(string exePath, out string reason)
    {
        try
        {
            if (!CheckPath(exePath, isFile: true, out reason)) return false;

            for (var dir = new DirectoryInfo(Path.GetDirectoryName(exePath)!); dir?.Parent != null; dir = dir.Parent)
            {
                if (!CheckPath(dir.FullName, isFile: false, out reason)) return false;
            }

            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"droits d'accès illisibles ({ex.Message})";
            return false;
        }
    }

    private static bool CheckPath(string path, bool isFile, out string reason)
    {
        FileSystemSecurity security = isFile
            ? new FileInfo(path).GetAccessControl()
            : new DirectoryInfo(path).GetAccessControl();

        if (!HasTrustedOwner(security))
        {
            reason = $"« {path} » n'appartient pas aux administrateurs";
            return false;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;

            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (IsTrusted(sid) || sid.Value == CreatorOwnerSid) continue;

            if ((rule.FileSystemRights & WriteRights) != 0)
            {
                reason = $"« {path} » est modifiable sans droits administrateur";
                return false;
            }
        }

        reason = "";
        return true;
    }

    private static bool HasTrustedOwner(FileSystemSecurity security) =>
        security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && IsTrusted(owner);

    private static bool IsTrusted(SecurityIdentifier sid) =>
        sid == Administrators || sid == LocalSystem || sid.Value == TrustedInstallerSid;
}
