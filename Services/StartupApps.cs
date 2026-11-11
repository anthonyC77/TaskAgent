using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TaskAgent.Services;

/// <summary>Un programme lancé par Windows à l'ouverture de session.</summary>
public class StartupEntry
{
    public string Name { get; init; } = "";          // nom de la valeur de registre ou du raccourci (clé technique)
    public string DisplayName { get; init; } = "";
    public string? Publisher { get; init; }
    public string Command { get; init; } = "";
    public string? ExePath { get; init; }
    public string SourceLabel { get; init; } = "";
    public bool MachineWide { get; init; }           // HKLM / dossier commun : modifier demande les droits administrateur
    public string ApprovedKey { get; init; } = "";   // sous-clé StartupApproved qui porte l'état activé/désactivé
    public bool Enabled { get; set; }
    public string? Tip { get; init; }

    /// <summary>Nom de processus probable une fois lancé (ex. « Discord »), pour retrouver sa RAM actuelle.</summary>
    public string? ProcessName => ExePath is null ? null : Path.GetFileNameWithoutExtension(ExePath);
}

/// <summary>
/// Programmes au démarrage, désactivés exactement comme le fait l'onglet « Démarrage » du Gestionnaire des tâches :
/// on ne supprime rien, on écrit un drapeau « désactivé » dans Explorer\StartupApproved. Réversible à tout moment.
/// Analogie : on ne jette pas le réveil, on coupe juste sa sonnerie.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartupApps
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    private static readonly Dictionary<string, string> Tips = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SecurityHealth"] = "Icône de Windows Sécurité : garde-la, elle est minuscule et te prévient en cas de menace.",
        ["SecurityHealthSystray"] = "Icône de Windows Sécurité : garde-la, elle est minuscule et te prévient en cas de menace.",
        ["OneDrive"] = "À garder si tu synchronises des fichiers avec OneDrive ; sinon, désactive-le sans risque.",
        ["Discord"] = "Se relance tout seul quand tu l'ouvres : rien ne se perd à le désactiver ici.",
        ["Steam"] = "Steam démarre en quelques secondes à la demande : inutile qu'il tourne en permanence.",
        ["EpicGamesLauncher"] = "Le lanceur Epic consomme en arrière-plan : à ouvrir seulement quand tu joues.",
        ["Spotify"] = "Spotify s'ouvre vite quand tu en as besoin : désactivable sans risque.",
        ["Teams"] = "Teams est lourd en arrière-plan : désactive-le si tu ne reçois pas d'appels en continu.",
        ["msteams"] = "Teams est lourd en arrière-plan : désactive-le si tu ne reçois pas d'appels en continu.",
        ["Docker Desktop"] = "Docker démarre aussi WSL (vmmem, souvent 2 Go+) : à lancer seulement quand tu développes.",
        ["TaskAgent"] = "C'est cette appli. Son lancement au démarrage se règle aussi via LaunchAtStartup dans settings.json.",
    };

    /// <summary>Toutes les entrées, triées : activées d'abord, puis par nom.</summary>
    public static List<StartupEntry> List()
    {
        var list = new List<StartupEntry>();
        ReadRunKey(list, Registry.CurrentUser, Run, "Registre (utilisateur)", machine: false, approved: "Run");
        ReadRunKey(list, Registry.LocalMachine, Run, "Registre (tous les utilisateurs)", machine: true, approved: "Run");
        ReadRunKey(list, Registry.LocalMachine, Run32, "Registre 32 bits (tous les utilisateurs)", machine: true, approved: "Run32");
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Dossier Démarrage (utilisateur)", machine: false);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Dossier Démarrage (tous les utilisateurs)", machine: true);
        return list.OrderByDescending(e => e.Enabled).ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Active/désactive. Renvoie null si c'est fait, sinon un message d'erreur.
    /// Pour une entrée « tous les utilisateurs », Windows demande l'autorisation administrateur (fenêtre UAC).
    /// </summary>
    public static async Task<string?> SetEnabledAsync(StartupEntry e, bool enabled)
    {
        var data = Flag(enabled);
        var hive = e.MachineWide ? Registry.LocalMachine : Registry.CurrentUser;
        try
        {
            using var key = hive.CreateSubKey(Approved + e.ApprovedKey);
            key.SetValue(e.Name, data, RegistryValueKind.Binary);
            e.Enabled = enabled;
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (!e.MachineWide) return "Accès refusé au registre : " + ex.Message;
        }

        // Entrée machine sans droits admin : on passe par reg.exe élevé (une seule fenêtre UAC).
        var args = $"add \"HKLM\\{Approved}{e.ApprovedKey}\" /v \"{e.Name}\" /t REG_BINARY /d {Convert.ToHexString(data)} /f";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("reg.exe", args)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            });
            if (p is null) return "Impossible de lancer reg.exe.";
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) return $"reg.exe a échoué (code {p.ExitCode}).";
            e.Enabled = enabled;
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Autorisation administrateur refusée : rien n'a été modifié.";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Ouvre la page Paramètres › Applications › Démarrage (applis du Microsoft Store en plus).</summary>
    public static void OpenWindowsSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); } catch { }
    }

    // ---------------- Détails techniques ----------------

    /// <summary>Format du Gestionnaire des tâches : 12 octets, 1er octet pair = activé, impair = désactivé (+ date de désactivation).</summary>
    private static byte[] Flag(bool enabled)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        return data;
    }

    private static bool IsEnabled(RegistryKey hive, string approvedKey, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(Approved + approvedKey);
            return key?.GetValue(name) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;
        }
        catch { return true; }
    }

    private static void ReadRunKey(List<StartupEntry> list, RegistryKey hive, string path, string label, bool machine, string approved)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name) || key.GetValue(name) is not string cmd || cmd.Length == 0) continue;
                list.Add(Build(name, cmd, ExeFromCommand(cmd), label, machine, approved, IsEnabled(hive, approved, name)));
            }
        }
        catch { /* clé illisible */ }
    }

    private static void ReadFolder(List<StartupEntry> list, string folder, string label, bool machine)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var hive = machine ? Registry.LocalMachine : Registry.CurrentUser;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var exe = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) : file;
            list.Add(Build(name, file, exe, label, machine, "StartupFolder", IsEnabled(hive, "StartupFolder", name)));
        }
    }

    private static StartupEntry Build(string name, string cmd, string? exe, string label, bool machine, string approved, bool enabled)
    {
        string display = Path.GetFileNameWithoutExtension(name), publisher = "";
        if (exe is not null && File.Exists(exe))
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(exe);
                if (!string.IsNullOrWhiteSpace(v.FileDescription)) display = v.FileDescription.Trim();
                publisher = v.CompanyName?.Trim() ?? "";
            }
            catch { }
        }
        var bare = Path.GetFileNameWithoutExtension(name);
        return new StartupEntry
        {
            Name = name, DisplayName = display, Publisher = publisher.Length > 0 ? publisher : null,
            Command = cmd, ExePath = exe, SourceLabel = label, MachineWide = machine, ApprovedKey = approved, Enabled = enabled,
            Tip = Tips.TryGetValue(bare, out var t) || (exe is not null && Tips.TryGetValue(Path.GetFileNameWithoutExtension(exe), out t)) ? t : null
        };
    }

    /// <summary>« "C:\X\app.exe" --min » ou « C:\X\app.exe -silent » → C:\X\app.exe.</summary>
    private static string? ExeFromCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : null;
        }
        var i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? cmd[..(i + 4)] : cmd.Split(' ')[0];
    }

    /// <summary>Cible d'un raccourci .lnk, via l'objet COM WScript.Shell fourni par Windows.</summary>
    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is not { } t) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            var target = (string)shell.CreateShortcut(lnk).TargetPath;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch { return null; }
    }
}
