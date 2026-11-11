using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TaskAgent.Kindle;

/// <summary>
/// Mode correction : applique <see cref="TextFixes"/> à un livre déjà produit (.epub ou .md) sans relire les PDF ni refaire l'OCR :
/// puces lues « e » par l'OCR, et charabia lu dans les schémas (retiré, table des matières comprise).
/// Le fichier d'origine n'est jamais modifié : le résultat est écrit à côté (« livre v2.epub »).
/// </summary>
public static class BookFixer
{
    private static readonly Regex ParagraphRx = new(@"<p>([^<]*)</p>", RegexOptions.Compiled);
    // Suite de paragraphes consécutifs : un élément de liste coupé par un changement de page se recolle à son voisin.
    private static readonly Regex ParagraphRunRx = new(@"(?:<p>[^<]*</p>\s*)+", RegexOptions.Compiled);
    // Titres et paragraphes dans l'ordre de la page (pour repérer les zones de schéma).
    private static readonly Regex TextElementRx = new(@"<(p|h[1-6])((?:\s[^>]*)?)>([^<]*)</\1>", RegexOptions.Compiled);
    private static readonly Regex MdEscapeRx = new(@"\\([\\`*\[\]<=#>])", RegexOptions.Compiled);
    // Image, puis jusqu'à 15 titres/paragraphes, puis la légende « Figure x-y » (groupe 5).
    private static readonly Regex FigureRunRx = new(
        @"(<img\b[^>]*>(?:\s*</div>)?)((?:\s*<(p|h[1-6])((?:\s[^>]*)?)>[^<]*</\3>){1,15}?)(\s*<p>(?:Figure|Fig\.|Figura|Abbildung)\s*\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <returns>Le fichier corrigé, ou null si rien n'était à corriger.</returns>
    public static string? Fix(string path, Action<string> log)
    {
        int bullets = 0, junk = 0;
        var output = KindleConverter.UniquePath(path);
        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) FixMarkdown(path, output, ref bullets, ref junk);
        else FixEpub(path, output, ref bullets, ref junk);

        if (bullets + junk == 0)
        {
            if (File.Exists(output)) File.Delete(output);
            log("Rien à corriger : ni puce mal lue, ni charabia de schéma dans ce livre (il est peut-être déjà corrigé). " +
                "Si tu vois encore des erreurs, vérifie que ta liseuse affiche bien ce fichier-ci et pas l'ancien.");
            return null;
        }
        log($"✔ {bullets} liste(s) à puces reconstituée(s), {junk} ligne(s) de charabia de schéma retirée(s) → {Path.GetFileName(output)}");
        if (junk > 0) log("  (Le schéma lui-même ne peut pas être retrouvé sans les PDF : reconvertis-les pour l'avoir en image.)");
        return output;
    }

    // ---------------- Markdown ----------------

