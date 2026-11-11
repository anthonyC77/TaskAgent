using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace PdfToKindle;

public sealed class ExtractOptions
{
    public PageSelection? Pages { get; set; }
    public bool IncludeImages { get; set; } = true;
    public bool KeepHeadersFooters { get; set; }
}

public sealed class ExtractStats
{
    public int Pages;
    public int Images;
    public int UnsupportedImages;
    public List<string> Warnings { get; } = new();
}

public sealed record PdfContent(string? Title, string? Author, List<Block> Blocks, ExtractStats Stats);

/// <summary>
/// Transforme un PDF (mise en page figée, comme une photo) en flux de texte
/// (titres, paragraphes, images) que le Kindle pourra remettre en page librement.
/// </summary>
public static class PdfExtractor
{
    // Un bloc de texte « brut » tel que lu sur la page, avant classification.
    private sealed record RawText(string Text, double Top, double Bottom, double FontSize, bool Bold, int LineCount);
    private sealed record RawImage(string ImageId, double Top, bool FullPage, double AreaRatio);
    private sealed class RawPage
    {
        public double Height;
        public List<RawText> Texts = new();
        public List<RawImage> Images = new();
    }

    private static readonly Regex PageNumberRx = new(
        @"^\s*(page|p\.)?\s*(\d{1,4}|[ivxlcdm]{1,7})(\s*(/|sur|of)\s*\d{1,4})?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static PdfContent Extract(string path, ImageStore store, ExtractOptions opt)
    {
        var stats = new ExtractStats();
        using var doc = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true });

        IEnumerable<int> pageNumbers = opt.Pages?.Resolve(doc.NumberOfPages)
                                       ?? Enumerable.Range(1, doc.NumberOfPages);
        var pages = new List<RawPage>();
        foreach (var n in pageNumbers)
        {
            try
            {
                pages.Add(ReadPage(doc.GetPage(n), store, opt, stats));
                stats.Pages++;
            }
            catch (Exception ex)
            {
                stats.Warnings.Add($"page {n} ignorée : {ex.Message}");
            }
        }

        if (!opt.KeepHeadersFooters) RemoveHeadersAndFooters(pages);
        RemoveRepeatedDecorations(pages);

        var bodySize = ComputeBodyFontSize(pages);
        var blocks = BuildBlocks(pages, bodySize);
        stats.Images = blocks.OfType<ImageBlock>().Count();

