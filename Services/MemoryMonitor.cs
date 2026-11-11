using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskAgent.Services;

/// <summary>État global de la RAM (comme l'en-tête « Mémoire » du Gestionnaire des tâches).</summary>
public record MemoryStatus(int UsedPercent, long TotalBytes, long AvailableBytes)
{
    public long UsedBytes => TotalBytes - AvailableBytes;
}

/// <summary>Une application = tous ses processus regroupés (ex. « Brave Browser (39) »).</summary>
public class AppMemoryGroup
{
    public string Name { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public List<int> Pids { get; init; } = new();
    public long Bytes { get; init; }
    public bool CanClose { get; init; }
    public string? Tip { get; init; }

    public int Count => Pids.Count;
    public string SizeLabel => MemoryMonitor.Format(Bytes);
}

/// <summary>
/// Lit la mémoire de la machine et des applications.
/// Analogie : la RAM, c'est ton bureau ; chaque appli ouverte y étale ses papiers.
/// Quand le bureau est plein à 90 %+, Windows range sans arrêt des papiers dans le tiroir (le disque) → tout rame.
/// </summary>
public static class MemoryMonitor
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Processus système à ne jamais proposer de fermer.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
        "dwm", "explorer", "fontdrvhost", "Memory Compression", "MsMpEng", "NisSrv", "SecurityHealthService",
        "sihost", "ctfmon", "conhost", "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "SearchIndexer", "TextInputHost", "audiodg", "spoolsv", "taskhostw", "dllhost",
        "WmiPrvSE", "LsaIso", "MpDefenderCoreService", "Taskmgr", "TaskAgent"
    };

    /// <summary>Conseils concrets pour les gros consommateurs habituels.</summary>
    private static readonly Dictionary<string, string> Tips = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brave"] = "Active l'économiseur de mémoire : brave://settings/system → « Économiseur de mémoire ». Ferme les onglets inutiles et désactive les extensions que tu n'utilises plus (brave://extensions).",
        ["chrome"] = "Active l'économiseur de mémoire : chrome://settings/performance. Deux navigateurs ouverts en même temps = deux fois la même facture : garde-en un seul.",
        ["msedge"] = "Active « Efficacité » et les onglets en veille : edge://settings/system.",
        ["firefox"] = "about:memory → « Minimize memory usage », et ferme les onglets inutiles.",
        ["claude"] = "L'appli Claude reste lourde avec beaucoup de conversations ouvertes : redémarre-la de temps en temps.",
        ["Code"] = "VS Code : ferme les dossiers/terminaux inutiles et désactive les extensions lourdes par espace de travail.",
        ["devenv"] = "Visual Studio : ferme les solutions inutilisées ; un redémarrage libère souvent 1 Go.",
        ["Unity"] = "Unity : un éditeur ouvert garde la scène et les assets en mémoire — ferme-le quand tu n'y travailles pas.",
        ["Teams"] = "Teams consomme beaucoup en arrière-plan : quitte-le complètement (icône près de l'horloge → Quitter).",
        ["Discord"] = "Discord : Paramètres → Windows → désactive le lancement au démarrage s'il ne te sert pas en continu.",
        ["vmmem"] = "vmmem = WSL/Docker. `wsl --shutdown` libère toute sa mémoire ; limite-la dans %UserProfile%\\.wslconfig (memory=4GB).",
        ["vmmemWSL"] = "vmmem = WSL/Docker. `wsl --shutdown` libère toute sa mémoire ; limite-la dans %UserProfile%\\.wslconfig (memory=4GB).",
    };

    public static MemoryStatus? GetStatus()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return null;
        return new MemoryStatus((int)m.dwMemoryLoad, (long)m.ullTotalPhys, (long)m.ullAvailPhys);
    }

    /// <summary>Applications triées de la plus gourmande à la moins gourmande.</summary>
    public static List<AppMemoryGroup> GetTopApps(int max = 15)
    {
        var mySession = Process.GetCurrentProcess().SessionId;
        var rows = new List<(string name, int pid, long bytes, int session, string? path)>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id is 0 or 4) continue;
                var name = p.ProcessName;
                rows.Add((name, p.Id, PrivateWorkingSet(p), p.SessionId, NameCache.ContainsKey(name) ? null : SafePath(p)));
            }
            catch { /* processus disparu ou inaccessible */ }
            finally { p.Dispose(); }
        }

        return rows
            .GroupBy(r => r.name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var path = g.Select(r => r.path).FirstOrDefault(x => x is not null);
                return new AppMemoryGroup
                {
                    ProcessName = g.Key,
                    Name = FriendlyName(g.Key, path),
                    Pids = g.Select(r => r.pid).ToList(),
                    Bytes = g.Sum(r => r.bytes),
                    CanClose = !Protected.Contains(g.Key) && g.All(r => r.session == mySession && r.session != 0),
                    Tip = Tips.TryGetValue(g.Key, out var t) ? t : null
                };
            })
            .OrderByDescending(a => a.Bytes)
            .Take(max)
            .ToList();
    }

    /// <summary>Fermeture propre : comme cliquer sur la croix (les navigateurs pourront restaurer leurs onglets).</summary>
    public static int CloseGracefully(AppMemoryGroup app)
    {
        var closed = 0;
        foreach (var pid in app.Pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow()) closed++;
            }
            catch { /* déjà fermé */ }
        }
        return closed;
    }

    /// <summary>Fermeture forcée (comme « Fin de tâche ») : le travail non enregistré est perdu.</summary>
    public static int Kill(AppMemoryGroup app)
    {
        var killed = 0;
        foreach (var pid in app.Pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                killed++;
            }
            catch { /* déjà fermé ou accès refusé */ }
        }
        return killed;
    }

    public static string Format(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} Go" : $"{bytes / (double)(1L << 20):0} Mo";

    // ---------------- Détails techniques ----------------

    private static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    private static string FriendlyName(string processName, string? path)
    {
        if (NameCache.TryGetValue(processName, out var cached)) return cached;
        var name = processName;
        try
        {
            if (path is not null && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d) name = d.Trim();
        }
        catch { }
        return NameCache[processName] = name;
    }

    private static string? SafePath(Process p)
    {
        try { return p.MainModule?.FileName; } catch { return null; }
    }

    /// <summary>
    /// « Plage de travail privée » = la colonne Mémoire du Gestionnaire des tâches.
    /// (WorkingSet64 compterait plusieurs fois les DLL partagées entre les 39 processus d'un navigateur.)
    /// </summary>
    private static long PrivateWorkingSet(Process p)
    {
        if (OperatingSystem.IsWindows())
        {
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var c = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
                    if (GetProcessMemoryInfo(h, ref c, c.cb) && c.PrivateWorkingSetSize != UIntPtr.Zero)
                        return (long)c.PrivateWorkingSetSize;
                }
                finally { CloseHandle(h); }
            }
        }
        return p.WorkingSet64;
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
            ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage,
            PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr h, ref PROCESS_MEMORY_COUNTERS_EX2 c, uint cb);
}
