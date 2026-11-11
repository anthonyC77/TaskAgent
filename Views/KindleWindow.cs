using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TaskAgent.Kindle;
using TaskAgent.Kindle.Translation;
using TaskAgent.Services;

namespace TaskAgent.Views;

/// <summary>
/// Fenêtre « PDF → Kindle / Markdown » (ex-outil pdf2kindle) : un PDF ou un dossier de PDF devient un EPUB ou du Markdown
/// lisible sur liseuse, avec en option une version traduite par le cerveau de TaskAgent.
/// </summary>
public class KindleWindow : Window
{
    private readonly ILlmProvider _llm;
    private readonly AppSettings _settings;

    private readonly TextBox _input = new() { Watermark = "Glisse ici un PDF ou un dossier, ou utilise les boutons →" };
    private readonly CheckBox _recursive = new() { Content = "Inclure les sous-dossiers" };
    private readonly CheckBox _separate = new() { Content = "Un fichier par PDF (sinon : 1 seul fichier, 1 PDF = 1 chapitre)" };
    private readonly ComboBox _format = new()
    {
        ItemsSource = new[] { "EPUB — pour liseuse Kindle", "Markdown (.md) — pour Obsidian, une IA, un wiki, GitHub…" },
        SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBox _pages = new() { Watermark = "toutes (ex. 1-20,25,30-)", Width = 200 };
    private readonly TextBox _title = new() { Watermark = "auto (métadonnées du PDF ou nom du fichier)" };
    private readonly TextBox _author = new() { Watermark = "auto" };
    private readonly CheckBox _images = new() { Content = "Inclure les images", IsChecked = true };
    private readonly CheckBox _keepHeaders = new() { Content = "Garder en-têtes, pieds et numéros de page" };
    private readonly CheckBox _fresh = new() { Content = "Repartir de zéro (relire tous les PDF, refaire l'OCR)" };

    private readonly ComboBox _ocr = new()
    {
        ItemsSource = new[] { "Désactivé", "Automatique : pages sans texte lisible (scans, texte dessiné)", "Forcer sur toutes les pages (texte du PDF illisible)" },
        SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBox _ocrLanguages = new() { Text = "fra+eng", Width = 160 };

    private readonly ComboBox _translate = new()
    {
        ItemsSource = new[] { "Pas de traduction", "En français si le livre ne l'est pas déjà", "Toujours traduire en français" },
        SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly CheckBox _translatedOnly = new() { Content = "Ne garder que la version traduite" };
    private readonly TextBox _glossary = new() { Watermark = "optionnel : termes imposés (.txt « terme => traduction » ou .json généré)" };
    private readonly TextBox _instructions = new() { Watermark = "optionnel : « tutoyer le lecteur, style soutenu »…" };

    private readonly TextBox _log = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), FontSize = 12, MinHeight = 140
    };
    private readonly Button _convert = new() { Content = "📚 Convertir", Classes = { "accent" } };
    private readonly Button _cancel = new() { Content = "Annuler", IsVisible = false };
    private readonly Button _openFolder = new() { Content = "📂 Ouvrir le dossier", IsEnabled = false };
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    // Ordre des chapitres (dossier → un seul livre) : deviné, puis modifiable avec ▲ ▼.
    private readonly ListBox _order = new() { Height = 190 };
    private readonly StackPanel _orderPanel = new() { Spacing = 4, IsVisible = false };
    private List<string> _orderFiles = new();
    private string? _orderRoot;

    private CancellationTokenSource? _cts;
    private string? _lastOutput;

    public KindleWindow(ILlmProvider llm, AppSettings settings)
    {
        _llm = llm;
        _settings = settings;
        Title = "TaskAgent · PDF → Kindle / Markdown";
        Width = 760; Height = 780; MinWidth = 560; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(new TextBlock
        {
            Text = "Un PDF, c'est une photo de page ; un EPUB, du texte liquide que la liseuse remet en forme. " +
                   "On recrée les paragraphes, titres, table des matières et images, puis tu l'envoies sur ton Kindle. " +
                   "En Markdown, chaque PDF devient un .md (titres #, paragraphes, images dans un dossier « nom_images » à côté).",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65
        });

        // --- Source ---
        var pickPdf = new Button { Content = "📄 PDF…" };
        pickPdf.Click += async (_, _) => await PickPdfAsync();
        var pickFolder = new Button { Content = "📁 Dossier…" };
        pickFolder.Click += async (_, _) => await PickFolderAsync(_input, "Dossier de PDF");
        form.Children.Add(Section("Source"));
        form.Children.Add(Row(_input, pickPdf, pickFolder));
        form.Children.Add(Wrap(_recursive, _separate));

        var up = new Button { Content = "▲" };
        ToolTip.SetTip(up, "Monter le chapitre sélectionné");
        up.Click += (_, _) => MoveSelected(-1);
        var down = new Button { Content = "▼" };
        ToolTip.SetTip(down, "Descendre le chapitre sélectionné");
        down.Click += (_, _) => MoveSelected(+1);
        var reset = new Button { Content = "↺ Ordre automatique" };
        reset.Click += (_, _) => RefreshOrder(force: true);
        var orderBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        orderBtns.Children.Add(up); orderBtns.Children.Add(down); orderBtns.Children.Add(reset);
        _orderPanel.Children.Add(new TextBlock
        {
            Text = "Ordre des chapitres dans le livre (deviné d'après les noms : préface, parties, chapitres numérotés, index…). " +
                   "Vérifie-le et corrige avec ▲ ▼ si besoin.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65
        });
        _orderPanel.Children.Add(_order);
        _orderPanel.Children.Add(orderBtns);
        form.Children.Add(_orderPanel);
        _input.TextChanged += (_, _) => RefreshOrder();
        _recursive.IsCheckedChanged += (_, _) => RefreshOrder(force: true);
        _separate.IsCheckedChanged += (_, _) => RefreshOrder();
        form.Children.Add(Labeled("Format", _format));
        // Pour un dossier converti en Markdown, on veut presque toujours un .md par PDF, rangé à côté de lui.
        _format.SelectionChanged += (_, _) => { if (_format.SelectedIndex == 1) _separate.IsChecked = true; };

        // --- Livre ---
        form.Children.Add(Section("Livre"));
        form.Children.Add(Labeled("Titre", _title));
        form.Children.Add(Labeled("Auteur", _author));
        form.Children.Add(Labeled("Pages", _pages));
        form.Children.Add(Wrap(_images, _keepHeaders));
        ToolTip.SetTip(_fresh, "Par défaut, une relance reprend le texte déjà lu (OCR compris) des PDF inchangés : seul ce qui manque est refait. " +
                               "Coche pour tout relire, par exemple après avoir changé de logiciel OCR.");
        form.Children.Add(_fresh);

        // --- OCR ---
        form.Children.Add(Section("OCR (PDF scannés)"));
        form.Children.Add(new TextBlock
        {
            Text = "Un scan n'est qu'une photo : l'OCR (Tesseract) y relit les lettres pour en refaire du texte reformatable et traduisible. " +
                   "Compter quelques secondes par page ; les modèles de langue (3 à 15 Mo chacun) sont téléchargés au premier scan rencontré.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65
        });
        form.Children.Add(_ocr);
        form.Children.Add(Labeled("Langues", Row(_ocrLanguages, new TextBlock
        {
            Text = "codes Tesseract : fra, eng, deu, spa, ita… reliés par « + »", FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center
        })));

        // --- Traduction ---
        var pickGlossary = new Button { Content = "…" };
        pickGlossary.Click += async (_, _) => await PickGlossaryAsync();
        form.Children.Add(Section("Traduction IA (2e fichier « nom.fr.epub » ou « nom.fr.md »)"));
        form.Children.Add(new TextBlock
        {
            Text = $"Traduit par : {_llm.Name}. Pas du mot-à-mot : l'IA lit d'abord un échantillon, dresse un glossaire " +
                   "(ce qui se traduit, ce qui reste en anglais), puis traduit par lots. Un cache permet de reprendre après une coupure.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65
        });
        form.Children.Add(_translate);
        form.Children.Add(_translatedOnly);
        form.Children.Add(Labeled("Glossaire", Row(_glossary, pickGlossary)));
        form.Children.Add(Labeled("Consignes", _instructions));

        // --- Actions ---
        _convert.Click += async (_, _) => await ConvertAsync();
        _cancel.Click += (_, _) => _cts?.Cancel();
        _openFolder.Click += (_, _) => { if (_lastOutput is not null) Open(Path.GetDirectoryName(_lastOutput)!); };
        var send = new Button { Content = "↗ Envoyer vers Kindle" };
        ToolTip.SetTip(send, "Ouvre la page Amazon « Envoyer vers Kindle » : glisses-y le .epub (ou envoie-le à ton adresse @kindle.com)");
        send.Click += (_, _) => Open(KindleConverter.SendToKindleUrl);

        var actions = new WrapPanel();
        var fix =new Button { Content = "🩹 Corriger un livre…" };
        ToolTip.SetTip(fix, "Corrige un .epub ou .md déjà produit (puces de liste lues « e », « © »… par l'OCR) sans relire les PDF ; " +
                            "le résultat est enregistré à côté (« livre v2.epub »), l'original n'est pas touché.");
        fix.Click += async (_, _) => await FixBookAsync();
        var toMd = new Button { Content = "📝 EPUB → Markdown…" };
        ToolTip.SetTip(toMd, "Transforme un ou plusieurs .epub déjà faits en .md (titres, listes, images dans « nom_images »), " +
                             "sans relire les PDF ni refaire l'OCR. Marche aussi avec un EPUB sans protection (DRM) venu d'ailleurs.");
        toMd.Click += async (_, _) => await EpubToMarkdownAsync();
        var translateEpub = new Button { Content = "🌍 Traduire un EPUB…" };
        ToolTip.SetTip(translateEpub, "Traduit un .epub sans protection (DRM) en français, sans passer par un PDF : « livre.fr.epub » à côté, l'original n'est pas touché. " +
                                      "Utilise le format, le glossaire et les consignes ci-dessus. Italiques, gras et liens ne sont pas conservés ; les tableaux sont mis à plat.");
        translateEpub.Click += async (_, _) => await TranslateEpubAsync();
        foreach (var button in new Control[] { _convert, _cancel, fix, toMd, translateEpub, _openFolder, send })
        {
            button.Margin = new Thickness(0, 0, 6, 4);
            actions.Children.Add(button);
        }
        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Spacing = 4 };
        bottom.Children.Add(_status);
        bottom.Children.Add(actions);

        var root = new DockPanel { Margin = new Thickness(14) };
        var formScroll = new ScrollViewer { Content = form, MaxHeight = 470 };
        DockPanel.SetDock(formScroll, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(formScroll);
        root.Children.Add(bottom);
        root.Children.Add(new Border { Margin = new Thickness(0, 10, 0, 0), Child = _log });
        Content = root;

        // Glisser-déposer d'un PDF ou d'un dossier n'importe où dans la fenêtre
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath() is { } path) _input.Text = path;
        });

        Closing += (_, e) =>
        {
            if (_cts is null) return;
            e.Cancel = true; // on ne ferme pas en pleine conversion
            _status.Text = "Conversion en cours : clique sur « Annuler » d'abord.";
        };
    }

    // ---------------- Conversion ----------------

    private async Task ConvertAsync()
    {
        if (_cts is not null) return;
        var input = _input.Text?.Trim().Trim('"');
        if (string.IsNullOrEmpty(input)) { _status.Text = "Choisis d'abord un PDF ou un dossier."; return; }
        if (input.EndsWith(".epub", StringComparison.OrdinalIgnoreCase)) { _status.Text = "Pour un EPUB, utilise « 🌍 Traduire un EPUB… » ou « 📝 EPUB → Markdown… »."; return; }

        var job = new KindleJob
        {
            Input = input,
            Recursive = _recursive.IsChecked == true,
            Separate = _separate.IsChecked == true,
            Files = _orderPanel.IsVisible && _orderFiles.Count > 0 ? new List<string>(_orderFiles) : null,
            Format = _format.SelectedIndex == 1 ? OutputFormat.Markdown : OutputFormat.Epub,
            Pages = _pages.Text,
            Title = _title.Text,
            Author = _author.Text,
            IncludeImages = _images.IsChecked == true,
            KeepHeadersFooters = _keepHeaders.IsChecked == true,
            Ocr = (OcrMode)_ocr.SelectedIndex,
            OcrLanguages = _ocrLanguages.Text ?? "fra+eng",
            Translate = (TranslateMode)_translate.SelectedIndex,
            TranslatedOnly = _translatedOnly.IsChecked == true,
            FreshStart = _fresh.IsChecked == true,
            Glossary = _glossary.Text?.Trim().Trim('"'),
            Instructions = _instructions.Text,
            Concurrency = TranslationConcurrency,
        };

        _log.Text = "";
        _cts = new CancellationTokenSource();
        SetBusy(true);
        var started = DateTime.Now;
        try
        {
            var llm = job.Translate == TranslateMode.Off ? null : new ProviderLlmClient(_llm);
            var produced = await KindleConverter.RunAsync(job, llm, Log, _cts.Token);
            _lastOutput = produced.LastOrDefault();
            _openFolder.IsEnabled = _lastOutput is not null;
            _status.Text = produced.Count > 0
                ? $"✔ {produced.Count} fichier(s) en {(DateTime.Now - started).TotalSeconds:0} s." +
                  (job.Format == OutputFormat.Epub ? " Glisse-les sur « Envoyer vers Kindle »." : "")
                : "Aucun fichier produit (voir le journal).";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Conversion annulée. Relancer reprendra la traduction où elle en était (cache).";
        }
        catch (Exception ex)
        {
            _status.Text = "⚠ " + ex.Message;
            Log("✗ " + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    /// <summary>Un modèle local (Qwen…) traite une requête à la fois ; Claude en encaisse plusieurs.</summary>
    private int TranslationConcurrency => _settings.Provider == "OpenAICompatible" ? 1 : 3;

    private async Task TranslateEpubAsync()
    {
        if (_cts is not null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "EPUB à traduire en français", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("EPUB") { Patterns = new[] { "*.epub" } } }
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;

        var job = new KindleJob
        {
            Input = path,
            Format = _format.SelectedIndex == 1 ? OutputFormat.Markdown : OutputFormat.Epub,
            Title = _title.Text,
            Author = _author.Text,
            // Le bouton dit déjà « traduire » : seul « Toujours » change quelque chose (forcer un livre déjà en français).
            Translate = _translate.SelectedIndex == 2 ? TranslateMode.Force : TranslateMode.Auto,
            Glossary = _glossary.Text?.Trim().Trim('"'),
            Instructions = _instructions.Text,
            Concurrency = TranslationConcurrency,
        };

        _log.Text = "";
        Log($"Traduction de {Path.GetFileName(path)}…");
        _cts = new CancellationTokenSource();
        SetBusy(true);
        _status.Text = "⏳ Traduction en cours…";
        var started = DateTime.Now;
        try
        {
            var result = await KindleConverter.TranslateEpubAsync(job, new ProviderLlmClient(_llm), Log, _cts.Token);
            if (result is not null) { _lastOutput = result; _openFolder.IsEnabled = true; }
            _status.Text = result is null
                ? "Ce livre semble déjà en français. Choisis « Toujours traduire en français » pour forcer."
                : $"✔ {Path.GetFileName(result)} en {(DateTime.Now - started).TotalMinutes:0.#} min.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Traduction annulée. Relancer sur le même EPUB reprendra où elle en était (cache).";
        }
        catch (Exception ex)
        {
            _status.Text = "⚠ " + ex.Message;
            Log("✗ " + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _convert.IsEnabled = !busy;
        _cancel.IsVisible = busy;
        if (busy) _status.Text = "⏳ Conversion en cours…";
    }

    /// <summary>Le convertisseur écrit depuis des tâches de fond : on repasse sur le fil de l'interface.</summary>
    private void Log(string line) => Dispatcher.UIThread.Post(() =>
    {
        _log.Text += line + Environment.NewLine;
        _log.CaretIndex = _log.Text.Length;
    });

    // ---------------- Correction d'un livre déjà fait ----------------

    private async Task FixBookAsync()
    {
        if (_cts is not null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Livre à corriger", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Livre") { Patterns = new[] { "*.epub", "*.md" } } }
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        _log.Text = "";
        Log($"Correction de {Path.GetFileName(path)}…");
        try
        {
            var result = await Task.Run(() => BookFixer.Fix(path, Log));
            if (result is not null) { _lastOutput = result; _openFolder.IsEnabled = true; }
            _status.Text = result is null ? "Rien à corriger." : $"✔ Livre corrigé : {Path.GetFileName(result)}";
        }
        catch (Exception ex) { _status.Text = "⚠ " + ex.Message; Log("✗ " + ex.Message); }
    }

    private async Task EpubToMarkdownAsync()
    {
        if (_cts is not null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "EPUB à transformer en Markdown", AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("EPUB") { Patterns = new[] { "*.epub" } } }
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0) return;
        _log.Text = "";
        var done = 0;
        foreach (var path in paths)
        {
            Log($"{Path.GetFileName(path)} → Markdown…");
            try
            {
                _lastOutput = await Task.Run(() => KindleConverter.EpubToMarkdown(path, Log));
                _openFolder.IsEnabled = true;
                done++;
            }
            catch (Exception ex) { Log("✗ " + ex.Message); }
        }
        _status.Text = $"✔ {done} fichier(s) .md sur {paths.Count}" + (done < paths.Count ? " (voir le journal)" : "");
    }

    // ---------------- Ordre des chapitres ----------------

    /// <summary>Affiche l'ordre deviné quand la source est un dossier fusionné en un seul livre ; garde les retouches tant que le dossier ne change pas.</summary>
    private void RefreshOrder(bool force = false)
    {
        var folder = _input.Text?.Trim().Trim('"');
        var show = !string.IsNullOrEmpty(folder) && Directory.Exists(folder) && _separate.IsChecked != true;
        _orderPanel.IsVisible = show;
        if (!show) return;
        if (!force && string.Equals(folder, _orderRoot, StringComparison.OrdinalIgnoreCase)) return;
        _orderRoot = folder;
        try { _orderFiles = KindleConverter.ListPdfs(folder!, _recursive.IsChecked == true); }
        catch { _orderFiles = new(); }
        ShowOrder(0);
    }

    private void ShowOrder(int selected)
    {
        _order.ItemsSource = _orderFiles.Select((f, i) => $"{i + 1,2}.  {Path.GetRelativePath(_orderRoot!, f)}").ToList();
        _order.SelectedIndex = _orderFiles.Count == 0 ? -1 : Math.Clamp(selected, 0, _orderFiles.Count - 1);
    }

    private void MoveSelected(int delta)
    {
        var i = _order.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _orderFiles.Count) return;
        (_orderFiles[i], _orderFiles[j]) = (_orderFiles[j], _orderFiles[i]);
        ShowOrder(j);
    }

    // ---------------- Sélecteurs ----------------

    private async Task PickPdfAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "PDF à convertir", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } }
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) _input.Text = path;
    }

    private async Task PickFolderAsync(TextBox target, string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) target.Text = path;
    }

    private async Task PickGlossaryAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Glossaire", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Glossaire") { Patterns = new[] { "*.txt", "*.json" } } }
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) _glossary.Text = path;
    }

    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { _status.Text = $"Impossible d'ouvrir « {target} » : {ex.Message}"; }
    }

    // ---------------- Mise en page ----------------

    private static TextBlock Section(string text) =>
        new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 15, Margin = new Thickness(0, 6, 0, 0) };

    private static Control Labeled(string label, Control field)
    {
        var row = new DockPanel();
        var l = new TextBlock { Text = label + " :", Width = 90, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(l, Dock.Left);
        row.Children.Add(l);
        row.Children.Add(field);
        return row;
    }

    /// <summary>Le premier contrôle prend la place restante, les autres (boutons) se rangent à droite.</summary>
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

    private static Control Wrap(params Control[] items)
    {
        var w = new WrapPanel();
        foreach (var i in items) { i.Margin = new Thickness(0, 0, 16, 0); w.Children.Add(i); }
        return w;
    }
}
