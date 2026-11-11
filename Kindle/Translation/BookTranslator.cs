using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskAgent.Kindle.Translation;

public sealed class TranslationOptions
{
    public string TargetLanguage { get; set; } = "fr";
    public int Concurrency { get; set; } = 4;
    /// <summary>Taille d'un lot envoyé au modèle (en caractères). Assez grand pour donner du contexte.</summary>
    public int BatchChars { get; set; } = 6000;
    public bool AutoGlossary { get; set; } = true;
    public string? GlossaryFile { get; set; }
    /// <summary>Où écrire le glossaire généré (pour pouvoir le relire / corriger puis le réutiliser).</summary>
    public string? GlossaryOutput { get; set; }
    public string CacheFile { get; set; } = "translation-cache.json";
    /// <summary>Consignes libres ajoutées au prompt (« tutoiement », « style soutenu »…).</summary>
    public string? ExtraInstructions { get; set; }
    /// <summary>Le texte vient d'un EPUB (propre, paragraphes entiers) et non d'une extraction PDF.</summary>
    public bool FromEpub { get; set; }
}

public sealed record GlossaryTerm(string Source, string Target, string? Note = null);

/// <summary>
/// Traduction « qualité édition » d'un livre entier, en 2 passes :
///  1. le modèle lit un échantillon du livre et établit un profil (domaine, ton) + un glossaire
///     qui décide, terme par terme, ce qui se traduit et ce qui reste en anglais ;
///  2. le texte est traduit par lots de plusieurs paragraphes (pour garder le fil),
///     avec ce glossaire imposé partout → cohérence du début à la fin du livre.
/// Un cache évite de repayer ce qui a déjà été traduit si on relance.
/// </summary>
public sealed class BookTranslator
{
    private readonly ILlmClient _llm;
    private readonly TranslationOptions _opt;
    private readonly Action<string> _log;
    private readonly Dictionary<string, string> _cache;
    private readonly object _cacheLock = new();
    private readonly object _fileLock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record Segment(int Id, string Kind, string Text);
    private sealed record DocumentProfile(string Domain, string Tone, List<GlossaryTerm> Terms);

    public BookTranslator(ILlmClient llm, TranslationOptions opt, Action<string> log)
    {
        _llm = llm;
        _opt = opt;
        _log = log;
        _cache = LoadCache(opt.CacheFile);
    }