    private static void FixMarkdown(string path, string output, ref int bullets, ref int junk)
    {
        var all = File.ReadAllLines(path);
        var headerEnd = all.Length > 0 && all[0] == "---" ? Array.IndexOf(all, "---", 1) : -1;

        string Plain(int i) => MdEscapeRx.Replace(all[i].TrimStart('#', ' '), "$1");
        var drop = new bool[all.Length];

        // 0. Texte recopié d'une image : lignes entre « ![](…) » et la légende « Figure x-y ».
        for (var i = headerEnd + 1; i < all.Length; i++)
        {
            if (!all[i].StartsWith("![")) continue;
            var between = new List<int>();
            var k = i + 1;
            for (; k < all.Length && between.Count <= 15; k++)
            {
                if (all[k].Trim().Length == 0) continue;
                if (all[k].StartsWith("![") || TextFixes.CaptionRx.IsMatch(Plain(k))) break;
                between.Add(k);
            }
            if (k < all.Length && TextFixes.CaptionRx.IsMatch(Plain(k)) && TextFixes.IsTextFromImage(between.Select(Plain).ToList()))
                foreach (var b in between) drop[b] = true;
        }

        // 1. Zones de schéma : suites de lignes de texte (titres compris) entre deux images ou séparateurs.
        var segment = new List<int>();
        void CheckSegment()
        {
            var flags = TextFixes.FindDiagramJunk(segment.Select(Plain).ToList());
            for (var k = 0; k < flags.Length; k++) if (flags[k]) drop[segment[k]] = true;
            segment.Clear();
        }
        for (var i = headerEnd + 1; i < all.Length; i++)
        {
            if (all[i].StartsWith("![") || all[i] == "---") CheckSegment();
            else if (all[i].Trim().Length > 0 && !drop[i]) segment.Add(i);
        }
        CheckSegment();
        junk = drop.Count(d => d);

        // 2. Puces de liste, sur ce qui reste.
        var lines = new List<string>();
        var run = new List<string>(); // paragraphes de texte consécutifs (« - x » = élément de liste)
        var fixedBullets = 0;
        void Flush()
        {
            if (run.Count == 0) return;
            var asText = run.Select(l => l.StartsWith("- ") ? "• " + l[2..] : l).ToList();
            var fixedRun = TextFixes.FixParagraphs(asText);
            if (!fixedRun.SequenceEqual(asText)) fixedBullets++;
            foreach (var p in fixedRun) { lines.Add(p.StartsWith("• ") ? "- " + p[2..] : p); lines.Add(""); }
            run.Clear();
        }
        for (var i = 0; i < all.Length; i++)
        {
            var line = all[i];
            if (i <= headerEnd) { lines.Add(line); continue; } // en-tête YAML
            if (drop[i]) continue;
            if (line.StartsWith('#') || line.StartsWith("![") || line == "---") { Flush(); lines.Add(line); continue; }
            if (line.Trim().Length == 0) { if (run.Count == 0 && (lines.Count == 0 || lines[^1].Length > 0)) lines.Add(line); continue; }
            run.Add(line);
        }
        Flush();
        bullets = fixedBullets;
        if (bullets + junk > 0) File.WriteAllLines(output, lines, new UTF8Encoding(false));
    }

    // ---------------- EPUB ----------------

    private static void FixEpub(string path, string output, ref int bullets, ref int junk)
    {
        // Tout en mémoire d'abord : il faut connaître les titres retirés avant de réécrire la table des matières.
        var entries = new List<(string Name, byte[] Data)>();
        using (var source = ZipFile.OpenRead(path))
            foreach (var e in source.Entries)
            {
                using var ms = new MemoryStream();
                using (var s = e.Open()) s.CopyTo(ms);
                entries.Add((e.FullName, ms.ToArray()));
            }

        static bool IsPage(string n) => n.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
        static string Text(byte[] d) => new UTF8Encoding(false).GetString(d);
        static byte[] Bytes(string s) => new UTF8Encoding(false).GetBytes(s);

        var removedTitles = new HashSet<string>();
        int b = 0, j = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var (name, data) = entries[i];
            var html = Text(data);
            if (!IsPage(name) || html.Contains("epub:type=\"toc\"")) continue;

            // 0. Texte recopié d'une image : entre <img> et la légende « Figure x-y ».
            html = FigureRunRx.Replace(html, m =>
            {
                var inner = TextElementRx.Matches(m.Groups[2].Value).ToList();
                var texts = inner.Select(x => WebUtility.HtmlDecode(x.Groups[3].Value)).ToList();
                if (!TextFixes.IsTextFromImage(texts)) return m.Value;
                foreach (var x in inner.Where(x => x.Groups[1].Value.StartsWith('h')))
                    removedTitles.Add(WebUtility.HtmlDecode(x.Groups[3].Value).Trim());
                j += inner.Count;
                // Les titres retirés gardent leur ancre (id) pour la table des matières.
                var anchors = string.Concat(inner.Select(x => Regex.Match(x.Groups[2].Value, @"\bid=""([^""]+)""") is { Success: true } a
                    ? $"<span id=\"{a.Groups[1].Value}\"></span>" : ""));
                return m.Groups[1].Value + anchors + m.Groups[5].Value;
            });

            // 1. Charabia de schéma, par suites d'éléments qui se touchent (une image ou un autre élément coupe la suite).
            var matches = TextElementRx.Matches(html).ToList();
            var drop = new bool[matches.Count];
            var seg = new List<int>();
            void Check()
            {
                var flags = TextFixes.FindDiagramJunk(seg.Select(k => WebUtility.HtmlDecode(matches[k].Groups[3].Value)).ToList());
                for (var k = 0; k < flags.Length; k++) if (flags[k]) drop[seg[k]] = true;
                seg.Clear();
            }
            for (var k = 0; k < matches.Count; k++)
            {
                if (seg.Count > 0)
                {
                    var prev = matches[seg[^1]];
                    var gap = html[(prev.Index + prev.Length)..matches[k].Index];
                    if (gap.Trim().Length > 0) Check();
                }
                seg.Add(k);
            }
            Check();
            if (drop.Any(d => d))
            {
                var sb = new StringBuilder(html);
                for (var k = matches.Count - 1; k >= 0; k--)
                {
                    if (!drop[k]) continue;
                    var m = matches[k];
                    j++;
                    var isHeading = m.Groups[1].Value.StartsWith('h');
                    if (isHeading) removedTitles.Add(WebUtility.HtmlDecode(m.Groups[3].Value).Trim());
                    // Un titre garde son ancre (id) : les liens de la table des matières restent valides.
                    var id = Regex.Match(m.Groups[2].Value, @"\bid=""([^""]+)""");
                    sb.Remove(m.Index, m.Length).Insert(m.Index, id.Success ? $"<span id=\"{id.Groups[1].Value}\"></span>" : "");
                }
                html = sb.ToString();
            }

            // 2. Puces de liste
            html = ParagraphRunRx.Replace(html, m =>
            {
                var texts = ParagraphRx.Matches(m.Value).Select(p => WebUtility.HtmlDecode(p.Groups[1].Value)).ToList();
                var fixedRun = TextFixes.FixParagraphs(texts);
                if (fixedRun.SequenceEqual(texts)) return m.Value;
                b++;
                return string.Join("\n", fixedRun.Select(p => "<p>" + EpubWriter.Esc(p) + "</p>")) + "\n";
            });
            entries[i] = (name, Bytes(html));
        }
        bullets = b;
        junk = j;
        if (b + j == 0) return;

