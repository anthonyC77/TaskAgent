using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TaskAgent.Services;

namespace TaskAgent.Views;

/// <summary>
/// Fenêtre « Imprimer des pages web » : les pages saisies (ou, en option, tout un site exploré à partir d'elles)
/// en PDF dans le dossier choisi (Playwright + Edge).
/// </summary>
public class SitePrintWindow : Window
{
    private readonly TextBox _urls = new()
    {
        Watermark = "Une adresse par ligne, par ex. :\nhttps://exemple.com/guide/installation\nhttps://exemple.com/guide/reglages\n" +
                    "(tu peux aussi coller un texte ou une liste de favoris : les liens sont repérés tout seuls)",
        AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 140
    };
    private readonly TextBlock _count = new() { FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _follow = new() { Content = "Explorer aussi les pages reliées (suivre les liens internes, tout le site)" };
    private readonly TextBox _folder = new() { Watermark = "Dossier où enregistrer les PDF" };
    private readonly NumericUpDown _max = new() { Value = 100, Minimum = 1, Maximum = 5000, Increment = 10, Width = 140, FormatString = "0" };
    private readonly CheckBox _samePath = new() { Content = "Rester sous l'adresse de départ (ex. seulement /docs/…)", IsChecked = true };
    private readonly CheckBox _sitemap = new() { Content = "Lire aussi le plan du site (sitemap.xml)", IsChecked = true };
    private readonly CheckBox _merge = new() { Content = "Créer aussi un PDF unique qui regroupe toutes les pages", IsChecked = true };
    private readonly CheckBox _landscape = new() { Content = "Paysage" };
    private readonly CheckBox _login = new() { Content = "Me connecter d'abord (site avec compte)" };

    private readonly TextBox _log = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), FontSize = 12, MinHeight = 140
    };
    private readonly Button _print = new() { Content = "🖨 Imprimer", Classes = { "accent" } };
    private readonly Button _cancel = new() { Content = "Annuler", IsVisible = false };
    private readonly Button _openFolder = new() { Content = "📂 Ouvrir le dossier", IsEnabled = false };
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    private CancellationTokenSource? _cts;

    public SitePrintWindow()
    {
        Title = "TaskAgent · Imprimer des pages web";
        Width = 720; Height = 640; MinWidth = 540; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _folder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sites imprimés");

        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(new TextBlock
        {
            Text = "Colle les pages qui t'intéressent : un navigateur invisible (Edge) les ouvre une à une, dans l'ordre, et enregistre chacune en PDF " +
                   "comme Ctrl+P → « Enregistrer au format PDF ». Un fichier _sommaire.txt relie chaque PDF à son adresse.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65
        });

        var pick = new Button { Content = "📁 Choisir…" };
        pick.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Dossier de destination" });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) _folder.Text = path;
        };
        var import = new Button { Content = "📄 Depuis un fichier…" };
        ToolTip.SetTip(import, "Charger une liste d'adresses depuis un .txt (une par ligne), un .csv ou un export de favoris .html");
        import.Click += async (_, _) => await ImportAsync();
        var clear = new Button { Content = "Vider" };
        clear.Click += (_, _) => _urls.Text = "";
        var listTools = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var listBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var fromPage = new Button { Content = "🔗 Liens d'une page…" };
        ToolTip.SetTip(fromPage, "Repère tous les liens d'une page (sommaire, menu, index…) : tu coches ceux à imprimer, ou tu les enregistres en .txt.");
        fromPage.Click += async (_, _) => await PickLinksAsync(fromPage);
        listBtns.Children.Add(fromPage); listBtns.Children.Add(import); listBtns.Children.Add(clear);
        DockPanel.SetDock(listBtns, Dock.Right);
        listTools.Children.Add(listBtns);
        listTools.Children.Add(_count);
        var list = new StackPanel();
        list.Children.Add(_urls);
        list.Children.Add(listTools);
        _urls.TextChanged += (_, _) => UpdateCount();
        UpdateCount();

        form.Children.Add(Labeled("Pages", list));
        form.Children.Add(Labeled("Dossier", Row(_folder, pick)));
        form.Children.Add(_follow);

        // Réglages de l'exploration : affichés seulement si on suit les liens.
        var explore = new StackPanel { Spacing = 8, Margin = new Thickness(24, 0, 0, 0), IsVisible = false };
        _follow.IsCheckedChanged += (_, _) => { explore.IsVisible = _follow.IsChecked == true; UpdateCount(); };
        var max = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        max.Children.Add(_max);
        max.Children.Add(new TextBlock
        {
            Text = "garde-fou : un gros site peut compter des milliers de pages", FontSize = 12, Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center
        });
        explore.Children.Add(Labeled("Max. pages", max));
        explore.Children.Add(_samePath);
        explore.Children.Add(_sitemap);
        form.Children.Add(explore);
        form.Children.Add(_merge);
        var opts = new WrapPanel();
        _landscape.Margin = new Thickness(0, 0, 16, 0);
        opts.Children.Add(_landscape);
        opts.Children.Add(_login);
        ToolTip.SetTip(_login, "Ouvre d'abord une fenêtre de navigateur pour te connecter ; la session est ensuite réutilisée pour imprimer (et gardée pour la prochaine fois).");
        form.Children.Add(opts);

        _print.Click += async (_, _) => await PrintAsync();
        _cancel.Click += (_, _) => _cts?.Cancel();
        _openFolder.Click += (_, _) => Open(_folder.Text ?? "");

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var assisted = new Button { Content = "🖐 Impression assistée" };
        ToolTip.SetTip(assisted, "Pour les sites qui renvoient 403 : chaque page s'ouvre dans TON navigateur, tu fais Ctrl+P → Entrée, " +
                                 "TaskAgent range le PDF et ouvre la suivante.");
        assisted.Click += (_, _) => OpenAssisted();
        actions.Children.Add(_print); actions.Children.Add(assisted); actions.Children.Add(_cancel); actions.Children.Add(_openFolder);
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(actions, Dock.Right);
        bottom.Children.Add(actions);
        bottom.Children.Add(_status);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(form, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(form);
        root.Children.Add(bottom);
        root.Children.Add(new Border { Margin = new Thickness(0, 10, 0, 0), Child = _log });
        Content = root;

        Closing += (_, e) =>
        {
            if (_cts is null) return;
            e.Cancel = true; // on ne ferme pas en pleine impression
            _status.Text = "Impression en cours : clique sur « Annuler » d'abord.";
        };
    }

    private async Task PrintAsync()
    {
        if (_cts is not null) return;
        var urls = SitePrinter.ParseUrls(_urls.Text);
        var folder = _folder.Text?.Trim().Trim('"');
        if (urls.Count == 0) { _status.Text = "Colle d'abord au moins une adresse (https://…)."; return; }
        if (string.IsNullOrEmpty(folder)) { _status.Text = "Choisis le dossier de destination."; return; }

        var job = new SitePrintJob
        {
            Urls = urls, OutputFolder = folder, FollowLinks = _follow.IsChecked == true, MaxPages = (int)(_max.Value ?? 100),
            SamePathOnly = _samePath.IsChecked == true, UseSitemap = _sitemap.IsChecked == true,
            Merge = _merge.IsChecked == true, Landscape = _landscape.IsChecked == true, LoginFirst = _login.IsChecked == true
        };

        _log.Text = "";
        _cts = new CancellationTokenSource();
        _print.IsEnabled = false;
        _cancel.IsVisible = true;
        _status.Text = "⏳ Impression en cours…";
        var started = DateTime.Now;
        try
        {
            var produced = await Task.Run(() => SitePrinter.RunAsync(job, Log, WaitForLoginAsync, _cts.Token));
            _openFolder.IsEnabled = produced.Count > 0;
            _status.Text = produced.Count > 0
                ? $"✔ {produced.Count} PDF en {(DateTime.Now - started).TotalSeconds:0} s dans {folder}"
                : "Aucune page imprimée (voir le journal). Si le site renvoie 403, utilise « 🖐 Impression assistée ».";
        }
        catch (OperationCanceledException) { _status.Text = "Impression annulée : les PDF déjà créés restent dans le dossier."; _openFolder.IsEnabled = true; }
        catch (Exception ex)
        {
            _status.Text = "⚠ " + ex.Message.Split('\n')[0];
            Log("✗ " + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _print.IsEnabled = true;
            _cancel.IsVisible = false;
        }
    }

    private void UpdateCount()
    {
        var n = SitePrinter.ParseUrls(_urls.Text).Count;
        _count.Text = n == 0 ? "Aucune adresse reconnue pour l'instant."
            : _follow.IsChecked == true ? $"{n} adresse(s) de départ · les pages reliées seront ajoutées."
            : $"{n} page(s) à imprimer, dans cet ordre.";
    }

    /// <summary>N'importe quel fichier texte : les adresses sont repérées dedans (favoris exportés, CSV, notes…).</summary>
    private async Task ImportAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Liste d'adresses", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Liste d'adresses") { Patterns = new[] { "*.txt", "*.csv", "*.html", "*.htm", "*.md" } } }
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            // Page enregistrée avec Ctrl+S (ou export de favoris) : on propose ses liens à cocher.
            if (Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm")
            {
                await PickFromHtmlAsync(await File.ReadAllTextAsync(path), null);
                return;
            }
            // Lignes « # … » = commentaires (ex. fichier enregistré depuis « Liens d'une page ») ; une adresse par ligne pour le reste.
            var text = string.Join("\n", (await File.ReadAllLinesAsync(path)).Where(l => !l.TrimStart().StartsWith('#')));
            var urls = SitePrinter.ParseUrls(Regex.Replace(text, @"(https?://)", "\n$1"));
            AddUrls(urls);
            _status.Text = $"{urls.Count} adresse(s) ajoutée(s) depuis {Path.GetFileName(path)}.";
        }
        catch (Exception ex) { _status.Text = "Lecture impossible : " + ex.Message; }
    }

    private void OpenAssisted()
    {
        var urls = SitePrinter.ParseUrls(_urls.Text);
        var folder = _folder.Text?.Trim().Trim('"');
        if (urls.Count == 0) { _status.Text = "Colle d'abord au moins une adresse (https://…)."; return; }
        if (string.IsNullOrEmpty(folder)) { _status.Text = "Choisis le dossier de destination."; return; }
        try { new AssistedPrintWindow(urls, folder).Show(this); }
        catch (Exception ex) { _status.Text = "⚠ " + ex.Message; }
    }

    private void AddUrls(IEnumerable<Uri> urls)
    {
        var current = (_urls.Text ?? "").TrimEnd();
        _urls.Text = (current.Length > 0 ? current + "\n" : "") + string.Join("\n", urls);
    }

    /// <summary>Demande une page, en extrait les liens, puis laisse cocher ceux à ajouter à la liste.</summary>
    private async Task PickLinksAsync(Button trigger)
    {
        if (_cts is not null) { _status.Text = "Attends la fin de l'impression en cours."; return; }
        var answer = await Dialogs.AskAsync(this,
            "Adresse de la page dont tu veux récupérer les liens (sommaire, menu, index…), OU son code HTML collé depuis ton navigateur",
            "https://exemple.com/guide/   — ou —   <html>… (F12 → clic droit sur <html> → Copier → Copier l'élément)");
        if (string.IsNullOrWhiteSpace(answer)) return;
        if (answer.Contains('<')) { await PickFromHtmlAsync(answer, null); return; }
        if (SitePrinter.ParseUrls(answer).FirstOrDefault() is not { } source) return;

        trigger.IsEnabled = _print.IsEnabled = false;
        _status.Text = $"⏳ Lecture des liens de {source}…";
        List<PageLink> links;
        try
        {
            links = await Task.Run(() => SitePrinter.ExtractLinksAsync(source, CancellationToken.None));
        }
        catch (Exception ex)
        {
            links = new();
            _status.Text = "⚠ " + ex.Message.Split('\n')[0];
        }
        finally { trigger.IsEnabled = _print.IsEnabled = true; }

        if (links.Count == 0)
        {
            // Plan B : la page que TON navigateur affiche, copiée depuis F12.
            if (!await Dialogs.ConfirmAsync(this, "Aucun lien récupéré automatiquement (site qui refuse les navigateurs automatisés, ou page réservée). " +
                                                  "Veux-tu coller le code de la page depuis ton navigateur ? Ouvre la page, F12 → onglet Éléments → " +
                                                  "clic droit sur la ligne <html> → Copier → Copier l'élément.")) return;
            var html = await Dialogs.AskAsync(this, $"Colle ici le code HTML de {source}", "<html>…");
            if (!string.IsNullOrWhiteSpace(html)) await PickFromHtmlAsync(html, source);
            return;
        }
        _status.Text = "";
        await ShowPickerAsync(source, links);
    }

    private async Task PickFromHtmlAsync(string html, Uri? source)
    {
        var (baseUri, links) = SitePrinter.LinksFromHtml(html, source);
        if (baseUri is null)
        {
            // Liens relatifs (« /docs/x ») : il faut l'adresse de la page pour les compléter.
            var answer = await Dialogs.AskAsync(this, "Quelle est l'adresse de cette page ? (pour compléter les liens relatifs)", "https://exemple.com/guide/");
            if (SitePrinter.ParseUrls(answer).FirstOrDefault() is not { } u) return;
            (baseUri, links) = SitePrinter.LinksFromHtml(html, u);
        }
        if (links.Count == 0) { _status.Text = "Aucun lien trouvé dans ce code HTML."; return; }
        await ShowPickerAsync(baseUri!, links);
    }

    private async Task ShowPickerAsync(Uri source, List<PageLink> links)
    {
        var chosen = await new LinkPickerWindow(source, links).ShowDialog<List<Uri>?>(this);
        if (chosen is not { Count: > 0 }) return;
        AddUrls(chosen);
        _status.Text = $"{chosen.Count} lien(s) ajouté(s) à la liste.";
    }

    /// <summary>Appelé depuis le fil de fond quand le navigateur visible est prêt : on demande à l'utilisateur sur le fil de l'interface.</summary>
    private Task<bool> WaitForLoginAsync() => Dispatcher.UIThread.InvokeAsync(() =>
        Dialogs.ConfirmAsync(this, "Connecte-toi au site dans la fenêtre du navigateur qui vient de s'ouvrir. " +
                                   "Une fois connecté, clique sur « Oui » pour lancer l'impression (« Non » annule)."));

    private void Log(string line) => Dispatcher.UIThread.Post(() =>
    {
        _log.Text += line + Environment.NewLine;
        _log.CaretIndex = _log.Text.Length;
    });

    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { _status.Text = $"Impossible d'ouvrir « {target} » : {ex.Message}"; }
    }

    private static Control Labeled(string label, Control field)
    {
        var row = new DockPanel();
        var l = new TextBlock { Text = label + " :", Width = 90, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(l, Dock.Left);
        row.Children.Add(l);
        row.Children.Add(field);
        return row;
    }

    private static Control Row(Control main, params Control[] right)
    {
        var row = new DockPanel();
        foreach (var c in Enumerable.Reverse(right))
        {
            c.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(c, Dock.Right);
            row.Children.Add(c);
        }
        row.Children.Add(main);
        return row;
    }
}
