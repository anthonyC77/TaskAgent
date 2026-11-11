using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TaskAgent.Services;

namespace TaskAgent.Views;

/// <summary>
/// Choix parmi les liens d'une page : cases à cocher, filtre texte, « même site seulement ».
/// Renvoie les adresses cochées (pour les imprimer), ou null si annulé ; peut aussi les enregistrer en .txt.
/// </summary>
public class LinkPickerWindow : Window
{
    private readonly List<(PageLink link, CheckBox box)> _items = new();
    private readonly TextBox _filter = new() { Watermark = "Filtrer (texte ou adresse, ex. « chapitre » ou « /docs/ »)" };
    private readonly CheckBox _sameSite = new() { Content = "Seulement les liens du même site", IsChecked = true };
    private readonly TextBlock _count = new() { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };

    public LinkPickerWindow(Uri source, List<PageLink> links)
    {
        Title = "TaskAgent · Liens de la page";
        Width = 760; Height = 620; MinWidth = 520; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var rows = new StackPanel { Spacing = 1 };
        foreach (var l in links)
        {
            var cell = new StackPanel();
            cell.Children.Add(new TextBlock { Text = l.Text, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            cell.Children.Add(new TextBlock { Text = l.Url.ToString(), FontSize = 11, Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis });
            var box = new CheckBox { Content = cell, IsChecked = l.SameSite, HorizontalAlignment = HorizontalAlignment.Stretch };
            box.IsCheckedChanged += (_, _) => UpdateCount();
            _items.Add((l, box));
            rows.Children.Add(box);
        }

        var all = new Button { Content = "Tout cocher" };
        all.Click += (_, _) => SetVisible(true);
        var none = new Button { Content = "Tout décocher" };
        none.Click += (_, _) => SetVisible(false);
        var tools = new DockPanel();
        var toolBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        toolBtns.Children.Add(all); toolBtns.Children.Add(none);
        DockPanel.SetDock(toolBtns, Dock.Right);
        tools.Children.Add(toolBtns);
        tools.Children.Add(_sameSite);

        var top = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(new TextBlock
        {
            Text = $"{links.Count} lien(s) trouvé(s) sur {source}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap
        });
        top.Children.Add(_filter);
        top.Children.Add(tools);

        var add = new Button { Content = "➕ Ajouter à la liste à imprimer", Classes = { "accent" } };
        add.Click += (_, _) => Close(Checked());
        var save = new Button { Content = "💾 Enregistrer en .txt…" };
        ToolTip.SetTip(save, "Enregistre les liens cochés (une adresse par ligne, avec leur texte en commentaire) ; réutilisable via « Depuis un fichier… ».");
        save.Click += async (_, _) => await SaveAsync(source);
        var cancel = new Button { Content = "Annuler" };
        cancel.Click += (_, _) => Close(null);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        btns.Children.Add(save); btns.Children.Add(add); btns.Children.Add(cancel);
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(btns, Dock.Right);
        bottom.Children.Add(btns);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(_count);
        info.Children.Add(_status);
        bottom.Children.Add(info);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = rows });
        Content = root;

        _filter.TextChanged += (_, _) => ApplyFilter();
        _sameSite.IsCheckedChanged += (_, _) => ApplyFilter();
        ApplyFilter();
    }

    /// <summary>Le filtre masque les lignes ; « Tout cocher » n'agit que sur ce qui est affiché.</summary>
    private void ApplyFilter()
    {
        var f = _filter.Text?.Trim() ?? "";
        foreach (var (link, box) in _items)
            box.IsVisible = (_sameSite.IsChecked != true || link.SameSite) &&
                            (f.Length == 0 || link.Text.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
                             link.Url.ToString().Contains(f, StringComparison.OrdinalIgnoreCase));
        UpdateCount();
    }

    private void SetVisible(bool value)
    {
        foreach (var (_, box) in _items) if (box.IsVisible) box.IsChecked = value;
    }

    /// <summary>Les liens masqués par le filtre ne comptent pas, même cochés.</summary>
    private List<PageLink> CheckedLinks() => _items.Where(i => i.box.IsVisible && i.box.IsChecked == true).Select(i => i.link).ToList();

    private List<Uri> Checked() => CheckedLinks().Select(l => l.Url).ToList();

    private void UpdateCount() => _count.Text = $"{CheckedLinks().Count} lien(s) coché(s) sur {_items.Count(i => i.box.IsVisible)} affiché(s)";

    private async Task SaveAsync(Uri source)
    {
        var links = CheckedLinks();
        if (links.Count == 0) { _status.Text = "Coche d'abord au moins un lien."; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Enregistrer les liens", SuggestedFileName = $"liens {source.Host}.txt", DefaultExtension = "txt",
            FileTypeChoices = new[] { new FilePickerFileType("Texte") { Patterns = new[] { "*.txt" } } }
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        // Le texte du lien en commentaire « # » au-dessus de chaque adresse : lisible, et ignoré à la relecture.
        var lines = new List<string> { $"# Liens de {source} ({DateTime.Now:dd/MM/yyyy HH:mm})" };
        foreach (var l in links) { lines.Add("# " + l.Text); lines.Add(l.Url.ToString()); }
        try
        {
            await File.WriteAllLinesAsync(path, lines);
            _status.Text = $"✔ {links.Count} lien(s) enregistré(s) dans {Path.GetFileName(path)}";
        }
        catch (Exception ex) { _status.Text = "Enregistrement impossible : " + ex.Message; }
    }
}