        // Nouvelle identité (identifiant + « (v2) » dans le titre) : sinon les liseuses (Kindle, Calibre…) croient
        // que c'est le même livre et continuent d'afficher l'ancien.
        var opf = entries.FirstOrDefault(e => e.Name.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
        var oldUid = opf.Data is null ? null
            : Regex.Match(Text(opf.Data), @"<dc:identifier[^>]*>([^<]+)</dc:identifier>") is { Success: true } um ? um.Groups[1].Value.Trim() : null;
        var newUid = "urn:uuid:" + Guid.NewGuid();
        var label = Regex.Match(Path.GetFileNameWithoutExtension(output), @" (v\d+)$") is { Success: true } vm ? vm.Groups[1].Value : "corrigé";
        bool Removed(string t) => removedTitles.Contains(WebUtility.HtmlDecode(t).Trim());

        using var target = ZipFile.Open(output, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            var bytes = data;
            if (name.EndsWith(".opf", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ncx", StringComparison.OrdinalIgnoreCase))
            {
                var xml = Text(data);
                if (oldUid is not null) xml = xml.Replace(oldUid, newUid);
                xml = Regex.Replace(xml, @"(<dc:title[^>]*>)([^<]*?)(?:\s\((?:v\d+|corrigé)\))?(</dc:title>)", $"$1$2 ({label})$3");
                xml = Regex.Replace(xml, @"(<docTitle>\s*<text>)([^<]*?)(?:\s\((?:v\d+|corrigé)\))?(</text>)", $"$1$2 ({label})$3");
                // Table des matières (ancien format) : on retire les entrées des titres de charabia.
                xml = Regex.Replace(xml, @"<navPoint[^>]*>\s*<navLabel>\s*<text>([^<]*)</text>\s*</navLabel>\s*<content[^>]*/>\s*</navPoint>\s*",
                    m => Removed(m.Groups[1].Value) ? "" : m.Value);
                bytes = Bytes(xml);
            }
            else if (IsPage(name) && Text(data).Contains("epub:type=\"toc\"") && removedTitles.Count > 0)
            {
                // Table des matières (EPUB 3) : entrées sans sous-niveau seulement.
                bytes = Bytes(Regex.Replace(Text(data), @"<li>\s*<a href=""[^""]*"">([^<]*)</a>\s*</li>\s*",
                    m => Removed(m.Groups[1].Value) ? "" : m.Value));
            }
            // Le fichier « mimetype » doit rester le premier et non compressé (norme EPUB).
            var entry = target.CreateEntry(name, name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using var dest = entry.Open();
            dest.Write(bytes);
        }
    }
}
