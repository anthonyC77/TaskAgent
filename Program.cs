using Avalonia;

namespace TaskAgent;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Examen de l'agent sans interface (Services/EvalRunner.cs, skill « evaluer ») : aucune fenêtre n'est ouverte.
        if (args.Contains("--eval")) return Services.EvalRunner.Run(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
