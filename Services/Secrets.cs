using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace TaskAgent.Services;

/// <summary>
/// Coffre-fort des jetons d'accès (GitHub…). Ils ne sont jamais écrits en clair ni envoyés à l'IA.
/// Windows : chiffrés avec DPAPI, lisibles uniquement par ta session Windows.
/// macOS : rangés dans le Trousseau via la commande `security`.
/// </summary>
public static class Secrets
{
    private const string Service = "TaskAgent";

    public static string? Get(string name)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var file = FilePath(name);
                if (!File.Exists(file)) return null;
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            if (OperatingSystem.IsMacOS())
            {
                var (code, output) = Security("find-generic-password", "-s", Service, "-a", name, "-w");
                return code == 0 && output.Length > 0 ? output : null;
            }
        }
        catch { /* jeton illisible (autre session, fichier abîmé) : on le redemandera */ }
        return null;
    }

    public static void Set(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { Delete(name); return; }
        if (OperatingSystem.IsWindows())
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath(name), bytes);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // -U : met à jour s'il existe déjà
            var (code, output) = Security("add-generic-password", "-U", "-s", Service, "-a", name, "-w", value.Trim());
            if (code != 0) throw new InvalidOperationException("Trousseau macOS : " + output);
        }
        else throw new PlatformNotSupportedException("Stockage sécurisé des jetons non disponible sur ce système.");
    }

    public static void Delete(string name)
    {
        try
        {
            if (OperatingSystem.IsWindows()) File.Delete(FilePath(name));
            else if (OperatingSystem.IsMacOS()) Security("delete-generic-password", "-s", Service, "-a", name);
        }
        catch { }
    }

    private static string FilePath(string name) => Path.Combine(AppPaths.DataDir, $"{name}.secret");

    private static (int code, string output) Security(params string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/security")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Trim());
    }
}
