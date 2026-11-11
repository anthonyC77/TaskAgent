using System.Collections.Generic;
using System.Collections.Generic;
using System.Linq;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig;

namespace TaskAgent.Kindle;

public sealed class ExtractOptions
{
    public PageSelection? Pages { get; set; }

    public bool IncludeImages { get; set; } = true;

    public bool KeepHeadersFooters { get; set; }

    public OcrMode Ocr { get; set; } = OcrMode.Auto;

    public string OcrLanguages { get; set; } = "fra+eng";

    public int OcrDpi { get; set; } = 300;

    public CancellationToken Cancel { get; set; }

    public string? PagesSpec { get; set; }

    public bool UseCache { get; set; } = true;
}

public sealed class ExtractStats
{
    public int Pages;

    public int Images;

    public int UnsupportedImages;

    public int OcrPages;

    public List<string> Warnings { get; } = new List<string>();
}

public sealed record PdfContent(string? Title, string? Author, List<Block> Blocks, ExtractStats Stats);

public static class PdfExtractor
{
    private sealed record RawText(string Text, double Top, double Bottom, double FontSize, bool Bold, int LineCount);

    private sealed record RawImage(string ImageId, double Top, bool FullPage, double AreaRatio, double Left = 0.0, double Right = 0.0, double Bottom = 0.0);

    private sealed class RawPage
    {
        public double Height;

        public int LetterCount;

        public bool HasLargeImage;

        public bool HasDrawnText;

        public List<RawText> Texts = new List<RawText>();

        public List<RawImage> Images = new List<RawImage>();
    }

