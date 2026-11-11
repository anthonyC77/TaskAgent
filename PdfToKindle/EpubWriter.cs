using System.IO.Compression;
using System.Text;

namespace PdfToKindle;

/// <summary>
/// Écrit un EPUB 3 (avec toc.ncx pour la rétro-compatibilité) accepté par « Envoyer vers Kindle ».
/// Un EPUB, c'est simplement un ZIP contenant des pages XHTML, une feuille CSS,
/// les images et un « sommaire » (content.opf) qui décrit le tout.
/// </summary>
public static class EpubWriter
{
    // Kindle découpe mal les très gros fichiers XHTML : on vise ~120 Ko par fichier.
    private const int MaxPartChars = 120_000;

    private sealed record NavEntry(string Title, string Href, int Level);
    private sealed record Part(string FileName, string Xhtml);

    public static void Write(Book book, string outputPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var parts = new List<Part>();
        var nav = new List<NavEntry>();
        var singleChapter = book.Chapters.Count == 1;
        var headingCounter = 0;

        for (var ci = 0; ci < book.Chapters.Count; ci++)
            RenderChapter(book, book.Chapters[ci], ci, singleChapter, parts, nav, ref headingCounter);

        if (nav.Count == 0 && parts.Count > 0)
            nav.Add(new NavEntry(book.Title, parts[0].FileName, 1));

        var usedImages = book.Images.All;
        var cover = book.CoverImageId is null ? null : book.Images.Get(book.CoverImageId);
        var uid = "urn:uuid:" + Guid.NewGuid();

        if (File.Exists(outputPath)) File.Delete(outputPath);
        using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create);

        // 1) « mimetype » DOIT être le premier fichier, non compressé.
        AddText(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        AddText(zip, "META-INF/container.xml", ContainerXml);
        AddText(zip, "OEBPS/style.css", Css);

        if (cover is not null)
            AddText(zip, "OEBPS/text/cover.xhtml", CoverXhtml(book, cover));

        foreach (var p in parts)
            AddText(zip, "OEBPS/text/" + p.FileName, p.Xhtml);

        foreach (var img in usedImages)
        {
            var e = zip.CreateEntry("OEBPS/images/" + img.FileName, CompressionLevel.NoCompression);
            using var s = e.Open();
            s.Write(img.Data);
        }

        AddText(zip, "OEBPS/nav.xhtml", NavXhtml(book, nav, cover is not null));
        AddText(zip, "OEBPS/toc.ncx", Ncx(book, nav, uid));
        AddText(zip, "OEBPS/content.opf", Opf(book, parts, usedImages, cover, uid));
    }

    // ------------------------------------------------------------------ chapitres

    private static void RenderChapter(Book book, Chapter ch, int chapterIndex, bool singleChapter,
        List<Part> parts, List<NavEntry> nav, ref int headingCounter)
    {
        var partIndex = 0;
        var body = new StringBuilder();
        string FileName(int pi) => $"ch{chapterIndex + 1:D3}_{pi + 1:D2}.xhtml";

        void Flush()
        {
            if (body.Length == 0) return;
            parts.Add(new Part(FileName(partIndex), Page(book, ch.Title, body.ToString())));
            body.Clear();
            partIndex++;
        }

        // En mode « plusieurs PDF », chaque PDF devient un chapitre avec son titre.
        if (!singleChapter && !string.IsNullOrWhiteSpace(ch.Title))
        {
            var id = $"h{++headingCounter}";
            body.Append($"<h1 class=\"chapter\" id=\"{id}\">{Esc(ch.Title)}</h1>\n");
            nav.Add(new NavEntry(ch.Title, $"text/{FileName(partIndex)}#{id}", 1));
        }

        foreach (var block in ch.Blocks)
        {
            // Coupure de fichier avant un grand titre ou quand le fichier devient gros.
            if (body.Length > MaxPartChars || (block is HeadingBlock { Level: 1 } && body.Length > 20_000))
                Flush();

            switch (block)
            {
                case HeadingBlock h:
                {
                    var id = $"h{++headingCounter}";
                    // Les titres de PDF descendent d'un niveau quand le PDF est lui-même un chapitre.
                    var level = Math.Clamp(singleChapter ? h.Level : h.Level + 1, 1, 4);
                    body.Append($"<h{level} id=\"{id}\">{Esc(h.Text)}</h{level}>\n");
                    var navLevel = singleChapter ? h.Level : h.Level + 1;
                    if (navLevel <= 3)
                        nav.Add(new NavEntry(h.Text, $"text/{FileName(partIndex)}#{id}", navLevel));
                    break;
                }
                case ParagraphBlock p:
                    body.Append("<p>").Append(Esc(p.Text)).Append("</p>\n");
                    break;
                case ImageBlock img:
                {
                    var asset = book.Images.Get(img.ImageId);
                    if (asset is null) break;
                    var cls = img.FullPage ? "figure fullpage" : "figure";
                    body.Append($"<div class=\"{cls}\"><img src=\"../images/{asset.FileName}\" alt=\"\"/></div>\n");
                    break;
                }
            }
        }
        Flush();
    }

