using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Aether.Services.Optimization;

/// <summary>
/// Utilisateur réellement connecté à la session Windows.
///
/// Sur un compte standard, l'invite UAC demande les identifiants d'un autre compte
/// (administrateur) : AETHER tourne alors sous ce compte, et %TEMP% ou %LOCALAPPDATA%
/// désignent ses dossiers à lui, pas ceux de la personne devant l'écran.
/// </summary>
public static class SessionUser
{
    private record Info(string Sid, string ProfilePath, string TempPath);

    private static readonly Lazy<Info?> _info = new(Resolve);

    /// <summary>Dossier temporaire de l'utilisateur de la session, null s'il est introuvable.</summary>
    public static string? TempPath => _info.Value?.TempPath;

    /// <summary>%LOCALAPPDATA% de l'utilisateur de la session, null s'il est introuvable.</summary>
    public static string? LocalAppDataPath =>
        _info.Value is { } i ? Path.Combine(i.ProfilePath, "AppData", "Local") : null;

    /// <summary>Vrai si AETHER tourne sous un autre compte que celui de la session.</summary>
    public static bool IsDifferentAccount
    {
        get
        {
            if (_info.Value is not { } info) return false;
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return !string.Equals(info.Sid, id.User?.Value, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    private static Info? Resolve()
    {
        try
        {
            string? user = Query(WTSUserName);
            string? domain = Query(WTSDomainName);
            if (string.IsNullOrEmpty(user)) return null;

            var account = string.IsNullOrEmpty(domain) ? new NTAccount(user) : new NTAccount(domain, user);
            var sid = account.Translate(typeof(SecurityIdentifier)).Value;

            using var profileKey = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
            if (profileKey?.GetValue("ProfileImagePath") is not string profile || profile.Length == 0) return null;
            profile = Environment.ExpandEnvironmentVariables(profile);

            var local = Path.Combine(profile, "AppData", "Local");
            var temp = Path.Combine(local, "Temp");

            // TEMP personnalisé : ses variables propres à l'utilisateur ne doivent pas être
            // résolues avec celles du compte élevé.
            using var env = Registry.Users.OpenSubKey($@"{sid}\Environment");
            if (env?.GetValue("TEMP", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string raw && raw.Length > 0)
            {
                var resolved = Environment.ExpandEnvironmentVariables(raw
                    .Replace("%USERPROFILE%", profile, StringComparison.OrdinalIgnoreCase)
                    .Replace("%LOCALAPPDATA%", local, StringComparison.OrdinalIgnoreCase));
                if (Path.IsPathRooted(resolved)) temp = resolved;
            }

            return new Info(sid, profile, temp);
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- Win32

    private const int WTSUserName = 5;
    private const int WTSDomainName = 7;
    private static readonly IntPtr CurrentServer = IntPtr.Zero;
    private const int CurrentSession = -1;

    private static string? Query(int infoClass)
    {
        if (!WTSQuerySessionInformation(CurrentServer, CurrentSession, infoClass, out var buffer, out _))
            return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass,
                                                          out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
