using System.Collections.Generic;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System;
using TaskAgent.Kindle.Translation;

namespace TaskAgent.Kindle;

public enum TranslateMode
{
    Off,
    Auto,
    Force
}

public enum OutputFormat
{
    Epub,
    Markdown
}

public sealed class KindleJob
{
    public string Input { get; set; } = "";

    public string? Output { get; set; }

    public OutputFormat Format { get; set; }

    public string? Pages { get; set; }

    public bool Recursive { get; set; }

    public bool Separate { get; set; }

    public List<string>? Files { get; set; }

    public string? Title { get; set; }

    public string? Author { get; set; }

    public string? Language { get; set; }

    public string? Cover { get; set; }

    public bool IncludeImages { get; set; } = true;

    public bool KeepHeadersFooters { get; set; }

    public OcrMode Ocr { get; set; } = OcrMode.Auto;

    public string OcrLanguages { get; set; } = "fra+eng";

    public TranslateMode Translate { get; set; }

    public string TargetLanguage { get; set; } = "fr";

    public bool TranslatedOnly { get; set; }

    public bool AutoGlossary { get; set; } = true;

    public string? Glossary { get; set; }

    public string? Instructions { get; set; }

    public int Concurrency { get; set; } = 3;

    public bool FreshStart { get; set; }
}

public static class KindleConverter
{
    public const string SendToKindleUrl = "https://www.amazon.fr/sendtokindle";

    public static string EpubToMarkdown(string epubPath, Action<string> log)
    {
        Book book = EpubReader.Read(epubPath, log);
        int num = TextFixes.Apply(book);
        if (num > 0)
        {
            log($"Corrections OCR : {num} liste(s) à puces reconstituée(s).");
        }
        string text = UniquePath(Path.ChangeExtension(epubPath, ".md"));
        MarkdownWriter.Write(book, text);
        log($"✓ {text} ({new FileInfo(text).Length / 1024} Ko)");
        return text;
    }

    public static async Task<string?> TranslateEpubAsync(KindleJob o, ILlmClient llm, Action<string> log, CancellationToken ct)
    {
        Book book = await Task.Run(() => EpubReader.Read(o.Input, log), ct);
        if (!book.Chapters.SelectMany((Chapter c) => c.Blocks).Any((Block b) => (b is ParagraphBlock || b is HeadingBlock) ? true : false))
        {
            throw new InvalidDataException("Aucun texte lisible dans cet EPUB (il est peut-être protégé par un DRM).");
        }
        if (!string.IsNullOrWhiteSpace(o.Title))
        {
            book.Title = o.Title;
        }
        if (!string.IsNullOrWhiteSpace(o.Author))
        {
            book.Author = o.Author;
        }
        log("Langue détectée : " + book.Language);
        if (o.Translate != TranslateMode.Force && book.Language == o.TargetLanguage)
        {
            log("Texte déjà en « " + o.TargetLanguage + " » : pas de traduction (choisis « Toujours » pour forcer).");
            return null;
        }
        return await TranslateAndWriteAsync(o, book, o.Input, llm, fromEpub: true, log, ct);
    }

    private static async Task<string> TranslateAndWriteAsync(KindleJob o, Book book, string basePath, ILlmClient llm, bool fromEpub, Action<string> log, CancellationToken ct)
    {
        string stem = Path.Combine(Path.GetDirectoryName(basePath), Path.GetFileNameWithoutExtension(basePath));
        string text = WriteBook(o, await new BookTranslator(llm, new TranslationOptions
        {
            TargetLanguage = o.TargetLanguage,
            Concurrency = o.Concurrency,
            AutoGlossary = o.AutoGlossary,
            GlossaryFile = (string.IsNullOrWhiteSpace(o.Glossary) ? null : o.Glossary),
            GlossaryOutput = stem + ".glossaire.json",
            CacheFile = stem + ".cache-traduction.json",
            ExtraInstructions = o.Instructions,
            FromEpub = fromEpub
        }, log).TranslateAsync(book, ct), stem + "." + o.TargetLanguage + Extension(o), log);
        log($"✓ {text} ({new FileInfo(text).Length / 1024} Ko)");
        return text;
    }

    private static string Extension(KindleJob o)
    {
        if (o.Format != OutputFormat.Markdown)
        {
            return ".epub";
        }
        return ".md";
    }

