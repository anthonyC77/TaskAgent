using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TaskAgent.Services;

namespace TaskAgent.Views;

/// <summary>Fenêtre « Nouveautés » : CHANGELOG.md mis en forme, avec ta version repérée et celles qui arrivent.</summary>
public class ReleaseNotesWindow : Window
{
    /// <summary>Déclenché par « Redémarrer sur la x.y.z ».</summary>
    public event Action? RestartRequested;

    public ReleaseNotesWindow()
    {
        Title = "TaskAgent · Nouveautés";
        Width = 680; Height = 620; MinWidth = 460; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var update = AppVersion.UpdateAvailable();
        var list = new StackPanel { Spacing = 4 };
        var notes = AppVersion.Notes();
        if (notes.Count == 0) list.Children.Add(new TextBlock { Text = "Aucune note de version trouvée (CHANGELOG.md).", Opacity = 0.7 });

        foreach (var n in notes)
        {
            var tag = n.Version == AppVersion.Current ? "  ← ta version"
                : n.Version > AppVersion.Current ? "  · nouveau, pas encore lancé" : "";
            var title = new TextBlock
            {
                Text = $"Version {n.Version.ToString(3)}" + (n.Date.Length > 0 ? $" — {n.Date}" : "") + tag,
                FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 2)
            };
            if (n.Version > AppVersion.Current) title.Foreground = Brush.Parse("#30A46C");
            list.Children.Add(title);
            foreach (var raw in n.Body.Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith("<!--")) continue;
                if (line.StartsWith("### "))
                {
                    list.Children.Add(new TextBlock { Text = Plain(line[4..]), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
                    continue;
                }
                var indent = line.Length - line.TrimStart().Length;
                var text = line.TrimStart();
                var bullet = text.StartsWith("- ");
                list.Children.Add(new TextBlock
                {
                    Text = (bullet ? "•  " : "") + Plain(bullet ? text[2..] : text),
                    TextWrapping = TextWrapping.Wrap, FontSize = 13,
                    Margin = new Thickness(bullet ? 8 + indent * 6 : 22 + indent * 2, 0, 0, 0)
                });
            }
        }

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        if (update is { } u)
        {
            var restart = new Button { Content = $"🔄 Redémarrer sur la {u.Version.ToString(3)}", Classes = { "accent" } };
            restart.Click += (_, _) => { Close(); RestartRequested?.Invoke(); };
            bottom.Children.Add(restart);
        }
        var close = new Button { Content = "Fermer" };
        close.Click += (_, _) => Close();
        bottom.Children.Add(close);

        var header = new TextBlock
        {
            Text = $"Tu utilises la version {AppVersion.Label}." +
                   (update is { } up ? $" La {up.Version.ToString(3)} est prête." : ""),
            FontSize = 13, Opacity = 0.8
        };

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = list });
        Content = root;
    }

    /// <summary>Markdown simple → texte : on retire **gras**, `code` et les liens [x](y).</summary>
    private static string Plain(string s) =>
        Regex.Replace(Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1"), @"\*\*|`", "");
}
