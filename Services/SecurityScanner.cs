using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using Aether.Models;

namespace Aether.Services;

/// <summary>
/// Analyse RÉELLE en lecture seule : énumère les processus en cours et les entrées de
/// démarrage Windows, vérifie la signature Authenticode et l'emplacement d'exécution.
/// Ne modifie, ne déplace ni ne supprime jamais rien.
/// </summary>
public static class SecurityScanner
{
    /// <summary>Une "unité" à analyser (chemin d'exe + contexte).</summary>
    public record Unit(string Name, string Path, string Category);

    /// <summary>Construit la liste de tout ce qui sera analysé (pour la progression).</summary>
    public static List<Unit> Collect()
    {
        var units = new List<Unit>();

        foreach (var p in Process.GetProcesses())
        {
            string path = "";
            try { path = p.MainModule?.FileName ?? ""; } catch { /* accès refusé = process système protégé */ }
            units.Add(new Unit(p.ProcessName, path, "Processus"));
        }

        foreach (var (name, path) in StartupEntries())
            units.Add(new Unit(name, path, "Démarrage"));

        return units;
    }

    /// <summary>Analyse une unité et renvoie un ScanItem si elle mérite d'être signalée, sinon null.</summary>
    public static ScanItem? Inspect(Unit u)
    {
        // Sans chemin exploitable (process système protégé) : considéré sûr, non listé.
        if (string.IsNullOrWhiteSpace(u.Path) || !File.Exists(u.Path)) return null;

        string lower = u.Path.ToLowerInvariant();
        bool inTemp = lower.Contains("\\temp\\") || lower.Contains("\\appdata\\local\\temp");
        bool inUserland = lower.Contains("\\appdata\\") || lower.Contains("\\downloads\\") ||
                          lower.Contains("\\users\\public\\");
        bool inWindows = lower.StartsWith(@"c:\windows\") ||
                         lower.Contains(@"\program files\") || lower.Contains(@"\program files (x86)\");

        // Validation réelle (embarquée OU catalogue Windows) via WinVerifyTrust.
        bool trusted = IsTrusted(u.Path);
        string publisher = PublisherName(u.Path);

        int risk = 0;
        string reason = "";

        // Les fichiers de Windows sont signés par catalogue : jamais signalés.
        if (inTemp && !inWindows)
        {
            risk = 2;
            reason = "S'exécute depuis un dossier temporaire — emplacement inhabituel pour un programme légitime.";
        }
        else if (!trusted && !inWindows)
        {
            risk = 1;
            reason = inUserland
                ? "Exécutable sans signature valide, lancé depuis un dossier utilisateur."
                : "Exécutable sans signature numérique valide.";
        }

        if (risk == 0) return null;   // sain : on ne l'affiche pas

        return new ScanItem
        {
            Name = u.Name,
            Path = u.Path,
            Category = u.Category,
            Publisher = trusted && publisher.Length > 0 ? publisher
                        : trusted ? "Signé (validé)" : "Éditeur inconnu (non signé)",
            Risk = risk,
            Reason = reason
        };
    }

    /// <summary>Nom de l'éditeur depuis la signature embarquée (best-effort).</summary>
    private static string PublisherName(string path)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch { return ""; }
    }

    // ---- WinVerifyTrust : valide les signatures embarquées ET par catalogue (.cat) ----
    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO { public uint cbStruct; public IntPtr pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData;
        public uint dwUIChoice; public uint fdwRevocationChecks; public uint dwUnionChoice;
        public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData;
        public IntPtr pwszURLReference; public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

    private static bool IsTrusted(string path)
    {
        const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1;
        const uint WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2, WTD_SAFER_FLAG = 0x100;

        IntPtr pPath = Marshal.StringToCoTaskMemUni(path);
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = pPath };
        IntPtr pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        Marshal.StructureToPtr(fileInfo, pFile, false);

        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = WTD_CHOICE_FILE,
            pFile = pFile,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_SAFER_FLAG
        };
        IntPtr pData = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_DATA>());
        Marshal.StructureToPtr(data, pData, false);

        try
        {
            uint result = WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);
            // Libère l'état interne.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(data, pData, true);
            WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);
            return result == 0; // S_OK = signature valide
        }
        catch { return true; /* en cas d'échec API, ne pas alarmer */ }
        finally
        {
            Marshal.FreeCoTaskMem(pPath);
            Marshal.FreeCoTaskMem(pFile);
            Marshal.FreeCoTaskMem(pData);
        }
    }

    /// <summary>Entrées de démarrage réelles (clés Run HKCU + HKLM).</summary>
    private static IEnumerable<(string Name, string Path)> StartupEntries()
    {
        var roots = new (RegistryKey Hive, string Sub)[]
        {
            (Registry.CurrentUser,  @"Software\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        };

        foreach (var (hive, sub) in roots)
        {
            RegistryKey? key = null;
            try { key = hive.OpenSubKey(sub); } catch { }
            if (key == null) continue;
            using (key)
            {
                foreach (var name in key.GetValueNames())
                {
                    var raw = key.GetValue(name)?.ToString() ?? "";
                    var exe = ExtractExe(raw);
                    if (!string.IsNullOrWhiteSpace(exe))
                        yield return (name, exe);
                }
            }
        }
    }

    /// <summary>Extrait le chemin de l'exe d'une ligne de commande de démarrage.</summary>
    private static string ExtractExe(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith("\""))
        {
            int end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd.Substring(1, end - 1) : cmd.Trim('"');
        }
        int space = cmd.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase);
        return space > 0 ? cmd.Substring(0, space + 4) : cmd;
    }
}
