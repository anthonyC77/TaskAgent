using System.Text.RegularExpressions;

namespace TaskAgent.Kindle;

/// <summary>
/// Corrections des erreurs typiques de l'OCR, appliquées au texte du livre (nouvelle conversion ou livre déjà fait).
/// Puces de liste : Tesseract lit souvent la puce ronde « • » comme « e », « © », « ® », « ° » ou « « »,
/// et colle les éléments d'une liste en un seul paragraphe : « e Créer la doc e Accueillir les nouveaux ».
/// </summary>
public static class TextFixes
{
    // Une « fausse puce » isolée, suivie d'une majuscule (ou d'un chiffre) : début d'élément de liste.
    private const string Bullet = @"(?:e|©|®|°|«|»|¢|\*|•|·|◦|▪|o)";
    private static readonly Regex StartsWithBullet = new($@"^{Bullet}\s+(?=[\p{{Lu}}\d])");
    private static readonly Regex InnerBullet = new($@"\s+{Bullet}\s+(?=[\p{{Lu}}\d])");
    private static readonly Regex LeadInBullet = new($@"(?<=:)\s+{Bullet}\s+(?=[\p{{Lu}}\d])");

    private static readonly Regex WordRx = new(@"^[\p{L}][\p{L}'’\-]*[.,;:!?…)”»]?$");

    /// <summary>
    /// Charabia d'OCR : ce que Tesseract « lit » dans un schéma (cadres, flèches, étiquettes éparpillées),
    /// ex. « ( \( > ( N (1 D software || software Directorof || VPof ». Repéré aux traits lus comme \ ou |,
    /// mêlés à des fragments orphelins (« ( », « > », « N », « (1 ») ou à peu de vrais mots.
    /// Le code et les modèles à remplir (« [date] », « str | None ») ne sont pas visés.
    /// </summary>
    public static bool IsGarbage(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return false;
        var tokens = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var strokes = t.Count(c => c is '\\' or '|' or '¦');
        if (strokes == 0) return false;
        var words = tokens.Count(w => w.Length >= 2 && WordRx.IsMatch(w) && w.Any(c => "aeiouyAEIOUYàâéèêëîïôûùü".Contains(c)));
        var orphans = tokens.Count(w => w.Length <= 2 && !Regex.IsMatch(w, @"^(?:\p{L}{2}|[aAIàyô])$"));
        var wordRatio = words / (double)tokens.Length;
        var orphanRatio = orphans / (double)tokens.Length;
        return (strokes >= 2 && wordRatio < 0.6) || (tokens.Length >= 2 && orphanRatio >= 0.4);
    }

    /// <summary>
    /// Zone de schéma dans une suite de textes (dans l'ordre de lecture) : le charabia, plus les étiquettes isolées qui
    /// le touchent (« Architect », « architect architect manager », « Software Engineering software Team lead »).
    /// Une vraie phrase ou un long paragraphe arrête la zone. Renvoie, pour chaque texte, s'il fait partie du schéma.
    /// </summary>
    public static bool[] FindDiagramJunk(IReadOnlyList<string> texts)
    {
        var junk = texts.Select(IsGarbage).ToArray();
        if (!junk.Any(j => j)) return junk;
        for (var changed = true; changed;)
        {
            changed = false;
            for (var i = 0; i < texts.Count; i++)
            {
                if (junk[i] || !LabelLike(texts[i])) continue;
                if ((i > 0 && junk[i - 1]) || (i + 1 < texts.Count && junk[i + 1])) { junk[i] = true; changed = true; }
            }
        }
        return junk;
    }

