using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TaskAgent.Services;

namespace TaskAgent.Views;

/// <summary>
/// Fenêtre « Démarrage » : programmes lancés à l'ouverture de session, avec leur RAM actuelle,
/// et un interrupteur pour les désactiver (même mécanisme que le Gestionnaire des tâches, réversible).
/// </summary>
[SupportedOSPlatform("windows")]
public class StartupWindow : Window
{
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _summary = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private Dictionary<string, AppMemoryGroup> _running = new(StringComparer.OrdinalIgnoreCase);

    public StartupWindow()
    {
        Title = "TaskAgent · Démarrage";
        Width = 720; Height = 600; MinWidth = 520; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var top = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(_summary);
        top.Children.Add(new TextBlock
        {
            Text = "Chaque programme ci-dessous s'ouvre tout seul quand tu allumes le PC et occupe de la RAM même si tu ne t'en sers pas. " +
                   "Le désactiver ne le désinstalle pas : il ne se lance plus automatiquement, et tu peux toujours l'ouvrir toi-même. " +
                   "Effet au prochain démarrage — pour libérer la mémoire tout de suite, ferme-le aussi depuis la fenêtre Mémoire.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65
        });

        var refresh = new Button { Content = "🔄 Actualiser" };
        refresh.Click += (_, _) => Refresh();
        var settings = new Button { Content = "⚙ Paramètres Windows" };
        ToolTip.SetTip(settings, "Paramètres › Applications › Démarrage : montre aussi les applis du Microsoft Store, absentes de cette liste.");
        settings.Click += (_, _) => StartupApps.OpenWindowsSettings();
        var close = new Button { Content = "Fermer" };
        close.Click += (_, _) => Close();
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        btns.Children.Add(refresh); btns.Children.Add(settings); btns.Children.Add(close);
        DockPanel.SetDock(btns, Dock.Right);
        bottom.Children.Add(btns);
        bottom.Children.Add(_status);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = _rows });
        Content = root;

        Opened += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _running = MemoryMonitor.GetTopApps(int.MaxValue).ToDictionary(a => a.ProcessName, StringComparer.OrdinalIgnoreCase);
        var entries = StartupApps.List();

        var on = entries.Where(e => e.Enabled).ToList();
        var ram = on.Sum(e => Ram(e)?.Bytes ?? 0);
        _summary.Text = $"{on.Count} programme(s) au démarrage sur {entries.Count}" +
                        (ram > 0 ? $"  ·  {MemoryMonitor.Format(ram)} de RAM occupés en ce moment par ceux qui tournent" : "");

        _rows.Children.Clear();
        foreach (var e in entries.OrderByDescending(e => e.Enabled).ThenByDescending(e => Ram(e)?.Bytes ?? 0))
            _rows.Children.Add(BuildRow(e));
    }

    private AppMemoryGroup? Ram(StartupEntry e) =>
        e.ProcessName is { } p && _running.TryGetValue(p, out var a) ? a : null;

    private Control BuildRow(StartupEntry e)
    {
        var running = Ram(e);
        var toggle = new ToggleSwitch { IsChecked = e.Enabled, OnContent = "Activé", OffContent = "Désactivé", VerticalAlignment = VerticalAlignment.Center };
        if (e.MachineWide) ToolTip.SetTip(toggle, "Concerne tous les utilisateurs : Windows demandera l'autorisation administrateur.");
        toggle.IsCheckedChanged += async (_, _) =>
        {
            var want = toggle.IsChecked == true;
            if (want == e.Enabled) return;
            toggle.IsEnabled = false;
            var error = await StartupApps.SetEnabledAsync(e, want);
            toggle.IsEnabled = true;
            if (error is not null)
            {
                toggle.IsChecked = e.Enabled;
                _status.Text = $"{e.DisplayName} : {error}";
                return;
            }
            _status.Text = want
                ? $"{e.DisplayName} se lancera de nouveau au démarrage."
                : $"{e.DisplayName} ne se lancera plus au démarrage." +
                  (running is null ? "" : $" Il tourne encore ({running.SizeLabel}) : ferme-le depuis la fenêtre Mémoire pour libérer la place maintenant.");
            Refresh();
        };

        var name = new TextBlock
        {
            Text = e.DisplayName, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Opacity = e.Enabled ? 1 : 0.55
        };
        var size = new TextBlock
        {
            Text = running is null ? "pas lancé" : $"{running.SizeLabel} maintenant", Width = 130, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), Opacity = running is null ? 0.5 : 1
        };

        var line = new DockPanel();
        DockPanel.SetDock(toggle, Dock.Right);
        DockPanel.SetDock(size, Dock.Right);
        line.Children.Add(toggle);
        line.Children.Add(size);
        line.Children.Add(name);

        var cell = new StackPanel { Spacing = 2 };
        cell.Children.Add(line);
        cell.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", new[] { e.Publisher, e.SourceLabel }.Where(s => !string.IsNullOrEmpty(s))),
            FontSize = 11, Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis
        });
        ToolTip.SetTip(cell, e.Command);
        if (e.Tip is not null)
            cell.Children.Add(new TextBlock { Text = "↳ " + e.Tip, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        return new Border
        {
            Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#14888888")), Child = cell
        };
    }
}