    private static readonly Regex PageNumberRx = new Regex("^\\s*(page|p\\.)?\\s*(\\d{1,4}|[ivxlcdm]{1,7})(\\s*(/|sur|of)\\s*\\d{1,4})?\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static PdfContent Extract(string path, ImageStore store, ExtractOptions opt, Action<string>? log = null)
    {
        ExtractStats extractStats = new ExtractStats();
        using PdfDocument pdfDocument = PdfDocument.Open(path, new ParsingOptions
        {
            UseLenientParsing = true
        });
        IEnumerable<int> enumerable = opt.Pages?.Resolve(pdfDocument.NumberOfPages) ?? Enumerable.Range(1, pdfDocument.NumberOfPages);
        List<RawPage> list = new List<RawPage>();
        PdfOcr pdfOcr = null;
        bool flag = false;
        try
        {
            foreach (int item in enumerable)
            {
                opt.Cancel.ThrowIfCancellationRequested();
                try
                {
                    Page page = pdfDocument.GetPage(item);
                    RawPage rawPage = ReadPage(page, store, opt, extractStats);
                    if (!flag && NeedsOcr(rawPage, opt))
                    {
                        try
                        {
                            if (pdfOcr == null)
                            {
                                pdfOcr = PdfOcr.Open(path, opt.OcrLanguages, opt.OcrDpi, log ?? ((Action<string>)((string _) =>
                                {
                                })), opt.Cancel);
                            }
                            List<(double, double, double, double)> ignore = (from i in rawPage.Images
                                where !i.FullPage && i.Right > i.Left
                                select (Left: i.Left, Top: i.Top, Right: i.Right, Bottom: i.Bottom)).ToList();
                            ApplyOcr(rawPage, pdfOcr.ReadPage(item, page.Height, ignore), store, opt, extractStats);
                            if (++extractStats.OcrPages % 10 == 0)
                            {
                                log?.Invoke($"   OCR : {extractStats.OcrPages} page(s) lue(s)…");
                            }
                        }
                        catch (InvalidOperationException ex) when (pdfOcr == null)
                        {
                            flag = true;
                            extractStats.Warnings.Add(ex.Message);
                        }
                    }
                    list.Add(rawPage);
                    extractStats.Pages++;
                }
                catch (Exception ex2) when (!(ex2 is OperationCanceledException))
                {
                    extractStats.Warnings.Add($"page {item} ignorée : {ex2.Message}");
                }
            }
        }
        finally
        {
            pdfOcr?.Dispose();
        }
        if (!opt.KeepHeadersFooters)
        {
            RemoveHeadersAndFooters(list);
        }
        RemoveRepeatedDecorations(list);
        double bodySize = ComputeBodyFontSize(list);
        List<Block> list2 = BuildBlocks(list, bodySize);
        extractStats.Images = list2.OfType<ImageBlock>().Count();
        string text = Clean(pdfDocument.Information.Title ?? "");
        string text2 = Clean(pdfDocument.Information.Author ?? "");
        return new PdfContent(LooksLikeRealTitle(text) ? text : null, string.IsNullOrWhiteSpace(text2) ? null : text2, list2, extractStats);
    }

    private static RawPage ReadPage(Page page, ImageStore store, ExtractOptions opt, ExtractStats stats)
    {
        RawPage rawPage = new RawPage
        {
            Height = page.Height,
            LetterCount = page.Letters.Count
        };
        rawPage.HasLargeImage = HasLargeImage(page);
        rawPage.HasDrawnText = rawPage.LetterCount < 30 && CountPaths(page) > 50;
        List<Word> list = (from w in NearestNeighbourWordExtractor.Instance.GetWords(page.Letters)
            where !string.IsNullOrWhiteSpace(w.Text)
            select w).ToList();
        if (list.Count > 0)
        {
            IReadOnlyList<TextBlock> blocks = DocstrumBoundingBoxes.Instance.GetBlocks(list);
            foreach (TextBlock item in UnsupervisedReadingOrderDetector.Instance.Get(blocks).ToList())
            {
                rawPage.Texts.AddRange(SplitBlockByFontSize(item.TextLines));
            }
        }
        if (opt.IncludeImages)
        {
            ReadImages(page, store, rawPage, stats);
        }
        return rawPage;
    }

    private static bool NeedsOcr(RawPage raw, ExtractOptions opt)
    {
        return opt.Ocr switch
        {
            OcrMode.Force => true, 
            OcrMode.Auto => raw.LetterCount < 30 && (raw.HasLargeImage || raw.HasDrawnText), 
            _ => false, 
        };
    }

    private static int CountPaths(Page page)
    {
        try
        {
            return page.Paths.Count;
        }
        catch
        {
            return 0;
        }
    }

    private static bool HasLargeImage(Page page)
    {
        double pageArea = page.Width * page.Height;
        if (pageArea <= 0.0)
        {
            return false;
        }
        try
        {
            return page.GetImages().Any((IPdfImage i) => Math.Abs(i.Bounds.Width * i.Bounds.Height) / pageArea > 0.3);
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyOcr(RawPage raw, OcrPage ocr, ImageStore store, ExtractOptions opt, ExtractStats stats)
    {
        if (ocr.Paragraphs.Count == 0 && ocr.Figures.Count == 0)
        {
            return;
        }
        raw.Texts = (from p in ocr.Paragraphs
            select new RawText(JoinLines(p.Lines), p.Top, p.Bottom, p.FontSize, Bold: false, p.Lines.Count) into t
            where t.Text.Length > 0
            select t).ToList();
        raw.Images.RemoveAll((RawImage i) => i.FullPage);
        if (!opt.IncludeImages)
        {
            return;
        }
        foreach (OcrFigure figure in ocr.Figures)
        {
            ImageAsset imageAsset = store.Add(figure.Png, "image/png", ".png", figure.Width, figure.Height);
            raw.Images.Add(new RawImage(imageAsset.Id, figure.Top, FullPage: false, figure.AreaRatio));
            stats.Images++;
        }
    }

    private static IEnumerable<RawText> SplitBlockByFontSize(IReadOnlyList<TextLine> lines)
    {
        List<TextLine> run = new List<TextLine>();
        double runSize = 0.0;
        foreach (TextLine line in lines)
        {
            double size = LineFontSize(line.Words);
            if (run.Count > 0 && (size / runSize > 1.12 || runSize / size > 1.12))
            {
                yield return MakeRawText(run);
                run.Clear();
            }
            if (run.Count == 0)
            {
                runSize = size;
            }
            run.Add(line);
        }
        if (run.Count > 0)
        {
            yield return MakeRawText(run);
        }
    }

    private static RawText MakeRawText(List<TextLine> lines)
    {
        List<Letter> list = lines.SelectMany((TextLine l) => l.Words).SelectMany((Word w) => w.Letters).ToList();
        double fontSize = LineFontSize(lines.SelectMany((TextLine l) => l.Words).ToList());
        int num = Enumerable.Count(list, (Letter l) => IsBoldFont(l.FontName));
        return new RawText(JoinLines(lines.Select((TextLine l) => l.Text)), lines.Max((TextLine l) => l.BoundingBox.Top), lines.Min((TextLine l) => l.BoundingBox.Bottom), fontSize, list.Count > 0 && (double)num > (double)list.Count * 0.6, lines.Count);
    }

    private static double LineFontSize(IReadOnlyList<Word> words)
    {
        List<double> list = (from l in words.SelectMany((Word w) => w.Letters)
            select (!(l.PointSize > 0.5)) ? l.BoundingBox.Height : l.PointSize into s
            where s > 0.0
            orderby s
            select s).ToList();
        if (list.Count != 0)
        {
            return list[list.Count / 2];
        }
        return 10.0;
    }

    private static bool IsBoldFont(string? fontName)
    {
        if (fontName != null)
        {
            if (!fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase) && !fontName.Contains("Black", StringComparison.OrdinalIgnoreCase) && !fontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase))
            {
                return fontName.Contains("Semibold", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }
        return false;
    }

    private static void ReadImages(Page page, ImageStore store, RawPage raw, ExtractStats stats)
    {
        double num = page.Width * page.Height;
        foreach (IPdfImage image in page.GetImages())
        {
            try
            {
                PdfRectangle bounds = image.Bounds;
                double num2 = ((num > 0.0) ? (Math.Abs(bounds.Width * bounds.Height) / num) : 0.0);
                if (image.WidthInSamples >= 40 && image.HeightInSamples >= 40 && !(num2 < 0.005))
                {
                    (byte[], string, string)? tuple = Encode(image);
                    if (!tuple.HasValue)
                    {
                        stats.UnsupportedImages++;
                        continue;
                    }
                    ImageAsset imageAsset = store.Add(tuple.Value.Item1, tuple.Value.Item2, tuple.Value.Item3, image.WidthInSamples, image.HeightInSamples);
                    raw.Images.Add(new RawImage(imageAsset.Id, bounds.Top, num2 > 0.55, num2, bounds.Left, bounds.Right, bounds.Bottom));
                }
            }
            catch
            {
                stats.UnsupportedImages++;
            }
        }
    }

    private static (byte[] Data, string Mime, string Ext)? Encode(IPdfImage img)
    {
        byte[] array = img.RawBytes.ToArray();
        if (array.Length > 3 && array[0] == byte.MaxValue && array[1] == 216 && array[2] == byte.MaxValue)
        {
            return (array, "image/jpeg", ".jpg");
        }
        if (img.TryGetPng(out byte[] bytes) && bytes != null && bytes.Length > 0)
        {
            return (bytes, "image/png", ".png");
        }
        return null;
    }

    private static void RemoveHeadersAndFooters(List<RawPage> pages)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>();
        foreach (RawPage p in pages)
        {
            foreach (string item in (from t in p.Texts
                where InMargin(t, p.Height)
                select Key(t.Text)).Distinct())
            {
                counts[item] = counts.GetValueOrDefault(item) + 1;
            }
        }
        int threshold = Math.Max(2, (int)Math.Ceiling((double)pages.Count * 0.3));
        foreach (RawPage p2 in pages)
        {
            p2.Texts.RemoveAll((RawText t) => InMargin(t, p2.Height) && (PageNumberRx.IsMatch(t.Text) || (pages.Count >= 3 && counts.GetValueOrDefault(Key(t.Text)) >= threshold)));
        }
        static bool InMargin(RawText t, double h)
        {
            if (!(t.Bottom > h * 0.92))
            {
                return t.Top < h * 0.08;
            }
            return true;
        }
        static string Key(string s)
        {
            return Regex.Replace(s.ToLowerInvariant(), "\\d+", "#").Trim();
        }
    }

    private static void RemoveRepeatedDecorations(List<RawPage> pages)
    {
        Dictionary<string, int> perImage = (from id in pages.SelectMany((RawPage p) => p.Images.Select((RawImage i) => i.ImageId).Distinct())
            group id by id).ToDictionary((IGrouping<string, string> g) => g.Key, (IGrouping<string, string> g) => g.Count());
        foreach (RawPage page in pages)
        {
            page.Images.RemoveAll((RawImage i) => perImage[i.ImageId] >= 3 && i.AreaRatio < 0.25);
        }
    }

    private static double ComputeBodyFontSize(List<RawPage> pages)
    {
        Dictionary<double, int> dictionary = new Dictionary<double, int>();
        foreach (RawText item in pages.SelectMany((RawPage p) => p.Texts))
        {
            double key = Math.Round(item.FontSize * 2.0) / 2.0;
            dictionary[key] = dictionary.GetValueOrDefault(key) + item.Text.Length;
        }
        if (dictionary.Count != 0)
        {
            return dictionary.MaxBy((KeyValuePair<double, int> kv) => kv.Value).Key;
        }
        return 10.0;
    }

    private static List<Block> BuildBlocks(List<RawPage> pages, double bodySize)
    {
        List<Block> list = new List<Block>();
        foreach (RawPage page in pages)
        {
            List<RawImage> list2 = page.Images.OrderByDescending((RawImage i) => i.Top).ToList();
            foreach (RawText text in page.Texts)
            {
                while (list2.Count > 0 && list2[0].Top >= text.Top)
                {
                    list.Add(new ImageBlock(list2[0].ImageId, list2[0].FullPage));
                    list2.RemoveAt(0);
                }
                int? num = HeadingLevel(text, bodySize);
                if (num.HasValue)
                {
                    int valueOrDefault = num.GetValueOrDefault();
                    list.Add(new HeadingBlock(text.Text, valueOrDefault));
                }
                else
                {
                    AppendParagraph(list, text.Text);
                }
            }
            foreach (RawImage item in list2)
            {
                list.Add(new ImageBlock(item.ImageId, item.FullPage));
            }
        }
        return list;
    }

    private static int? HeadingLevel(RawText t, double body)
    {
        if (t.Text.Length > 150 || t.LineCount > 3 || !t.Text.Any(char.IsLetter))
        {
            return null;
        }
        double num = t.FontSize / body;
        if (num >= 1.6)
        {
            return 1;
        }
        if (num >= 1.22)
        {
            return 2;
        }
        if (t.Bold && num >= 0.95 && t.LineCount == 1 && t.Text.Length <= 90 && !EndsSentence(t.Text))
        {
            return 3;
        }
        return null;
    }

    private static void AppendParagraph(List<Block> result, string text)
    {
        if (result.Count > 0)
        {
            if (result[result.Count - 1] is ParagraphBlock paragraphBlock && !EndsSentence(paragraphBlock.Text) && text.Length > 0 && char.IsLower(text[0]))
            {
                result[result.Count - 1] = new ParagraphBlock(JoinLines(new string[2] { paragraphBlock.Text, text }));
                return;
            }
        }
        result.Add(new ParagraphBlock(text));
    }

    private static bool EndsSentence(string s)
    {
        s = s.TrimEnd();
        if (s.Length > 0)
        {
            string text = s;
            return ".!?:;»\"”)…".Contains(text[text.Length - 1]);
        }
        return false;
    }

    public static string JoinLines(IEnumerable<string> lines)
    {
        StringBuilder stringBuilder = new StringBuilder();
        foreach (string line in lines)
        {
            string text = Clean(line);
            if (text.Length == 0)
            {
                continue;
            }
            if (stringBuilder.Length == 0)
            {
                stringBuilder.Append(text);
                continue;
            }
            if (stringBuilder[stringBuilder.Length - 1] == '\u00ad')
            {
                stringBuilder.Length--;
            }
            else
            {
                if (stringBuilder[stringBuilder.Length - 1] == '-' && stringBuilder.Length > 1)
                {
                    if (char.IsLetter(stringBuilder[stringBuilder.Length - 2]) && char.IsLower(text[0]))
                    {
                        stringBuilder.Length--;
                        goto IL_00be;
                    }
                }
                stringBuilder.Append(' ');
            }
            goto IL_00be;
            IL_00be:
            stringBuilder.Append(text);
        }
        return stringBuilder.ToString();
    }

    public static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }
        StringBuilder stringBuilder = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
            case 'ﬀ':
                stringBuilder.Append("ff");
                continue;
            case 'ﬁ':
                stringBuilder.Append("fi");
                continue;
            case 'ﬂ':
                stringBuilder.Append("fl");
                continue;
            case 'ﬃ':
                stringBuilder.Append("ffi");
                continue;
            case 'ﬄ':
                stringBuilder.Append("ffl");
                continue;
            case 'ﬅ':
            case 'ﬆ':
                stringBuilder.Append("st");
                continue;
            case '\t':
            case '\n':
            case '\r':
            case '\u00a0':
            case '\u2002':
            case '\u2003':
            case '\u2009':
                stringBuilder.Append(' ');
                continue;
            }
            if (!char.IsControl(c) && c != '\ufffd')
            {
                stringBuilder.Append(c);
            }
        }
        return Regex.Replace(stringBuilder.ToString(), " {2,}", " ").Trim();
    }

    private static bool LooksLikeRealTitle(string t)
    {
        if (t.Length >= 3 && !t.EndsWith(".doc", StringComparison.OrdinalIgnoreCase) && !t.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) && !t.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && !t.Equals("untitled", StringComparison.OrdinalIgnoreCase))
        {
            return !t.StartsWith("Microsoft Word", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}

public sealed class PageSelection
{
    private readonly List<(int From, int? To)> _ranges = new List<(int, int?)>();

    public static PageSelection Parse(string spec)
    {
        PageSelection pageSelection = new PageSelection();
        string[] array = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string text in array)
        {
            int num = text.IndexOf('-');
            if (num < 0)
            {
                int num2 = int.Parse(text);
                pageSelection._ranges.Add((num2, num2));
                continue;
            }
            int num3 = ((num == 0) ? 1 : int.Parse(text.Substring(0, num)));
            int? num4 = ((num == text.Length - 1) ? ((int?)null) : new int?(int.Parse(text.Substring(num + 1))));
            if (num3 < 1 || (num4.HasValue && num4 < num3))
            {
                throw new FormatException("Plage invalide : " + text);
            }
            pageSelection._ranges.Add((num3, num4));
        }
        if (pageSelection._ranges.Count == 0)
        {
            throw new FormatException("Sélection de pages vide");
        }
        return pageSelection;
    }

    public IEnumerable<int> Resolve(int pageCount)
    {
        return from n in _ranges.SelectMany(((int From, int? To) r) => Enumerable.Range(r.From, Math.Max(0, Math.Min(r.To.GetValueOrDefault(pageCount), pageCount) - r.From + 1))).Distinct()
            orderby n
            select n;
    }
}
