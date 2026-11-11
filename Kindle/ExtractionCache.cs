using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskAgent.Kindle;

/// <summary>
/// Mémoire du travail long (lecture du PDF + OCR), PDF par PDF : si la conversion échoue à la fin (fichier ouvert ailleurs,
/// coupure, traduction interrompue…), la relance reprend le texte déjà lu en quelques secondes au lieu de refaire l'OCR.
/// La clé change si le PDF est modifié ou si les réglages d'extraction changent (pages, images, OCR, langues).
/// Analogie : on garde ses brouillons ; on ne recopie que ce qui manque.
/// </summary>
public static class ExtractionCache
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskAgent", "cache-pdf");

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private sealed record BlockDto(string K, string? T, int L, string? Img, bool Full);
    private sealed record ImageDto(string File, string MediaType, string Ext, int W, int H);
    private sealed record StatsDto(int Pages, int Images, int UnsupportedImages, int OcrPages, List<string> Warnings);
    private sealed record EntryDto(string? Title, string? Author, List<BlockDto> Blocks, List<ImageDto> Images, StatsDto Stats);

    /// <summary>Résultat en mémoire s'il existe et que <paramref name="opt"/>.UseCache est vrai ; sinon extraction puis mise en mémoire.</summary>
    public static PdfContent GetOrExtract(string file, ImageStore store, ExtractOptions opt, Action<string> log)
    {
        var dir = Path.Combine(Dir, Key(file, opt));
        if (opt.UseCache && TryLoad(dir, store) is { } cached)
        {
            log("   ♻ déjà lu lors d'un essai précédent : repris tel quel (pas de nouvel OCR)");
            return cached;
        }
        var content = PdfExtractor.Extract(file, store, opt, log);
        try { Save(dir, content, store); }
        catch (Exception ex) { log("   ! mise en mémoire impossible (la conversion continue) : " + ex.Message); }
        return content;
    }

    /// <summary>Supprime les entrées de plus de 30 jours (appelé au début de chaque conversion).</summary>
    public static void Prune()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var d in new DirectoryInfo(Dir).GetDirectories())
                if (DateTime.UtcNow - d.LastWriteTimeUtc > MaxAge)
                    try { d.Delete(recursive: true); } catch { }
        }
        catch { }
    }

    private static string Key(string file, ExtractOptions opt)
    {
        var info = new FileInfo(file);
        var raw = string.Join("|", info.FullName.ToLowerInvariant(), info.Length, info.LastWriteTimeUtc.Ticks,
            opt.PagesSpec ?? "", opt.IncludeImages, opt.KeepHeadersFooters, opt.Ocr, opt.OcrLanguages, opt.OcrDpi, "v2"); // v2 : schémas découpés en image au lieu de charabia
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..20];
    }

    private static void Save(string dir, PdfContent content, ImageStore store)
    {
        var tmp = dir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Directory.CreateDirectory(tmp);

        var images = new List<ImageDto>();
        var blocks = new List<BlockDto>();
        foreach (var b in content.Blocks)
        {
            switch (b)
            {
                case HeadingBlock h: blocks.Add(new BlockDto("h", h.Text, h.Level, null, false)); break;
                case ParagraphBlock p: blocks.Add(new BlockDto("p", p.Text, 0, null, false)); break;
                case ImageBlock i when store.Get(i.ImageId) is { } a:
                    if (images.All(x => x.File != a.FileName))
                    {
                        File.WriteAllBytes(Path.Combine(tmp, a.FileName), a.Data);
                        images.Add(new ImageDto(a.FileName, a.MediaType, a.Extension, a.Width, a.Height));
                    }
                    blocks.Add(new BlockDto("i", null, 0, a.FileName, i.FullPage));
                    break;
            }
        }
        var s = content.Stats;
        var entry = new EntryDto(content.Title, content.Author, blocks, images,
            new StatsDto(s.Pages, s.Images, s.UnsupportedImages, s.OcrPages, s.Warnings));
        File.WriteAllText(Path.Combine(tmp, "content.json"), JsonSerializer.Serialize(entry));

        // Écrit à part puis renommé : une entrée à moitié écrite (coupure) n'est jamais relue.
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.Move(tmp, dir);
    }

    private static PdfContent? TryLoad(string dir, ImageStore store)
    {
        try
        {
            var json = Path.Combine(dir, "content.json");
            if (!File.Exists(json)) return null;
            var entry = JsonSerializer.Deserialize<EntryDto>(File.ReadAllText(json));
            if (entry is null) return null;

            // Les images reprennent un identifiant dans le livre en cours (les numéros diffèrent d'un livre à l'autre).
            var ids = new Dictionary<string, string>();
            foreach (var img in entry.Images)
                ids[img.File] = store.Add(File.ReadAllBytes(Path.Combine(dir, img.File)), img.MediaType, img.Ext, img.W, img.H).Id;

            var blocks = new List<Block>();
            foreach (var b in entry.Blocks)
            {
                Block? block = b.K switch
                {
                    "h" => new HeadingBlock(b.T ?? "", b.L),
                    "p" => new ParagraphBlock(b.T ?? ""),
                    "i" when b.Img is not null && ids.TryGetValue(b.Img, out var id) => new ImageBlock(id, b.Full),
                    _ => null
                };
                if (block is not null) blocks.Add(block);
            }

            var stats = new ExtractStats
            {
                Pages = entry.Stats.Pages, Images = entry.Stats.Images,
                UnsupportedImages = entry.Stats.UnsupportedImages, OcrPages = entry.Stats.OcrPages
            };
            stats.Warnings.AddRange(entry.Stats.Warnings);
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow); // encore utile : repousse le ménage
            return new PdfContent(entry.Title, entry.Author, blocks, stats);
        }
        catch { return null; }
    }
}