    private static string WriteBook(KindleJob o, Book book, string path, Action<string> log)
    {
        string text = UniquePath(path);
        if (o.Format == OutputFormat.Markdown)
        {
            MarkdownWriter.Write(book, text);
        }
        else
        {
            EpubWriter.Write(book, text);
        }
        if (text != path)
        {
            log($"(« {Path.GetFileName(path)} » existe déjà : nouvelle version « {Path.GetFileName(text)} »)");
        }
        return text;
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }
        string directoryName = Path.GetDirectoryName(path);
        string value = Regex.Replace(Path.GetFileNameWithoutExtension(path), " v\\d+$", "");
        string extension = Path.GetExtension(path);
        int num = 2;
        string text;
        while (true)
        {
            text = Path.Combine(directoryName, $"{value} v{num}{extension}");
            if (!File.Exists(text))
            {
                break;
            }
            num++;
        }
        return text;
    }

    public static List<string> ListPdfs(string folder, bool recursive)
    {
        return ChapterOrder.Sort(from f in Directory.EnumerateFiles(folder, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            where f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            select f, folder);
    }

    public static async Task<List<string>> RunAsync(KindleJob o, ILlmClient? llm, Action<string> log, CancellationToken ct)
    {
        List<string> produced = new List<string>();
        string text = Extension(o);
        ExtractionCache.Prune();
        string fullPath = Path.GetFullPath(o.Input);
        bool flag = Directory.Exists(fullPath);
        List<string> list;
        if (flag)
        {
            List<string> files = o.Files;
            list = ((files != null && files.Count > 0) ? files : ListPdfs(fullPath, o.Recursive));
            if (list.Count == 0)
            {
                throw new FileNotFoundException("Aucun PDF dans " + fullPath);
            }
        }
        else
        {
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Introuvable : " + fullPath);
            }
            list = new List<string> { fullPath };
        }
        if (o.Translate != TranslateMode.Off && llm == null)
        {
            throw new InvalidOperationException("Traduction demandée mais aucun modèle n'est configuré.");
        }
        ExtractOptions extractOpt = new ExtractOptions
        {
            Pages = (string.IsNullOrWhiteSpace(o.Pages) ? null : PageSelection.Parse(o.Pages)),
            IncludeImages = o.IncludeImages,
            KeepHeadersFooters = o.KeepHeadersFooters,
            Ocr = o.Ocr,
            OcrLanguages = (string.IsNullOrWhiteSpace(o.OcrLanguages) ? "fra+eng" : o.OcrLanguages.Trim()),
            Cancel = ct,
            PagesSpec = o.Pages?.Trim(),
            UseCache = !o.FreshStart
        };
        List<(List<string>, string, string)> list2 = new List<(List<string>, string, string)>();
        if (flag && o.Separate)
        {
            foreach (string item in list)
            {
                list2.Add((new List<string> { item }, BookBuilder.PrettifyFileName(item), Path.Combine(o.Output ?? Path.GetDirectoryName(item), Path.GetFileNameWithoutExtension(item) + text)));
            }
        }
        else
        {
            string text2 = (flag ? new DirectoryInfo(fullPath).Name : Path.GetFileNameWithoutExtension(fullPath));
            string text3 = o.Output ?? Path.Combine(flag ? fullPath : Path.GetDirectoryName(fullPath), text2 + text);
            if (Directory.Exists(text3))
            {
                text3 = Path.Combine(text3, text2 + text);
            }
            list2.Add((list, flag ? text2 : BookBuilder.PrettifyFileName(fullPath), text3));
        }
        int failures = 0;
        foreach (var job in list2)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Book book = await Task.Run(() => BookBuilder.Build(job.Item1, job.Item2, extractOpt, log), ct);
                if (book.Chapters.Count == 0)
                {
                    log("Rien à convertir.");
                    failures++;
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(o.Title))
                {
                    book.Title = o.Title;
                }
                if (!string.IsNullOrWhiteSpace(o.Author))
                {
                    book.Author = o.Author;
                }
                if (!string.IsNullOrWhiteSpace(o.Language))
                {
                    book.Language = o.Language;
                }
                if (!string.IsNullOrWhiteSpace(o.Cover))
                {
                    byte[] data = await File.ReadAllBytesAsync(o.Cover, ct);
                    bool flag2 = o.Cover.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                    book.CoverImageId = book.Images.Add(data, flag2 ? "image/png" : "image/jpeg", flag2 ? ".png" : ".jpg", 0, 0).Id;
                }
                log("Langue détectée : " + book.Language);
                bool flag3 = llm != null && o.Translate != TranslateMode.Off && (o.Translate == TranslateMode.Force || book.Language != o.TargetLanguage);
                if (!o.TranslatedOnly || !flag3)
                {
                    string text4 = WriteBook(o, book, job.Item3, log);
                    produced.Add(text4);
                    log($"✓ {text4} ({new FileInfo(text4).Length / 1024} Ko)");
                }
                if (o.Translate != TranslateMode.Off && !flag3)
                {
                    log("Texte déjà en « " + o.TargetLanguage + " » : pas de traduction (choisis « Toujours » pour forcer).");
                }
                if (flag3)
                {
                    List<string> list3 = produced;
                    list3.Add(await TranslateAndWriteAsync(o, book, job.Item3, llm, fromEpub: false, log, ct));
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                log("✗ " + job.Item3 + " : " + ex.Message);
                failures++;
            }
        }
        if (failures > 0)
        {
            log($"{failures} livre(s) en échec. Relance « Convertir » : les PDF déjà lus sont repris sans refaire l'OCR.");
        }
        return produced;
    }
}
