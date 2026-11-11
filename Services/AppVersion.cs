using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace TaskAgent.Services;

/// <summary>Une section de CHANGELOG.md : « ## 1.1.0 — 2026-11-02 » + son texte.</summary>
public record ReleaseNote(Version Version, string Date, string Body);

/// <summary>
/// Versions déployées : deploy.ps1 publie chaque version dans releases\x.y.z\ et écrit releases\current.txt.
/// Analogie : chaque version a sa propre boîte ; celle que tu utilises n'est jamais touchée pendant qu'on prépare la suivante,
/// et l'étiquette « current » dit laquelle prendre au prochain démarrage.
/// </summary>
public static class AppVersion
{
    /// <summary>Version de l'exe en cours (fixée par deploy.ps1 via -p:Version).</summary>
    public static Version Current { get; } = ReadCurrent();

    /// <summary>Lancé depuis bin\Debug ou bin\Release (dotnet run / Visual Studio) plutôt que depuis une version déployée.</summary>
    public static bool IsDev { get; } = AppContext.BaseDirectory.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);

    public static string Label => IsDev ? $"{Current.ToString(3)} (dev)" : Current.ToString(3);

    /// <summary>Dossier releases\ : parent du dossier de la version déployée, ou à la racine du projet en dev.</summary>
    public static string? ReleasesDir { get; } = FindReleasesDir();

    /// <summary>Version désignée par releases\current.txt, et son exe ; null si rien de déployé.</summary>
    public static (Version Version, string Exe)? Latest()
    {
        if (ReleasesDir is null) return null;
        try
        {
            var text = File.ReadAllText(Path.Combine(ReleasesDir, "current.txt")).Trim();
            if (!Version.TryParse(text, out var v)) return null;
            var exe = Path.Combine(ReleasesDir, text, "TaskAgent.exe");
            return File.Exists(exe) ? (v, exe) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Version déployée à proposer : plus récente que celle en cours, ou n'importe laquelle si on tourne sur la version
    /// de travail (bin\Debug) — elle n'est pas faite pour l'usage quotidien, autant basculer sur la version déployée.
    /// </summary>
    public static (Version Version, string Exe)? UpdateAvailable() =>
        Latest() is { } l && (IsDev || l.Version > Current) ? l : null;

    /// <summary>Lance la nouvelle version ; l'appelant ferme ensuite celle-ci.</summary>
    public static bool Launch(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
            return true;
        }
        catch { return false; }
    }

    /// <summary>Notes de version : celles de la dernière version déployée si elle existe (pour voir ce qui arrive), sinon les siennes.</summary>
    public static List<ReleaseNote> Notes()
    {
        var candidates = new List<string>();
        if (Latest() is { } l) candidates.Add(Path.Combine(Path.GetDirectoryName(l.Exe)!, "CHANGELOG.md"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md"));
        if (ReleasesDir is not null) candidates.Add(Path.Combine(Path.GetDirectoryName(ReleasesDir)!, "CHANGELOG.md"));
        var file = candidates.FirstOrDefault(File.Exists);
        return file is null ? new() : Parse(File.ReadAllText(file));
    }

    public static List<ReleaseNote> Parse(string markdown)
    {
        var notes = new List<ReleaseNote>();
        var matches = Regex.Matches(markdown, @"^##\s+(\d+\.\d+\.\d+)\s*(?:[—–-]\s*(.*?))?\s*$", RegexOptions.Multiline);
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var end = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            var body = markdown[(m.Index + m.Length)..end].Trim();
            notes.Add(new ReleaseNote(Version.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim(), body));
        }
        return notes;
    }

    private static Version ReadCurrent()
    {
        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var clean = info?.Split('+')[0]; // « 1.1.0+abc123 » : on retire l'empreinte de compilation
        return Version.TryParse(clean, out var v) ? v : new Version(0, 0, 0);
    }

    private static string? FindReleasesDir()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        // Version déployée : …\releases\1.1.0\TaskAgent.exe
        var parent = Directory.GetParent(baseDir);
        if (parent is not null && parent.Name.Equals("releases", StringComparison.OrdinalIgnoreCase)) return parent.FullName;
        // Dev : on remonte jusqu'au dossier du projet (celui qui contient TaskAgent.csproj).
        for (var d = new DirectoryInfo(baseDir); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TaskAgent.csproj"))) return Path.Combine(d.FullName, "releases");
        return null;
    }
}