        var title = Clean(doc.Information.Title ?? "");
        var author = Clean(doc.Information.Author ?? "");
        return new PdfContent(
            LooksLikeRealTitle(title) ? title : null,
            string.IsNullOrWhiteSpace(author) ? null : author,
            blocks, stats);
    }

    // ------------------------------------------------------------------ lecture d'une page

    private static RawPage ReadPage(Page page, ImageStore store, ExtractOptions opt, ExtractStats stats)
    {
        var raw = new RawPage { Height = page.Height };

        var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters)
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .ToList();

        if (words.Count > 0)
        {
            // Docstrum regroupe les mots en blocs (≈ paragraphes),
            // puis on devine l'ordre de lecture (colonnes, encadrés…).
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
            var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks).ToList();

            foreach (var block in ordered)
                raw.Texts.AddRange(SplitBlockByFontSize(block.TextLines));
        }

        if (opt.IncludeImages)
            ReadImages(page, store, raw, stats);

        return raw;
    }

    /// <summary>
    /// Un bloc Docstrum peut coller un titre et le paragraphe suivant :
    /// on le recoupe dès que la taille de police change nettement.
    /// </summary>
    private static IEnumerable<RawText> SplitBlockByFontSize(IReadOnlyList<UglyToad.PdfPig.DocumentLayoutAnalysis.TextLine> lines)
    {
        var run = new List<UglyToad.PdfPig.DocumentLayoutAnalysis.TextLine>();
        double runSize = 0;

        foreach (var line in lines)
        {
            var size = LineFontSize(line.Words);
            if (run.Count > 0 && (size / runSize > 1.12 || runSize / size > 1.12))
            {
                yield return MakeRawText(run);
                run.Clear();
            }
            if (run.Count == 0) runSize = size;
            run.Add(line);
        }
        if (run.Count > 0) yield return MakeRawText(run);
    }

    private static RawText MakeRawText(List<UglyToad.PdfPig.DocumentLayoutAnalysis.TextLine> lines)
    {
        var letters = lines.SelectMany(l => l.Words).SelectMany(w => w.Letters).ToList();
        var size = LineFontSize(lines.SelectMany(l => l.Words).ToList());
        var boldCount = letters.Count(l => IsBoldFont(l.FontName));
        return new RawText(
            JoinLines(lines.Select(l => l.Text)),
            lines.Max(l => l.BoundingBox.Top),
            lines.Min(l => l.BoundingBox.Bottom),
            size,
            letters.Count > 0 && boldCount > letters.Count * 0.6,
            lines.Count);
    }

    private static double LineFontSize(IReadOnlyList<Word> words)
    {
        var sizes = words.SelectMany(w => w.Letters)
            .Select(l => l.PointSize > 0.5 ? l.PointSize : l.GlyphRectangle.Height)
            .Where(s => s > 0)
            .OrderBy(s => s)
            .ToList();
        return sizes.Count == 0 ? 10 : sizes[sizes.Count / 2];
    }

    private static bool IsBoldFont(string? fontName) =>
        fontName is not null &&
        (fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
         fontName.Contains("Black", StringComparison.OrdinalIgnoreCase) ||
         fontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase) ||
         fontName.Contains("Semibold", StringComparison.OrdinalIgnoreCase));

    private static void ReadImages(Page page, ImageStore store, RawPage raw, ExtractStats stats)
    {
        var pageArea = page.Width * page.Height;
        foreach (var img in page.GetImages())
        {
            try
            {
#pragma warning disable CS0612, CS0618 // « Bounds » existe dans toutes les versions de PdfPig
                var b = img.Bounds;
#pragma warning restore CS0612, CS0618
                var areaRatio = pageArea > 0 ? Math.Abs(b.Width * b.Height) / pageArea : 0;

                // On ignore puces, filets et autres miettes graphiques.
                if (img.WidthInSamples < 40 || img.HeightInSamples < 40 || areaRatio < 0.005) continue;

                var encoded = Encode(img);
                if (encoded is null) { stats.UnsupportedImages++; continue; }

                var asset = store.Add(encoded.Value.Data, encoded.Value.Mime, encoded.Value.Ext,
                    img.WidthInSamples, img.HeightInSamples);
                raw.Images.Add(new RawImage(asset.Id, b.Top, areaRatio > 0.55, areaRatio));
            }
            catch
            {
                stats.UnsupportedImages++;
            }
        }
    }

    /// <summary>
    /// Kindle lit JPEG / PNG / GIF. Les JPEG du PDF sont recopiés tels quels (aucune perte) ;
    /// le reste est converti en PNG par PdfPig quand c'est possible.
    /// JPEG2000 et JBIG2 (certains scans) ne sont pas décodables en C# pur : ils sont signalés.
    /// </summary>
    private static (byte[] Data, string Mime, string Ext)? Encode(IPdfImage img)
    {
        var raw = img.RawBytes.ToArray();
        if (raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8 && raw[2] == 0xFF)
            return (raw, "image/jpeg", ".jpg");

        if (img.TryGetPng(out var png) && png is { Length: > 0 })
            return (png, "image/png", ".png");

        return null;
    }

    // ------------------------------------------------------------------ nettoyage

    /// <summary>
    /// En-têtes / pieds de page : texte situé dans la marge haute ou basse qui
    /// se répète d'une page à l'autre (titre courant, numéro de page…).
    /// </summary>
    private static void RemoveHeadersAndFooters(List<RawPage> pages)
    {
        static bool InMargin(RawText t, double h) => t.Bottom > h * 0.92 || t.Top < h * 0.08;
        static string Key(string s) => Regex.Replace(s.ToLowerInvariant(), @"\d+", "#").Trim();

        var counts = new Dictionary<string, int>();
        foreach (var p in pages)
            foreach (var k in p.Texts.Where(t => InMargin(t, p.Height)).Select(t => Key(t.Text)).Distinct())
                counts[k] = counts.GetValueOrDefault(k) + 1;

        var threshold = Math.Max(2, (int)Math.Ceiling(pages.Count * 0.3));
        foreach (var p in pages)
        {
            p.Texts.RemoveAll(t => InMargin(t, p.Height) &&
                                   (PageNumberRx.IsMatch(t.Text) ||
                                    (pages.Count >= 3 && counts.GetValueOrDefault(Key(t.Text)) >= threshold)));
        }
    }

    /// <summary>Logos et bandeaux répétés sur beaucoup de pages : supprimés.</summary>
    private static void RemoveRepeatedDecorations(List<RawPage> pages)
    {
        var perImage = pages.SelectMany(p => p.Images.Select(i => i.ImageId).Distinct())
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        foreach (var p in pages)
            p.Images.RemoveAll(i => perImage[i.ImageId] >= 3 && i.AreaRatio < 0.25);
    }

    private static double ComputeBodyFontSize(List<RawPage> pages)
    {
        // Taille « du corps de texte » = taille la plus fréquente, pondérée par le nombre de caractères.
        var histogram = new Dictionary<double, int>();
        foreach (var t in pages.SelectMany(p => p.Texts))
        {
            var key = Math.Round(t.FontSize * 2) / 2;
            histogram[key] = histogram.GetValueOrDefault(key) + t.Text.Length;
        }
        return histogram.Count == 0 ? 10 : histogram.MaxBy(kv => kv.Value).Key;
    }

    // ------------------------------------------------------------------ assemblage

    private static List<Block> BuildBlocks(List<RawPage> pages, double bodySize)
    {
        var result = new List<Block>();
        foreach (var page in pages)
        {
            var pendingImages = page.Images.OrderByDescending(i => i.Top).ToList();
            foreach (var t in page.Texts)
            {
                // Les images situées plus haut que ce texte sont insérées avant lui.
                while (pendingImages.Count > 0 && pendingImages[0].Top >= t.Top)
                {
                    result.Add(new ImageBlock(pendingImages[0].ImageId, pendingImages[0].FullPage));
                    pendingImages.RemoveAt(0);
                }

                var level = HeadingLevel(t, bodySize);
                if (level is int lv) result.Add(new HeadingBlock(t.Text, lv));
                else AppendParagraph(result, t.Text);
            }
            foreach (var img in pendingImages)
                result.Add(new ImageBlock(img.ImageId, img.FullPage));
        }
        return result;
    }

    private static int? HeadingLevel(RawText t, double body)
    {
        if (t.Text.Length > 150 || t.LineCount > 3 || !t.Text.Any(char.IsLetter)) return null;
        var ratio = t.FontSize / body;
        if (ratio >= 1.6) return 1;
        if (ratio >= 1.22) return 2;
        if (t.Bold && ratio >= 0.95 && t.LineCount == 1 && t.Text.Length <= 90 && !EndsSentence(t.Text)) return 3;
        return null;
    }

    /// <summary>
    /// Recolle un paragraphe coupé par un saut de page ou de colonne :
    /// le précédent ne finit pas par une ponctuation forte et le suivant commence en minuscule.
    /// </summary>
    private static void AppendParagraph(List<Block> result, string text)
    {
        if (result.Count > 0 && result[^1] is ParagraphBlock prev &&
            !EndsSentence(prev.Text) && text.Length > 0 && char.IsLower(text[0]))
        {
            result[^1] = new ParagraphBlock(JoinLines(new[] { prev.Text, text }));
            return;
        }
        result.Add(new ParagraphBlock(text));
    }

    private static bool EndsSentence(string s)
    {
        s = s.TrimEnd();
        return s.Length > 0 && ".!?:;»\"”)…".Contains(s[^1]);
    }

    // ------------------------------------------------------------------ texte

    /// <summary>Recolle des lignes en gérant la césure (« exem- / ple » → « exemple »).</summary>
    public static string JoinLines(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var rawLine in lines)
        {
            var line = Clean(rawLine);
            if (line.Length == 0) continue;
            if (sb.Length == 0) { sb.Append(line); continue; }

            if (sb[^1] == '­')
            {
                sb.Length--; // césure « douce » : on recolle sans espace
            }
            else if (sb[^1] == '-' && sb.Length > 1 && char.IsLetter(sb[^2]) && char.IsLower(line[0]))
            {
                sb.Length--;
            }
            else
            {
                sb.Append(' ');
            }
            sb.Append(line);
        }
        return sb.ToString();
    }

    public static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case 'ﬀ': sb.Append("ff"); break;
                case 'ﬁ': sb.Append("fi"); break;
                case 'ﬂ': sb.Append("fl"); break;
                case 'ﬃ': sb.Append("ffi"); break;
                case 'ﬄ': sb.Append("ffl"); break;
                case 'ﬅ' or 'ﬆ': sb.Append("st"); break;
                case '\t' or '\n' or '\r' or ' ' or ' ' or ' ' or ' ': sb.Append(' '); break;
                default:
                    if (!char.IsControl(c) && c != '�') sb.Append(c);
                    break;
            }
        }
        return Regex.Replace(sb.ToString(), @" {2,}", " ").Trim();
    }

    private static bool LooksLikeRealTitle(string t) =>
        t.Length >= 3 &&
        !t.EndsWith(".doc", StringComparison.OrdinalIgnoreCase) &&
        !t.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) &&
        !t.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
        !t.Equals("untitled", StringComparison.OrdinalIgnoreCase) &&
        !t.StartsWith("Microsoft Word", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Sélection de pages façon imprimante : « 1-10,15,20- ».</summary>
public sealed class PageSelection
{
    private readonly List<(int From, int? To)> _ranges = new();

    public static PageSelection Parse(string spec)
    {
        var sel = new PageSelection();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash < 0) { var n = int.Parse(part); sel._ranges.Add((n, n)); continue; }
            var from = dash == 0 ? 1 : int.Parse(part[..dash]);
            int? to = dash == part.Length - 1 ? null : int.Parse(part[(dash + 1)..]);
            if (from < 1 || (to is not null && to < from)) throw new FormatException($"Plage invalide : {part}");
            sel._ranges.Add((from, to));
        }
        if (sel._ranges.Count == 0) throw new FormatException("Sélection de pages vide");
        return sel;
    }

    public IEnumerable<int> Resolve(int pageCount) =>
        _ranges.SelectMany(r => Enumerable.Range(r.From, Math.Max(0, Math.Min(r.To ?? pageCount, pageCount) - r.From + 1)))
               .Distinct()
               .OrderBy(n => n);
}