    /// <summary>
    /// Étiquette de schéma : courte, pas une phrase, et 1-2 mots, un mot répété, un début en minuscule,
    /// ou quelques fragments avec un symbole isolé (« = VAS J »).
    /// </summary>
    private static bool LabelLike(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Length > 90 || t.EndsWith('.') || t.StartsWith("• ")) return false;
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var repeated = words.GroupBy(w => w.ToLowerInvariant()).Any(g => g.Key.Length > 2 && g.Count() > 1);
        var symbol = words.Length <= 4 && words.Any(w => !w.Any(char.IsLetterOrDigit));
        return words.Length <= 2 || repeated || char.IsLower(t[0]) || symbol;
    }

    /// <summary>Légende de figure : « Figure 1-1. … », « Fig. 3 : … », « Figure 2.4 … ».</summary>
    public static readonly Regex CaptionRx = new(@"^(?:Figure|Fig\.|Figura|Abbildung)\s*\d+(?:[-.]\d+)?", RegexOptions.IgnoreCase);

    /// <summary>
    /// Texte lu DANS une image : entre une image et sa légende « Figure x-y », les lignes courtes sont les étiquettes
    /// du schéma que l'OCR a recopiées (l'image les contient déjà). On ne retire que si l'une d'elles ressemble à du charabia
    /// ou à une étiquette, et jamais une vraie phrase longue.
    /// </summary>
    /// <param name="between">Textes entre l'image et la légende, dans l'ordre.</param>
    public static bool IsTextFromImage(IReadOnlyList<string> between) =>
        between.Count is > 0 and <= 15 &&
        between.All(t => t.Trim().Length <= 120 && !t.TrimStart().StartsWith("• ")) &&
        between.Any(t => IsGarbage(t) || LabelLike(t));

    /// <summary>
    /// Un paragraphe → un ou plusieurs paragraphes. « e A e B » → « • A », « • B » ;
    /// « Pour progresser : e A e B » → « Pour progresser : », « • A », « • B ». Sinon renvoie le texte tel quel.
    /// </summary>
    public static List<string> SplitBullets(string text)
    {
        var t = text.Trim();
        string? leadIn = null;
        if (!StartsWithBullet.IsMatch(t))
        {
            // Phrase d'introduction terminée par « : » suivie d'une liste, ou au moins 2 fausses puces dans le paragraphe
            // (« Completed e Design e Prototype ») : une seule au milieu d'une phrase n'est pas assez sûr pour couper.
            var m = LeadInBullet.Match(t);
            if (!m.Success && InnerBullet.Matches(t).Count >= 2) m = InnerBullet.Match(t);
            if (!m.Success) return new() { text };
            leadIn = t[..m.Index].Trim();
            t = t[(m.Index + m.Length)..];
        }
        else t = StartsWithBullet.Replace(t, "", 1);

        var items = InnerBullet.Split(t).Select(s => s.Trim()).Where(s => s.Length > 0).Select(s => "• " + s);
        var result = new List<string>();
        if (leadIn is not null) result.Add(leadIn);
        result.AddRange(items);
        return result;
    }

    /// <summary>
    /// Une suite de paragraphes consécutifs → paragraphes corrigés. En plus de <see cref="SplitBullets"/> :
    /// un paragraphe qui commence par une minuscule juste après un élément de liste en est la suite (coupé par un
    /// changement de page) ; on le recolle, puis on coupe aux fausses puces (« … commitments e Driving … »).
    /// </summary>
    public static List<string> FixParagraphs(IReadOnlyList<string> paragraphs)
    {
        var result = new List<string>();
        foreach (var raw in paragraphs)
        {
            var p = raw.Trim();
            if (result.Count > 0 && result[^1].StartsWith("• ") && p.Length > 0 && char.IsLower(p[0]))
            {
                var merged = result[^1] + " " + p;
                result.RemoveAt(result.Count - 1);
                result.AddRange(InnerBullet.Split(merged).Select(s => s.Trim()).Where(s => s.Length > 0)
                    .Select((s, i) => i == 0 ? s : "• " + s));
                continue;
            }
            result.AddRange(SplitBullets(raw));
        }
        return result;
    }

    /// <summary>Applique les corrections à tout le livre ; renvoie le nombre de suites de paragraphes corrigées.</summary>
    public static int Apply(Book book)
    {
        var fixes = 0;
        foreach (var ch in book.Chapters)
        {
            // 1a. Texte recopié d'une image : entre l'image et sa légende « Figure x-y ».
            for (var i = 0; i < ch.Blocks.Count; i++)
            {
                if (ch.Blocks[i] is not ImageBlock) continue;
                var end = i + 1;
                while (end < ch.Blocks.Count && end - i <= 16 && ch.Blocks[end] is HeadingBlock or ParagraphBlock &&
                       !(ch.Blocks[end] is ParagraphBlock cap && CaptionRx.IsMatch(cap.Text)))
                    end++;
                if (end >= ch.Blocks.Count || ch.Blocks[end] is not ParagraphBlock caption || !CaptionRx.IsMatch(caption.Text)) continue;
                var between = ch.Blocks.Skip(i + 1).Take(end - i - 1)
                    .Select(b => b is HeadingBlock h ? h.Text : ((ParagraphBlock)b).Text).ToList();
                if (!IsTextFromImage(between)) continue;
                ch.Blocks.RemoveRange(i + 1, between.Count);
                fixes += between.Count;
            }

            // 1b. Charabia de schéma : retiré (titres compris). Une image coupe la suite : on ne déborde pas au-delà.
            var start = 0;
            for (var i = 0; i <= ch.Blocks.Count; i++)
            {
                if (i < ch.Blocks.Count && ch.Blocks[i] is HeadingBlock or ParagraphBlock) continue;
                var run = ch.Blocks.Skip(start).Take(i - start).Select(b => b is HeadingBlock h ? h.Text : ((ParagraphBlock)b).Text).ToList();
                var junk = FindDiagramJunk(run);
                for (var k = run.Count - 1; k >= 0; k--)
                    if (junk[k]) { ch.Blocks.RemoveAt(start + k); i--; fixes++; }
                start = i + 1;
            }

            // 2. Puces de liste
            for (var i = 0; i < ch.Blocks.Count; i++)
            {
                if (ch.Blocks[i] is not ParagraphBlock) continue;
                var end = i;
                while (end < ch.Blocks.Count && ch.Blocks[end] is ParagraphBlock) end++;
                var run = ch.Blocks.Skip(i).Take(end - i).Cast<ParagraphBlock>().Select(p => p.Text).ToList();
                var fixedRun = FixParagraphs(run);
                if (!fixedRun.SequenceEqual(run))
                {
                    ch.Blocks.RemoveRange(i, run.Count);
                    ch.Blocks.InsertRange(i, fixedRun.Select(x => (Block)new ParagraphBlock(x)));
                    fixes++;
                }
                i += fixedRun.Count - 1;
            }
        }
        return fixes;
    }
}