    // ------------------------------------------------------------------ gabarits XML

    private const string ContainerXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """;

    // CSS volontairement sobre : le Kindle doit rester libre de la police et de la taille.
    private const string Css =
        """
        body { margin: 0 2%; }
        h1, h2, h3, h4 { text-align: left; line-height: 1.25; page-break-after: avoid; }
        h1 { font-size: 1.6em; margin: 1.2em 0 0.6em; }
        h1.chapter { page-break-before: always; margin-top: 2em; }
        h2 { font-size: 1.3em; margin: 1em 0 0.5em; }
        h3 { font-size: 1.1em; margin: 0.9em 0 0.4em; }
        h4 { font-size: 1em; margin: 0.8em 0 0.3em; }
        p { margin: 0 0 0.45em; text-indent: 1.2em; text-align: justify; }
        h1 + p, h2 + p, h3 + p, h4 + p, div.figure + p { text-indent: 0; }
        div.figure { text-align: center; margin: 0.8em 0; page-break-inside: avoid; }
        div.figure img { max-width: 100%; height: auto; }
        div.fullpage { page-break-before: always; page-break-after: always; margin: 0; }
        div.fullpage img { width: 100%; }
        div.cover { text-align: center; margin: 0; padding: 0; }
        div.cover img { max-width: 100%; max-height: 100%; }
        """;

    private static string Page(Book book, string title, string body) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="{book.Language}" lang="{book.Language}">
        <head>
          <meta charset="UTF-8"/>
          <title>{Esc(string.IsNullOrWhiteSpace(title) ? book.Title : title)}</title>
          <link rel="stylesheet" type="text/css" href="../style.css"/>
        </head>
        <body>
        {body}</body>
        </html>
        """;

    private static string CoverXhtml(Book book, ImageAsset cover) =>
        Page(book, book.Title,
            $"<div class=\"cover\"><img src=\"../images/{cover.FileName}\" alt=\"{Esc(book.Title)}\"/></div>\n");