    public async Task<Book> TranslateAsync(Book source, CancellationToken ct = default)
    {
        var segments = CollectSegments(source);
        _log($"Traduction {source.Language} → {_opt.TargetLanguage} avec {_llm.Name} : {segments.Count} segments");

        var profile = await BuildProfileAsync(source, ct);
        var system = BuildSystemPrompt(source.Language, profile);

        var todo = segments.Where(s => !_cache.ContainsKey(CacheKey(s.Text))).ToList();
        if (todo.Count < segments.Count)
            _log($"   {segments.Count - todo.Count} segment(s) déjà en cache");

        var batches = MakeBatches(todo);
        var done = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, _opt.Concurrency));
        var tasks = batches.Select(async batch =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await TranslateBatchAsync(system, source.Title, batch, segments, ct);
                var n = Interlocked.Increment(ref done);
                var step = Math.Max(1, batches.Count / 10);
                if (n % step == 0 || n == batches.Count)
                {
                    _log($"   {n * 100 / batches.Count} % ({n}/{batches.Count} lots)");
                    SaveCache(); // sauvegarde régulière : un Ctrl+C ne fait rien perdre
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        SaveCache();

        var missing = segments.Count(s => !_cache.ContainsKey(CacheKey(s.Text)));
        if (missing > 0) _log($"   ! {missing} segment(s) laissé(s) en langue d'origine (échec de traduction)");

        return source.CloneWithText(Lookup, _opt.TargetLanguage);
    }

    private string Lookup(string text) =>
        string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetter)
            ? text
            : _cache.GetValueOrDefault(CacheKey(text), text);

    // ------------------------------------------------------------------ passe 1 : profil + glossaire

    private async Task<DocumentProfile> BuildProfileAsync(Book book, CancellationToken ct)
    {
        var userTerms = LoadUserGlossary(_opt.GlossaryFile);
        DocumentProfile profile = new("", "", new List<GlossaryTerm>());

        if (_opt.AutoGlossary)
        {
            _log("   analyse du livre et création du glossaire…");
            try
            {
                var answer = await _llm.CompleteAsync(
                    $$"""
                    You are a senior terminologist preparing the translation of a book into {{LanguageName(_opt.TargetLanguage)}}.
                    Read the excerpt and return ONLY a JSON object, no prose:
                    {"domain": "<subject area, one line>",
                     "tone": "<register/voice of the book and how to render it in {{LanguageName(_opt.TargetLanguage)}}, one or two lines>",
                     "terms": [{"source": "<term as written>", "target": "<translation, or the source term unchanged if it must stay in English>", "note": "<short reason, optional>"}]}
                    Pick 15 to 60 recurring or ambiguous terms: domain jargon, named concepts, recurring expressions, names.
                    Keep a term in English when professionals writing in {{LanguageName(_opt.TargetLanguage)}} normally keep it in English
                    (typical in software, blockchain, finance, gaming, science); otherwise give the established equivalent.
                    """,
                    "<excerpt>\n" + RepresentativeSample(book) + "\n</excerpt>",
                    4000, ct);

                var json = ParseJsonObject(answer);
                var terms = json["terms"]?.AsArray()
                    .Select(t => new GlossaryTerm(
                        t?["source"]?.GetValue<string>() ?? "",
                        t?["target"]?.GetValue<string>() ?? "",
                        t?["note"]?.GetValue<string>()))
                    .Where(t => t.Source.Length > 0 && t.Target.Length > 0)
                    .ToList() ?? new();
                profile = new DocumentProfile(
                    json["domain"]?.GetValue<string>() ?? "",
                    json["tone"]?.GetValue<string>() ?? "",
                    terms);
                _log($"   domaine détecté : {profile.Domain}");
            }
            catch (Exception ex)
            {
                _log($"   ! glossaire automatique indisponible ({ex.Message}), on continue sans");
            }
        }

        // Le glossaire de l'utilisateur a toujours le dernier mot.
        var merged = profile.Terms
            .Where(t => !userTerms.Any(u => u.Source.Equals(t.Source, StringComparison.OrdinalIgnoreCase)))
            .Concat(userTerms)
            .ToList();
        profile = profile with { Terms = merged };

        if (_opt.GlossaryOutput is not null && (merged.Count > 0 || profile.Domain.Length > 0))
        {
            File.WriteAllText(_opt.GlossaryOutput, JsonSerializer.Serialize(
                new { domain = profile.Domain, tone = profile.Tone, terms = merged }, JsonOpts));
            _log($"   glossaire enregistré : {_opt.GlossaryOutput} ({merged.Count} termes)");
        }
        return profile;
    }

    private string BuildSystemPrompt(string sourceLang, DocumentProfile profile)
    {
        var target = LanguageName(_opt.TargetLanguage);
        var origin = _opt.FromEpub
            ? "You translate the text of an e-book (EPUB)."
            : "You translate content extracted from a PDF so it can be read as an e-book.";
        var sourceRule = _opt.FromEpub
            ? "- The text comes from a clean e-book: translate everything as written; never summarize, add commentary or omit content."
            : """
              - The text comes from PDF extraction and may contain artifacts (broken hyphenation, stray page numbers, OCR typos):
                silently fix obvious artifacts, but never summarize, add commentary or omit content.
              """;
        var paragraphRule = _opt.FromEpub
            ? "- Segments marked \"paragraph\" are complete paragraphs: never merge them. A segment starting with \"• \" is a list item: keep the bullet."
            : "- Segments marked \"paragraph\" may be a fragment that continues in the next segment: keep them as fragments.";

        var sb = new StringBuilder();
        sb.Append($"""
            You are an expert book translator from {LanguageName(sourceLang)} into {target}.
            {origin} Aim for publication quality:
            natural, idiomatic {target} that a native professional editor would sign off on — never word-for-word.

            Rules:
            - Preserve meaning, nuance, tone and register. Restructure sentences freely when {target} requires it.
            - Keep in English the terms that {target}-speaking practitioners normally use in English (technical jargon,
              product and brand names), plus code, identifiers, commands, URLs, file names, formulas and citations of titles.
              If such a term may be unclear to the reader, you may add a short {target} gloss in parentheses the first time only.
            - Follow the glossary strictly and stay consistent across segments.
            {sourceRule}
            - Segments marked "heading" are titles: translate them as concise headings following {target} capitalization rules.
            {paragraphRule}
            """);
        if (_opt.TargetLanguage == "fr")
            sb.Append("""

            - Typographie française : guillemets « » avec espaces insécables à l'intérieur, espace insécable avant : ; ! ?,
              apostrophe typographique ’, majuscules accentuées (É, À…), nombres au format français (1 000,5).
            """);
        if (!string.IsNullOrWhiteSpace(_opt.ExtraInstructions))
            sb.Append("\n- Additional instructions from the reader: ").Append(_opt.ExtraInstructions);

        if (profile.Domain.Length > 0 || profile.Tone.Length > 0)
            sb.Append($"\n\nDocument profile:\n- Domain: {profile.Domain}\n- Tone: {profile.Tone}");

        if (profile.Terms.Count > 0)
        {
            sb.Append("\n\nGlossary (source → required rendering; identical means keep it untranslated):\n");
            foreach (var t in profile.Terms)
                sb.Append($"- {t.Source} → {t.Target}{(string.IsNullOrWhiteSpace(t.Note) ? "" : $"  ({t.Note})")}\n");
        }

        sb.Append("""


            Input: a JSON array of segments {"id", "type", "text"}, optionally preceded by <previous_context> (already handled,
            do NOT translate it, it is only there for continuity).
            Output: ONLY a JSON object {"translations":[{"id":<same id>,"text":"<translation>"}]} with exactly one entry per input id.
            """);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ passe 2 : traduction par lots

    private async Task TranslateBatchAsync(string system, string title, List<Segment> batch,
        List<Segment> all, CancellationToken ct)
    {
        try
        {
            var context = PreviousContext(all, batch[0].Id);
            var payload = JsonSerializer.Serialize(batch.Select(s => new { id = s.Id, type = s.Kind, text = s.Text }), JsonOpts);
            var user = $"<document_title>{title}</document_title>\n" +
                       (context.Length > 0 ? $"<previous_context>\n{context}\n</previous_context>\n" : "") +
                       $"<segments>\n{payload}\n</segments>";

            var maxTokens = Math.Clamp(batch.Sum(s => s.Text.Length) / 2 + 1500, 2000, 16000);
            var answer = await _llm.CompleteAsync(system, user, maxTokens, ct);
            var json = ParseJsonObject(answer);

            var byId = json["translations"]!.AsArray()
                .Where(n => n?["id"] is not null && n["text"] is not null)
                .GroupBy(n => int.TryParse(n!["id"]!.ToString(), out var id) ? id : -1)
                .ToDictionary(g => g.Key, g => g.First()!["text"]!.ToString().Trim());

            var missing = batch.Where(s => !byId.TryGetValue(s.Id, out var t) || t.Length == 0).ToList();
            lock (_cacheLock)
                foreach (var s in batch.Except(missing))
                    _cache[CacheKey(s.Text)] = byId[s.Id];

            if (missing.Count > 0)
            {
                if (missing.Count == batch.Count && batch.Count == 1) throw new InvalidDataException("segment non traduit");
                await RetrySplitAsync(system, title, missing, all, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (batch.Count == 1)
            {
                _log($"   ! segment {batch[0].Id} non traduit : {ex.Message}");
                return;
            }
            await RetrySplitAsync(system, title, batch, all, ct);
        }
    }

    /// <summary>Réponse invalide ou tronquée : on recoupe le lot en deux et on recommence.</summary>
    private async Task RetrySplitAsync(string system, string title, List<Segment> batch, List<Segment> all, CancellationToken ct)
    {
        var half = Math.Max(1, batch.Count / 2);
        await TranslateBatchAsync(system, title, batch.Take(half).ToList(), all, ct);
        if (batch.Count > half)
            await TranslateBatchAsync(system, title, batch.Skip(half).ToList(), all, ct);
    }

    private List<List<Segment>> MakeBatches(List<Segment> segments)
    {
        var batches = new List<List<Segment>>();
        var current = new List<Segment>();
        var size = 0;
        foreach (var s in segments)
        {
            if (current.Count > 0 && (size + s.Text.Length > _opt.BatchChars || current.Count >= 60))
            {
                batches.Add(current);
                current = new List<Segment>();
                size = 0;
            }
            current.Add(s);
            size += s.Text.Length;
        }
        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    private static List<Segment> CollectSegments(Book book)
    {
        var seen = new HashSet<string>();
        var list = new List<Segment>();
        void Add(string kind, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetter) || !seen.Add(text)) return;
            list.Add(new Segment(list.Count + 1, kind, text));
        }

        Add("heading", book.Title);
        foreach (var ch in book.Chapters)
        {
            Add("heading", ch.Title);
            foreach (var b in ch.Blocks)
            {
                if (b is HeadingBlock h) Add("heading", h.Text);
                else if (b is ParagraphBlock p) Add("paragraph", p.Text);
            }
        }
        return list;
    }

    private static string PreviousContext(List<Segment> all, int firstId)
    {
        var idx = all.FindIndex(s => s.Id == firstId);
        if (idx <= 0) return "";
        var ctx = all.Skip(Math.Max(0, idx - 2)).Take(Math.Min(2, idx)).Select(s => s.Text);
        var text = string.Join("\n", ctx);
        return text.Length > 1500 ? text[^1500..] : text;
    }

    /// <summary>Extrait de ~12 000 caractères pris au début, au milieu et à la fin du livre.</summary>
    private static string RepresentativeSample(Book book)
    {
        var paragraphs = book.Chapters.SelectMany(c => c.Blocks)
            .Select(b => b switch { HeadingBlock h => "## " + h.Text, ParagraphBlock p => p.Text, _ => null })
            .Where(t => t is not null).Cast<string>().ToList();
        var total = paragraphs.Sum(p => p.Length);
        if (total <= 12_000) return string.Join("\n", paragraphs);

        var sb = new StringBuilder();
        foreach (var start in new[] { 0.0, 0.33, 0.66 })
        {
            var i = (int)(paragraphs.Count * start);
            var chunk = 0;
            sb.Append("\n[…]\n");
            while (i < paragraphs.Count && chunk < 4000)
            {
                sb.Append(paragraphs[i]).Append('\n');
                chunk += paragraphs[i].Length;
                i++;
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ utilitaires

    /// <summary>Tolère les ```json … ``` et le texte parasite autour du JSON.</summary>
    internal static JsonNode ParseJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("pas de JSON dans la réponse");
        return JsonNode.Parse(text[start..(end + 1)]) ?? throw new InvalidDataException("JSON vide");
    }

    /// <summary>
    /// Glossaire utilisateur : soit le .json produit par l'outil, soit un .txt :
    ///   smart contract            (garder tel quel)
    ///   ledger => registre        (traduction imposée)
    ///   # commentaire
    /// </summary>
    public static List<GlossaryTerm> LoadUserGlossary(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new();
        if (!File.Exists(path)) throw new FileNotFoundException("Glossaire introuvable", path);

        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            var arr = (json is JsonArray a ? a : json["terms"]?.AsArray()) ?? new JsonArray();
            return arr.Select(t => new GlossaryTerm(
                    t?["source"]?.GetValue<string>() ?? "",
                    t?["target"]?.GetValue<string>() ?? t?["source"]?.GetValue<string>() ?? "",
                    t?["note"]?.GetValue<string>()))
                .Where(t => t.Source.Length > 0).ToList();
        }

        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l =>
            {
                var parts = Regex.Split(l, @"\s*(?:=>|→|=)\s*", RegexOptions.None, TimeSpan.FromSeconds(1));
                return parts.Length >= 2
                    ? new GlossaryTerm(parts[0], parts[1])
                    : new GlossaryTerm(l, l, "keep in English");
            }).ToList();
    }

    private string CacheKey(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_opt.TargetLanguage + "\u0001" + text)))[..32];

    private static Dictionary<string, string> LoadCache(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    private void SaveCache()
    {
        string json;
        lock (_cacheLock) json = JsonSerializer.Serialize(_cache, JsonOpts);
        lock (_fileLock) File.WriteAllText(_opt.CacheFile, json);
    }

    public static string LanguageName(string code) => code switch
    {
        "fr" => "French (France)",
        "en" => "English",
        "es" => "Spanish",
        "de" => "German",
        "it" => "Italian",
        _ => code,
    };
}
