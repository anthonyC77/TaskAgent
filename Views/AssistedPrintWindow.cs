using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UglyToad.PdfPig.Writer;

namespace TaskAgent.Views;

/// <summary>
/// Impression assistée, pour les sites qui refusent les navigateurs automatisés (403) : chaque page s'ouvre dans TON navigateur,
/// tu fais Ctrl+P → Entrée, et TaskAgent repère le PDF arrivé dans le dossier, le numérote, puis ouvre la page suivante.
/// Analogie : TaskAgent te tend les pages une à une, c'est toi qui appuies sur le bouton de la photocopieuse.
/// </summary>
public class AssistedPrintWindow : Window
{
    private readonly List<Uri> _urls;
    private readonly string _folder;
    private readonly string?[] _files;   // PDF obtenu pour chaque page (null = pas encore)
    private int _current;
    private HashSet<string> _known;      // PDF déjà présents : on ne guette que les nouveaux

    private readonly TextBlock _progress = new() { FontSize = 16, FontWeight = FontWeight.SemiBold };
    private readonly ProgressBar _bar = new() { Height = 8, MinHeight = 8 };
    private readonly TextBlock _page = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _autoNext = new() { Content = "Ouvrir la page suivante dès que le PDF est arrivé", IsChecked = true };
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(1) };

    public AssistedPrintWindow(List<Uri> urls, string folder)
    {
        _urls = urls;
        _folder = folder;
        _files = new string?[urls.Count];
        Directory.CreateDirectory(folder);
        _known = Pdfs();

        Title = "TaskAgent · Impression assistée";
        Width = 640; Height = 470; MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Topmost = true; // reste visible par-dessus le navigateur

        var help = new TextBlock
        {
            Text = "Dans ton navigateur : Ctrl+P → Entrée (puis Entrée pour valider le nom).\n" +
                   $"La 1re fois seulement : choisis « Enregistrer au format PDF » comme imprimante et le dossier :\n{folder}\n" +
                   "Ton navigateur s'en souviendra pour les pages suivantes. TaskAgent renomme chaque PDF « 001 - … » dans l'ordre.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75
        };

        var open = new Button { Content = "🌐 Rouvrir la page" };
        open.Click += (_, _) => OpenCurrent();
        var prev = new Button { Content = "◀ Précédente" };
        prev.Click += (_, _) => Go(_current - 1);
        var skip = new Button { Content = "Passer ▶" };
        ToolTip.SetTip(skip, "Passe à la page suivante sans attendre de PDF.");
        skip.Click += (_, _) => Go(_current + 1);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        nav.Children.Add(prev); nav.Children.Add(open); nav.Children.Add(skip);

        var folderBtn = new Button { Content = "📂 Ouvrir le dossier" };
        folderBtn.Click += (_, _) => Open(_folder);
        var finish = new Button { Content = "✔ Terminer", Classes = { "accent" } };
        finish.Click += (_, _) => Close();
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        bottom.Children.Add(folderBtn); bottom.Children.Add(finish);

        var root = new StackPanel { Margin = new Thickness(14), Spacing = 10 };
        root.Children.Add(_progress);
        root.Children.Add(_bar);
        root.Children.Add(_page);
        root.Children.Add(nav);
        root.Children.Add(_autoNext);
        root.Children.Add(help);
        root.Children.Add(_status);
        root.Children.Add(bottom);
        Content = root;

        _watch.Tick += (_, _) => CheckForPdf();
        Opened += (_, _) => { Go(0); _watch.Start(); };
        Closed += (_, _) => { _watch.Stop(); WriteSummary(); };
    }

    private void Go(int index)
    {
        if (_current >= _urls.Count) return; // déjà terminé
        if (index >= _urls.Count) { Done(); return; }
        _current = Math.Max(0, index);
        _bar.Maximum = _urls.Count;
        _bar.Value = _files.Count(f => f is not null);
        _progress.Text = $"Page {_current + 1} sur {_urls.Count}  ·  {_files.Count(f => f is not null)} PDF obtenus";
        _page.Text = _urls[_current].ToString() + (_files[_current] is { } f ? $"\n✔ déjà enregistrée : {Path.GetFileName(f)}" : "");
        _status.Text = "⏳ En attente du PDF… (Ctrl+P dans ton navigateur)";
        OpenCurrent();
    }

    private void OpenCurrent() => Open(_urls[_current].ToString());

    /// <summary>Un nouveau PDF est arrivé et le navigateur a fini de l'écrire : on le renomme et on avance.</summary>
    private void CheckForPdf()
    {
        if (_current >= _urls.Count) return;
        var fresh = Pdfs().Except(_known, StringComparer.OrdinalIgnoreCase).Where(IsComplete).ToList();
        if (fresh.Count == 0) return;

        var file = fresh.OrderBy(File.GetLastWriteTimeUtc).Last();
        _known.UnionWith(fresh);
        var target = Path.Combine(_folder, $"{_current + 1:000} - {Path.GetFileNameWithoutExtension(file)}.pdf");
        try
        {
            if (!string.Equals(file, target, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(file, target);
            }
            _known.Add(target);
            _files[_current] = target;
        }
        catch (Exception ex) { _status.Text = $"PDF reçu mais pas renommé ({ex.Message}) : il reste sous son nom d'origine."; _files[_current] = file; }

        if (_autoNext.IsChecked == true) Go(_current + 1);
        else
        {
            _status.Text = $"✔ {Path.GetFileName(_files[_current])} — clique sur « Passer ▶ » pour la suite.";
            _bar.Value = _files.Count(f => f is not null);
        }
    }

    private void Done()
    {
        _current = _urls.Count;
        _watch.Stop();
        var got = _files.Where(f => f is not null).Cast<string>().ToList();
        _bar.Value = _bar.Maximum;
        _progress.Text = $"Terminé : {got.Count} PDF sur {_urls.Count} pages";
        _page.Text = "";
        if (got.Count > 1)
        {
            var merged = Path.Combine(_folder, $"_{_urls[0].Host} - pages choisies.pdf");
            try
            {
                File.WriteAllBytes(merged, PdfMerger.Merge(got.ToArray()));
                _status.Text = $"📕 PDF unique créé : {Path.GetFileName(merged)}";
            }
            catch (Exception ex) { _status.Text = "Fusion impossible (les PDF individuels sont là) : " + ex.Message; }
        }
        else _status.Text = "Rien à regrouper.";
        WriteSummary();
    }

    /// <summary>Même _sommaire.txt que l'impression automatique : fichier ↔ adresse.</summary>
    private void WriteSummary()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _urls.Count; i++)
            if (_files[i] is { } f) sb.AppendLine($"{Path.GetFileName(f)}\t{_urls[i]}");
        try { if (sb.Length > 0) File.WriteAllText(Path.Combine(_folder, "_sommaire.txt"), sb.ToString()); } catch { }
    }

    private HashSet<string> Pdfs() => new(Directory.EnumerateFiles(_folder, "*.pdf"), StringComparer.OrdinalIgnoreCase);

    /// <summary>Le navigateur écrit le PDF en plusieurs fois : on attend qu'il soit non vide et qu'on puisse l'ouvrir seul.</summary>
    private static bool IsComplete(string path)
    {
        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromSeconds(1)) return false;
            using var s = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return s.Length > 0;
        }
        catch { return false; }
    }

    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { _status.Text = $"Impossible d'ouvrir « {target} » : {ex.Message}"; }
    }
}