    private static string NavXhtml(Book book, List<NavEntry> nav, bool hasCover)
    {
        var sb = new StringBuilder();
        sb.Append($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="{book.Language}" lang="{book.Language}">
            <head><meta charset="UTF-8"/><title>{Esc(book.Title)}</title></head>
            <body>
            <nav epub:type="toc" id="toc"><h1>{(book.Language == "fr" ? "Table des matières" : "Contents")}</h1>

            """);
        AppendNestedList(sb, nav);
        sb.Append("</nav>\n<nav epub:type=\"landmarks\" hidden=\"\"><ol>\n");
        if (hasCover) sb.Append("<li><a epub:type=\"cover\" href=\"text/cover.xhtml\">Cover</a></li>\n");
        sb.Append("<li><a epub:type=\"toc\" href=\"nav.xhtml#toc\">Table of Contents</a></li>\n");
        if (nav.Count > 0) sb.Append($"<li><a epub:type=\"bodymatter\" href=\"{nav[0].Href}\">Start</a></li>\n");
        sb.Append("</ol></nav>\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>Construit des &lt;ol&gt; imbriqués valides, même si les niveaux « sautent » (1 → 3).</summary>
    private static void AppendNestedList(StringBuilder sb, List<NavEntry> nav)
    {
        var depth = 0;      // nombre de <ol> ouverts
        var prevLevel = 0;
        var minLevel = nav.Min(n => n.Level);

        foreach (var e in nav)
        {
            // Niveau relatif, sans « saut » de plus d'un cran par rapport au précédent.
            var level = Math.Clamp(e.Level - minLevel + 1, 1, prevLevel + 1);

            if (depth == 0) { sb.Append("<ol>\n"); depth = 1; }
            else if (level > depth) { sb.Append("\n<ol>\n"); depth++; }
            else
            {
                sb.Append("</li>\n");
                while (depth > level) { sb.Append("</ol>\n</li>\n"); depth--; }
            }
            sb.Append($"<li><a href=\"{e.Href}\">{Esc(e.Title)}</a>");
            prevLevel = level;
        }

        sb.Append("</li>\n");
        while (depth > 1) { sb.Append("</ol>\n</li>\n"); depth--; }
        sb.Append("</ol>\n");
    }

    private static string Ncx(Book book, List<NavEntry> nav, string uid)
    {
        var sb = new StringBuilder();
        sb.Append($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1" xml:lang="{book.Language}">
            <head>
              <meta name="dtb:uid" content="{uid}"/>
              <meta name="dtb:depth" content="{Math.Max(1, nav.Select(n => n.Level).DefaultIfEmpty(1).Max())}"/>
              <meta name="dtb:totalPageCount" content="0"/>
              <meta name="dtb:maxPageNumber" content="0"/>
            </head>
            <docTitle><text>{Esc(book.Title)}</text></docTitle>
            <navMap>

            """);
        // NCX « à plat » : le plus robuste sur les vieux Kindle.
        var i = 0;
        foreach (var e in nav)
        {
            i++;
            var indent = new string(' ', (e.Level - 1) * 2);
            sb.Append($"<navPoint id=\"np{i}\" playOrder=\"{i}\"><navLabel><text>{indent}{Esc(e.Title)}</text></navLabel><content src=\"{e.Href}\"/></navPoint>\n");
        }
        sb.Append("</navMap>\n</ncx>\n");
        return sb.ToString();
    }

    private static string Opf(Book book, List<Part> parts, IReadOnlyList<ImageAsset> images, ImageAsset? cover, string uid)
    {
        var manifest = new StringBuilder();
        var spine = new StringBuilder();

        manifest.Append("<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>\n");
        manifest.Append("<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>\n");
        manifest.Append("<item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>\n");

        if (cover is not null)
        {
            manifest.Append("<item id=\"coverpage\" href=\"text/cover.xhtml\" media-type=\"application/xhtml+xml\"/>\n");
            spine.Append("<itemref idref=\"coverpage\" linear=\"yes\"/>\n");
        }

        for (var i = 0; i < parts.Count; i++)
        {
            manifest.Append($"<item id=\"p{i + 1}\" href=\"text/{parts[i].FileName}\" media-type=\"application/xhtml+xml\"/>\n");
            spine.Append($"<itemref idref=\"p{i + 1}\"/>\n");
        }

        foreach (var img in images)
        {
            var isCover = cover is not null && img.Id == cover.Id;
            var id = isCover ? "cover-image" : img.Id;
            var props = isCover ? " properties=\"cover-image\"" : "";
            manifest.Append($"<item id=\"{id}\" href=\"images/{img.FileName}\" media-type=\"{img.MediaType}\"{props}/>\n");
        }

        var author = string.IsNullOrWhiteSpace(book.Author) ? "" : $"<dc:creator id=\"author\">{Esc(book.Author)}</dc:creator>\n";
        var coverMeta = cover is not null ? "<meta name=\"cover\" content=\"cover-image\"/>\n" : "";
        var modified = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid" xml:lang="{book.Language}">
            <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:identifier id="bookid">{uid}</dc:identifier>
            <dc:title>{Esc(book.Title)}</dc:title>
            <dc:language>{book.Language}</dc:language>
            {author}<meta property="dcterms:modified">{modified}</meta>
            {coverMeta}</metadata>
            <manifest>
            {manifest}</manifest>
            <spine toc="ncx">
            {spine}</spine>
            </package>
            """;
    }

    // ------------------------------------------------------------------ utilitaires

    private static void AddText(ZipArchive zip, string name, string content,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var s = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        s.Write(bytes);
    }

    /// <summary>Échappement XML + suppression des caractères interdits en XML (fréquents dans les PDF).</summary>
    public static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    if (c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF))
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
