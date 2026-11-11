using System.Text;
using System.Text.RegularExpressions;

namespace TaskAgent.Kindle;

/// <summary>
/// Écrit le livre intermédiaire en Markdown : « nom.md » + un dossier « nom_images/ » à côté (liens relatifs),
/// lisible tel quel dans Obsidian, VS Code, GitHub, ou à donner à une IA.
/// Hiérarchie : # titre du livre, ## un chapitre par PDF (mode dossier), puis les titres du PDF en dessous.
/// </summary>
public static class MarkdownWriter
{
    public static void Write(Book book, string outputPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(dir);
        var imagesName = Path.GetFileNameWithoutExtension(outputPath) + "_images";
        var imagesDir = Path.Combine(dir, imagesName);
        var written = new HashSet<string>();

        string? ImageLink(string id)
        {
            if (book.Images.Get(id) is not { } asset) return null;
            if (written.Add(asset.Id))
            {
                Directory.CreateDirectory(imagesDir);
                File.WriteAllBytes(Path.Combine(imagesDir, asset.FileName), asset.Data);
            }
            // <…> : la destination peut contenir des espaces (CommonMark).
            return $"![](<{imagesName}/{asset.FileName}>)";
        }

        var md = new StringBuilder();
        md.AppendLine("---");
        md.AppendLine($"title: {Yaml(book.Title)}");
        if (!string.IsNullOrWhiteSpace(book.Author)) md.AppendLine($"author: {Yaml(book.Author)}");
        md.AppendLine($"lang: {book.Language}");
        var sources = book.Chapters.Select(c => c.SourceFile).Where(s => s is not null).Distinct().ToList();
        if (sources.Count == 1) md.AppendLine($"source: {Yaml(Path.GetFileName(sources[0]!))}");
        md.AppendLine("---").AppendLine();
        md.AppendLine("# " + Inline(book.Title)).AppendLine();
        if (book.CoverImageId is { } cover && ImageLink(cover) is { } coverLink) md.AppendLine(coverLink).AppendLine();

        var single = book.Chapters.Count == 1;
        foreach (var ch in book.Chapters)
        {
            // En mode « plusieurs PDF », chaque PDF devient un chapitre ## et ses titres descendent d'un niveau.
            if (!single && !string.IsNullOrWhiteSpace(ch.Title)) md.AppendLine("## " + Inline(ch.Title)).AppendLine();
            var shift = single ? 1 : 2;

            foreach (var block in ch.Blocks)
            {
                switch (block)
                {
                    case HeadingBlock h:
                        md.Append('#', Math.Clamp(h.Level + shift, 2, 6)).Append(' ').AppendLine(Inline(h.Text)).AppendLine();
                        break;
                    case ParagraphBlock p:
                        md.AppendLine(Paragraph(p.Text)).AppendLine();
                        break;
                    case ImageBlock img when ImageLink(img.ImageId) is { } link:
                        md.AppendLine(link).AppendLine();
                        break;
                }
            }
        }

        File.WriteAllText(outputPath, md.ToString().TrimEnd() + Environment.NewLine, new UTF8Encoding(false));
    }

    /// <summary>Texte brut → Markdown : on neutralise les caractères qui deviendraient de la mise en forme.</summary>
    private static string Inline(string s) =>
        Regex.Replace(s.Replace("\r", " ").Replace("\n", " "), @"([\\`*\[\]<])", @"\$1").Trim();

    /// <summary>
    /// Un paragraphe sur une ligne. « 1. … » et « - … » restent des listes Markdown, les puces du PDF (•, ▪, ●…) en deviennent ;
    /// un début qui ferait un titre ou une citation (« # », « > ») est échappé pour rester du texte.
    /// </summary>
    private static string Paragraph(string s)
    {
        var text = Regex.Replace(Inline(s), @"^[•▪●◦‣·–]\s*", "- ");
        return Regex.Replace(text, @"^([#>=])", @"\$1");
    }

    private static string Yaml(string? s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
