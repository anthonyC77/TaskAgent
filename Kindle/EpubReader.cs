using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TaskAgent.Kindle;

/// <summary>
/// Lit un EPUB (produit par TaskAgent ou un autre EPUB sans DRM) et le remet dans le livre intermédiaire,
/// pour l'écrire ensuite en Markdown sans repasser par les PDF ni l'OCR.
/// Ordre de lecture : la « spine » du fichier content.opf ; titres h1-h6, paragraphes, listes, citations, code et images.
/// </summary>
public static class EpubReader
{
    private static readonly HashSet<string> ParagraphTags = new() { "p", "pre", "blockquote", "dt", "dd", "figcaption", "caption", "td", "th" };
    private static readonly HashSet<string> SkipTags = new() { "script", "style", "head", "nav" };
    private static readonly Dictionary<string, string> ImageTypes = new()
    {
        [".png"] = "image/png", [".gif"] = "image/gif", [".svg"] = "image/svg+xml", [".webp"] = "image/webp",
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
    };

    public static Book Read(string epubPath, Action<string> log)
    {
        using var zip = ZipFile.OpenRead(epubPath);

        // 1. META-INF/container.xml → chemin du content.opf
        var container = Load(zip, "META-INF/container.xml") ?? throw new InvalidDataException("EPUB invalide : container.xml manquant.");
        var opfPath = container.Descendants().First(e => e.Name.LocalName == "rootfile").Attribute("full-path")!.Value;
        var opf = Load(zip, opfPath) ?? throw new InvalidDataException("EPUB invalide : " + opfPath + " illisible.");
        var opfDir = Dir(opfPath);

        string? Meta(string name) => opf.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        var book = new Book
        {
            Title = Regex.Replace(Meta("title") ?? Path.GetFileNameWithoutExtension(epubPath), @"\s\((?:v\d+|corrigé)\)$", ""),
            Author = Meta("creator"),
        };

        // 2. Manifeste (id → fichier) et ordre de lecture (spine)
        var manifest = opf.Descendants().Where(e => e.Name.LocalName == "item")
            .ToDictionary(e => e.Attribute("id")!.Value, e => (Href: Uri.UnescapeDataString(e.Attribute("href")!.Value), Props: e.Attribute("properties")?.Value ?? ""));
        var spine = opf.Descendants().Where(e => e.Name.LocalName == "itemref")
            .Select(e => e.Attribute("idref")?.Value).Where(id => id is not null && manifest.ContainsKey(id))
            .Select(id => manifest[id!]).Where(m => !m.Props.Contains("nav")).ToList();

        // Un seul chapitre : les titres de l'EPUB (h1, h2…) donnent déjà la structure.
        var chapter = new Chapter { Title = book.Title };
        foreach (var item in spine)
        {
            var path = Combine(opfDir, item.Href);
            if (Load(zip, path, html: true) is not { } doc) { log($"   ! page illisible ignorée : {item.Href}"); continue; }
            var body = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "body");
            if (body is not null) Walk(body, chapter.Blocks, zip, Dir(path), book.Images);
        }
        book.Chapters.Add(chapter);
        ReadCover(opf, opfDir, zip, book, chapter);
        book.Language = ResolveLanguage(Meta("language"), BookBuilder.SampleText(book));
        log($"   {spine.Count} page(s) lue(s), {chapter.Blocks.Count} bloc(s), {book.Images.All.Count} image(s)");
        return book;
    }

    /// <summary>
    /// Les métadonnées d'un EPUB se trompent souvent de langue : le texte a le dernier mot, sauf pour une langue
    /// que le détecteur ne connaît pas (il y verrait à tort du français ou de l'espagnol).
    /// </summary>
    internal static string ResolveLanguage(string? declared, string sampleText)
    {
        var code = declared?.Split('-')[0].Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(code) || code == "und") code = null;   // « und » = langue non précisée
        if (code is not null && !LanguageDetector.Knows(code)) return code;
        return LanguageDetector.Detect(sampleText, code ?? "fr");
    }

    /// <summary>
    /// Couverture déclarée par l'EPUB 3 (properties="cover-image") ou l'EPUB 2 (meta name="cover") : elle devient la vraie
    /// couverture du livre, et on la retire du début du texte pour ne pas l'afficher deux fois.
    /// </summary>
    private static void ReadCover(XDocument opf, string opfDir, ZipArchive zip, Book book, Chapter chapter)
    {
        var items = opf.Descendants().Where(e => e.Name.LocalName == "item").ToList();
        var coverId = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "meta" && e.Attribute("name")?.Value == "cover")?.Attribute("content")?.Value;
        var item = items.FirstOrDefault(e => (e.Attribute("properties")?.Value ?? "").Split(' ').Contains("cover-image"))
                   ?? items.FirstOrDefault(e => e.Attribute("id")?.Value == coverId);
        if (item?.Attribute("href")?.Value is not { } href) return;

        var entry = zip.GetEntry(Combine(opfDir, Uri.UnescapeDataString(href)));
        if (entry is null || !ImageTypes.ContainsKey(Path.GetExtension(entry.Name).ToLowerInvariant())) return;

        book.CoverImageId = StoreImage(entry, book.Images).Id;
        var duplicate = chapter.Blocks.Take(5).ToList().FindIndex(b => b is ImageBlock i && i.ImageId == book.CoverImageId);
        if (duplicate >= 0) chapter.Blocks.RemoveAt(duplicate);
    }

    /// <summary>Parcours d'une page : un bloc par titre, paragraphe, élément de liste ou image ; les conteneurs (div, section…) sont traversés.</summary>
    private static void Walk(XElement element, List<Block> blocks, ZipArchive zip, string dir, ImageStore images)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText t)
            {
                // Texte posé directement dans un conteneur (sans <p>)
                if (Clean(t.Value) is { Length: > 0 } loose) blocks.Add(new ParagraphBlock(loose));
                continue;
            }
            if (node is not XElement e) continue;
            var tag = e.Name.LocalName.ToLowerInvariant();
            if (SkipTags.Contains(tag)) continue;

            if (tag.Length == 2 && tag[0] == 'h' && char.IsDigit(tag[1]))
            {
                if (Clean(e.Value) is { Length: > 0 } h) blocks.Add(new HeadingBlock(h, tag[1] - '0'));
                AddImages(e, blocks, zip, dir, images);
            }
            else if (tag == "li")
            {
                // Un élément de liste peut contenir une sous-liste : on garde son texte propre, puis les sous-éléments.
                var own = string.Concat(e.Nodes().Where(n => n is XText || n is XElement x && x.Name.LocalName is not ("ul" or "ol"))
                    .Select(n => n is XElement x ? x.Value : ((XText)n).Value));
                if (Clean(own) is { Length: > 0 } li) blocks.Add(new ParagraphBlock("• " + li));
                foreach (var sub in e.Elements().Where(x => x.Name.LocalName is "ul" or "ol")) Walk(sub, blocks, zip, dir, images);
            }
            else if (ParagraphTags.Contains(tag) && !e.Elements().Any(x => x.Name.LocalName is "p" or "div" or "ul" or "ol" or "table"))
            {
                if (Clean(e.Value) is { Length: > 0 } p) blocks.Add(new ParagraphBlock(p));
                AddImages(e, blocks, zip, dir, images);
            }
            else if (tag is "img" or "image") AddImage(e, blocks, zip, dir, images);
            else Walk(e, blocks, zip, dir, images);
        }
    }

    private static void AddImages(XElement e, List<Block> blocks, ZipArchive zip, string dir, ImageStore images)
    {
        foreach (var img in e.Descendants().Where(x => x.Name.LocalName is "img" or "image")) AddImage(img, blocks, zip, dir, images);
    }

    private static void AddImage(XElement img, List<Block> blocks, ZipArchive zip, string dir, ImageStore images)
    {
        var src = img.Attribute("src")?.Value ?? img.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;
        if (string.IsNullOrEmpty(src) || src.StartsWith("data:") || src.Contains("://")) return;
        var entry = zip.GetEntry(Combine(dir, Uri.UnescapeDataString(src)));
        if (entry is null) return;
        blocks.Add(new ImageBlock(StoreImage(entry, images).Id, false));
    }

    private static ImageAsset StoreImage(ZipArchiveEntry entry, ImageStore images)
    {
        using var ms = new MemoryStream();
        using (var s = entry.Open()) s.CopyTo(ms);
        var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
        return images.Add(ms.ToArray(), ImageTypes.GetValueOrDefault(ext, "image/jpeg"), ext.Length > 0 ? ext : ".jpg", 0, 0);
    }

    private static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static XDocument? Load(ZipArchive zip, string path, bool html = false)
    {
        var entry = zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open());
        var text = reader.ReadToEnd();
        try
        {
            using var xr = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            return XDocument.Load(xr);
        }
        catch (XmlException) when (html)
        {
            // Quelques EPUB contiennent des entités HTML (&nbsp;…) non déclarées : on les remplace et on réessaie.
            text = Regex.Replace(text, @"&(?!amp;|lt;|gt;|quot;|apos;|#)(\w+);", m => System.Net.WebUtility.HtmlDecode(m.Value));
            try
            {
                using var xr = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
                return XDocument.Load(xr);
            }
            catch (XmlException) { return null; }
        }
    }

    private static string Dir(string path) => path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";

    /// <summary>« OEBPS/text/ » + « ../images/a.png » → « OEBPS/images/a.png » (chemins internes du zip).</summary>
    private static string Combine(string dir, string href)
    {
        var parts = new List<string>(dir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var p in href.Split('#')[0].Split('/'))
        {
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (p is not ("." or "")) parts.Add(p);
        }
        return string.Join('/', parts);
    }
}
