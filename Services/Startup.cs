using Microsoft.Win32;

namespace TaskAgent.Services;

/// <summary>
/// Lancement à l'ouverture de session Windows : une entrée dans HKCU\...\Run, comme la plupart des applis
/// de la zone de notification. Pointe vers l'exe en cours : publie l'exe puis lance-le une fois depuis son dossier final.
/// </summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskAgent";

    public static void Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe) return;
        // `dotnet run` lance l'exe de bin\Debug : on n'inscrit pas une version de dev au démarrage.
        if (exe.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{exe}\"");
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
    }
}
