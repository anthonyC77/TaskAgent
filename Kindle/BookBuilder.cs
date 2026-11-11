using System.Text.RegularExpressions;

namespace TaskAgent.Kindle;

/// <summary>Assemble un ou plusieurs PDF en un livre.</summary>
public static class BookBuilder
{
    public static Book Build(IReadOnlyList<string> pdfFiles, string defaultTitle, ExtractOptions opt, Action<string> log)
    {
        var book = new Book { Title = defaultTitle };
        var allStats = new List<ExtractStats>();
        string? firstPdfTitle = null, firstAuthor = null;

        for (var i = 0; i < pdfFiles.Count; i++)
        {
            var file = pdfFiles[i];
            log($"[{i + 1}/{pdfFiles.Count}] {Path.GetFileName(file)}");
            PdfContent content;
            try
            {
                content = ExtractionCache.GetOrExtract(file, book.Images, opt, log);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"   ✗ illisible : {ex.Message}");
                continue;
            }

            allStats.Add(content.Stats);
            var s = content.Stats;
            log($"   {s.Pages} page(s), {s.Images} image(s)" +
                (s.OcrPages > 0 ? $", {s.OcrPages} page(s) scannée(s) lue(s) par OCR" : "") +
                (s.UnsupportedImages > 0 ? $", {s.UnsupportedImages} image(s) non convertible(s) (JPEG2000/JBIG2)" : ""));
            foreach (var w in s.Warnings) log("   ! " + w);

            firstPdfTitle ??= content.Title;
            firstAuthor ??= content.Author;

            var chapterTitle = pdfFiles.Count == 1
                ? defaultTitle
                : content.Title ?? PrettifyFileName(file);
            var chapter = new Chapter { Title = chapterTitle, SourceFile = file };
            chapter.Blocks.AddRange(content.Blocks);
            book.Chapters.Add(chapter);
        }

        if (pdfFiles.Count == 1 && firstPdfTitle is not null) book.Title = firstPdfTitle;
        book.Author = firstAuthor;

        // Couverture : une image pleine page en tout début de livre.
        var firstBlocks = book.Chapters.FirstOrDefault()?.Blocks.Take(3) ?? Enumerable.Empty<Block>();
        if (firstBlocks.OfType<ImageBlock>().FirstOrDefault(b => b.FullPage) is { } coverBlock)
        {
            book.CoverImageId = coverBlock.ImageId;
            book.Chapters[0].Blocks.Remove(coverBlock); // évite d'afficher la couverture deux fois
        }

        book.Images.RemoveUnused(book.Chapters.SelectMany(c => c.Blocks).OfType<ImageBlock>().Select(b => b.ImageId)
            .Append(book.CoverImageId ?? ""));
        book.Language = LanguageDetector.Detect(SampleText(book));
        if (TextFixes.Apply(book) is var fixes and > 0) log($"Corrections OCR : {fixes} liste(s) à puces reconstituée(s).");
        return book;
    }

    public static string SampleText(Book book, int maxChars = 20_000)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in book.Chapters.SelectMany(c => c.Blocks))
        {
            if (b is ParagraphBlock p) sb.Append(p.Text).Append(' ');
            if (sb.Length > maxChars) break;
        }
        return sb.ToString();
    }

    /// <summary>« 02_mon-super_chapitre.pdf » → « Mon super chapitre ».</summary>
    public static string PrettifyFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        name = Regex.Replace(name, @"^\d+[\s._-]*", "");
        name = Regex.Replace(name, @"[_]+|(?<=\w)-(?=\w)", " ").Trim();
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(path);
        return char.ToUpper(name[0]) + name[1..];
    }

    /// <summary>Tri « naturel » : chap2 avant chap10.</summary>
    public static int NaturalCompare(string? a, string? b)
    {
        static string Pad(string s) => Regex.Replace(s, @"\d+", m => m.Value.PadLeft(10, '0'));
        return string.Compare(Pad(a ?? ""), Pad(b ?? ""), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Détection de langue très simple mais efficace sur un texte long :
/// on compte les « petits mots » caractéristiques de chaque langue.
/// </summary>
public static class LanguageDetector
{
    private static readonly Dictionary<string, HashSet<string>> StopWords = new()
    {
        ["en"] = new() { "the", "and", "of", "to", "is", "in", "that", "it", "for", "with", "as", "was", "on", "are", "this", "be", "by", "which", "from", "have", "not", "you", "we", "they" },
        ["fr"] = new() { "le", "la", "les", "de", "des", "et", "est", "un", "une", "du", "que", "qui", "dans", "pour", "pas", "sur", "au", "avec", "ce", "il", "elle", "nous", "vous", "sont" },
        ["es"] = new() { "el", "los", "las", "y", "es", "por", "con", "para", "una", "como", "pero", "sus", "está", "son" },
        ["de"] = new() { "der", "die", "und", "das", "ist", "nicht", "ein", "eine", "mit", "auf", "für", "sich", "dem", "den" },
    };

    public static bool Knows(string code) => StopWords.ContainsKey(code);

    public static string Detect(string text, string fallback = "fr")
    {
        var words = Regex.Matches(text.ToLowerInvariant(), @"\p{L}+").Select(m => m.Value).Take(5000).ToList();
        if (words.Count < 30) return fallback;
        var scores = StopWords.ToDictionary(kv => kv.Key, kv => words.Count(w => kv.Value.Contains(w)));
        var best = scores.MaxBy(kv => kv.Value);
        return best.Value < words.Count * 0.05 ? fallback : best.Key;
    }
}
