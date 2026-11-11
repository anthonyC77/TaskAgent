using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TaskAgent.Services;

namespace TaskAgent.Views;

/// <summary>
/// Fenêtre « Mémoire » : jauge RAM, applis les plus gourmandes (regroupées comme dans le Gestionnaire des tâches),
/// fermeture propre ou forcée, conseils, et envoi d'un état des lieux à l'agent.
/// </summary>
public class MemoryWindow : Window
{
    private static readonly string[] Browsers = { "brave", "chrome", "msedge", "firefox", "opera", "vivaldi" };

    private readonly TextBlock _headline = new() { FontSize = 18, FontWeight = FontWeight.SemiBold };
    private readonly ProgressBar _bar = new() { Height = 10, MinHeight = 10, Maximum = 100 };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private List<AppMemoryGroup> _apps = new();
    private MemoryStatus? _mem;

    /// <summary>Déclenché par « Demander à l'agent » avec un état des lieux texte.</summary>
    public event Action<string>? AskAgent;

    public MemoryWindow(string? alert = null)
    {
        Title = "TaskAgent · Mémoire";
        Width = 720; Height = 640; MinWidth = 520; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var top = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        if (alert is not null)
            top.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#33E5484D")), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
                Child = new TextBlock { Text = alert, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold }
            });
        top.Children.Add(_headline);
        top.Children.Add(_bar);
        top.Children.Add(new TextBlock
        {
            Text = "Image mentale : la RAM, c'est ton bureau. Au-delà de ~85 %, Windows passe son temps à ranger des papiers " +
                   "dans le tiroir (le disque) et tout rame. Fermer une appli libère sa place immédiatement.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65
        });
        top.Children.Add(_hint);

        var refresh = new Button { Content = "🔄 Actualiser" };
        refresh.Click += (_, _) => Refresh();
        var startup = new Button { Content = "🚀 Démarrage" };
        ToolTip.SetTip(startup, "Programmes lancés automatiquement à l'allumage du PC : désactive ceux qui occupent la RAM pour rien.");
        startup.Click += (_, _) => { if (OperatingSystem.IsWindows()) new StartupWindow().Show(this); };
        var ask = new Button { Content = "💬 Demander à l'agent", Classes = { "accent" } };
        ask.Click += (_, _) => { AskAgent?.Invoke(Snapshot()); Close(); };
        var close = new Button { Content = "Fermer" };
        close.Click += (_, _) => Close();
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        btns.Children.Add(refresh); btns.Children.Add(startup); btns.Children.Add(ask); btns.Children.Add(close);
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

        _timer.Tick += (_, _) => Refresh();
        Opened += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        _mem = MemoryMonitor.GetStatus();
        _apps = MemoryMonitor.GetTopApps();

        if (_mem is { } m)
        {
            _headline.Text = $"Mémoire : {m.UsedPercent} %  ·  {MemoryMonitor.Format(m.UsedBytes)} utilisés sur {MemoryMonitor.Format(m.TotalBytes)}" +
                             $"  ·  {MemoryMonitor.Format(m.AvailableBytes)} libres";
            _bar.Value = m.UsedPercent;
            _bar.Foreground = MainWindow.MemoryBrush(m.UsedPercent);
        }
        else _headline.Text = "Mémoire : état global indisponible sur ce système";

        _hint.Text = BuildHint();
        _rows.Children.Clear();
        foreach (var app in _apps) _rows.Children.Add(BuildRow(app));
    }

    /// <summary>Le conseil n°1, calculé à partir de ce qui tourne vraiment.</summary>
    private string BuildHint()
    {
        var browsers = _apps.Where(a => Browsers.Contains(a.ProcessName.ToLowerInvariant())).ToList();
        if (browsers.Count >= 2)
        {
            var smaller = browsers.OrderBy(b => b.Bytes).First();
            return $"💡 Gain le plus simple : {browsers.Count} navigateurs tournent en même temps " +
                   $"({string.Join(" + ", browsers.Select(b => $"{b.Name} {b.SizeLabel}"))}). " +
                   $"Fermer {smaller.Name} libère ~{smaller.SizeLabel} d'un coup.";
        }
        if (_apps.FirstOrDefault(a => a.CanClose) is { } big && _mem is { } m && big.Bytes > m.TotalBytes / 10)
            return $"💡 {big.Name} occupe à lui seul {big.SizeLabel} ({100.0 * big.Bytes / m.TotalBytes:0} % de ta RAM)." +
                   (big.Tip is null ? "" : " " + big.Tip);
        return "💡 Ferme d'abord ce dont tu ne te sers pas maintenant, en commençant par le haut de la liste.";
    }

    private Control BuildRow(AppMemoryGroup app)
    {
        var pct = _mem is { TotalBytes: > 0 } m ? 100.0 * app.Bytes / m.TotalBytes : 0;

        var name = new TextBlock
        {
            Text = app.Count > 1 ? $"{app.Name} ({app.Count})" : app.Name,
            FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };
        var size = new TextBlock
        {
            Text = $"{app.SizeLabel}  ·  {pct:0} %", Width = 130, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0)
        };
        var bar = new ProgressBar { Value = pct, Maximum = 100, Height = 4, MinHeight = 4, Margin = new Thickness(0, 3, 0, 0) };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (app.CanClose)
        {
            var close = new Button { Content = "Fermer" };
            ToolTip.SetTip(close, "Comme cliquer sur la croix : l'appli peut enregistrer, les navigateurs restaurent leurs onglets.");
            close.Click += (_, _) =>
            {
                var n = MemoryMonitor.CloseGracefully(app);
                _status.Text = n > 0
                    ? $"Demande de fermeture envoyée à {app.Name}. Si elle reste ouverte, utilise « Forcer »."
                    : $"{app.Name} n'a pas de fenêtre à fermer (elle tourne en arrière-plan) : utilise « Forcer » ou quitte-la depuis la zone de notification.";
                DispatcherTimer.RunOnce(Refresh, TimeSpan.FromSeconds(2));
            };
            var kill = new Button { Content = "Forcer", Foreground = Brushes.IndianRed };
            ToolTip.SetTip(kill, "« Fin de tâche » : immédiat, mais le travail non enregistré est perdu.");
            kill.Click += async (_, _) =>
            {
                if (!await Dialogs.ConfirmAsync(this, $"Forcer la fermeture de {app.Name} ({app.Count} processus, {app.SizeLabel}) ? " +
                                                      "Le travail non enregistré sera perdu.")) return;
                var n = MemoryMonitor.Kill(app);
                _status.Text = $"{n} processus de {app.Name} arrêté(s) · ~{app.SizeLabel} libérés.";
                DispatcherTimer.RunOnce(Refresh, TimeSpan.FromSeconds(1));
            };
            actions.Children.Add(close);
            actions.Children.Add(kill);
        }
        else actions.Children.Add(new TextBlock { Text = "système", Opacity = 0.5, FontSize = 12, Width = 120, TextAlignment = TextAlignment.Center });

        var line = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        DockPanel.SetDock(size, Dock.Right);
        line.Children.Add(actions);
        line.Children.Add(size);
        line.Children.Add(name);

        var cell = new StackPanel { Spacing = 2 };
        cell.Children.Add(line);
        cell.Children.Add(bar);
        if (app.Tip is not null)
            cell.Children.Add(new TextBlock { Text = "↳ " + app.Tip, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        return new Border
        {
            Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#14888888")), Child = cell
        };
    }

    /// <summary>État des lieux envoyé à l'agent pour qu'il propose un plan.</summary>
    private string Snapshot()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ma mémoire vive est saturée, aide-moi à la réduire durablement.");
        if (_mem is { } m)
            sb.AppendLine($"État actuel : {m.UsedPercent} % utilisés ({MemoryMonitor.Format(m.UsedBytes)} sur {MemoryMonitor.Format(m.TotalBytes)}).");
        sb.AppendLine("Applications les plus gourmandes (plage de travail privée) :");
        foreach (var a in _apps.Take(12))
            sb.AppendLine($"- {a.Name} ({a.ProcessName}.exe, {a.Count} processus) : {a.SizeLabel}");
        sb.AppendLine();
        sb.AppendLine("Dis-moi : 1) ce que je peux fermer tout de suite sans rien perdre, 2) les réglages à changer pour que ça ne revienne pas " +
                      "(navigateurs, applis au démarrage, WSL/Docker…), 3) si ajouter de la RAM serait justifié vu cet usage. " +
                      "Découpe en étapes « - [ ] ».");
        return sb.ToString();
    }
}
