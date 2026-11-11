using System.Text;
using PdfToKindle;
using PdfToKindle.Translation;

Console.OutputEncoding = Encoding.UTF8;

CliOptions o;
try
{
    o = CliOptions.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine("Erreur : " + ex.Message);
    Console.Error.WriteLine();
    CliOptions.PrintHelp();
    return 2;
}
if (o.ShowHelp || o.Input is null) { CliOptions.PrintHelp(); return o.ShowHelp ? 0 : 2; }

void Log(string m) => Console.WriteLine(m);

// ---------------------------------------------------------------- 1. quels PDF ?
var inputPath = Path.GetFullPath(o.Input);
List<string> pdfs;
bool isFolder = Directory.Exists(inputPath);
if (isFolder)
{
    pdfs = Directory.EnumerateFiles(inputPath, "*.*",
            o.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
        .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => Path.GetRelativePath(inputPath, f), Comparer<string>.Create(BookBuilder.NaturalCompare))
        .ToList();
    if (pdfs.Count == 0) { Console.Error.WriteLine($"Aucun PDF dans {inputPath}"); return 1; }
}
else if (File.Exists(inputPath))
{
    pdfs = new() { inputPath };
}
else
{
    Console.Error.WriteLine($"Introuvable : {inputPath}");
    return 1;
}

// ---------------------------------------------------------------- 2. traducteur (optionnel)
ILlmClient? llm = null;
if (o.Translate != TranslateMode.Off)
{
    try { llm = CreateLlm(o); }
    catch (Exception ex) { Console.Error.WriteLine("Traduction impossible : " + ex.Message); return 1; }
}

var extractOpt = new ExtractOptions
{
    Pages = o.Pages is null ? null : PageSelection.Parse(o.Pages),
    IncludeImages = !o.NoImages,
    KeepHeadersFooters = o.KeepHeadersFooters,
};

// ---------------------------------------------------------------- 3. un livre, ou un livre par PDF
var jobs = new List<(List<string> Files, string Title, string Output)>();
if (isFolder && o.Separate)
{
    var outDir = o.Output ?? inputPath;
    foreach (var pdf in pdfs)
        jobs.Add((new() { pdf }, BookBuilder.PrettifyFileName(pdf),
            Path.Combine(outDir, Path.GetFileNameWithoutExtension(pdf) + ".epub")));
}
else
{
    var baseName = isFolder ? new DirectoryInfo(inputPath).Name : Path.GetFileNameWithoutExtension(inputPath);
    var output = o.Output ?? Path.Combine(isFolder ? inputPath : Path.GetDirectoryName(inputPath)!, baseName + ".epub");
    if (Directory.Exists(output)) output = Path.Combine(output, baseName + ".epub");
    jobs.Add((pdfs, isFolder ? baseName : BookBuilder.PrettifyFileName(inputPath), output));
}

var exitCode = 0;
foreach (var job in jobs)
{
    try
    {
        var book = BookBuilder.Build(job.Files, job.Title, extractOpt, Log);
        if (book.Chapters.Count == 0) { Log("Rien à convertir."); exitCode = 1; continue; }

        if (o.Title is not null) book.Title = o.Title;
        if (o.Author is not null) book.Author = o.Author;
        if (o.Language is not null) book.Language = o.Language;
        if (o.Cover is not null)
        {
            var bytes = File.ReadAllBytes(o.Cover);
            var png = o.Cover.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            book.CoverImageId = book.Images.Add(bytes, png ? "image/png" : "image/jpeg", png ? ".png" : ".jpg", 0, 0).Id;
        }

        Log($"Langue détectée : {book.Language}");
        var mustTranslate = llm is not null &&
                            (o.Translate == TranslateMode.Force || book.Language != o.TargetLanguage);
        if (!o.TranslatedOnly || !mustTranslate)
        {
            EpubWriter.Write(book, job.Output);
            Log($"✓ {job.Output} ({new FileInfo(job.Output).Length / 1024} Ko)");
        }

        // ------------------------------------------------------ 4. version traduite
        if (llm is not null && !mustTranslate)
            Log($"Texte déjà en « {o.TargetLanguage} » : pas de traduction (utilisez --translate force pour forcer).");

        if (mustTranslate)
        {
            var translatedPath = Path.Combine(Path.GetDirectoryName(job.Output)!,
                Path.GetFileNameWithoutExtension(job.Output) + "." + o.TargetLanguage + ".epub");
            var stem = Path.Combine(Path.GetDirectoryName(job.Output)!, Path.GetFileNameWithoutExtension(job.Output));
            var translator = new BookTranslator(llm!, new TranslationOptions
            {
                TargetLanguage = o.TargetLanguage,
                Concurrency = o.Concurrency,
                AutoGlossary = !o.NoAutoGlossary,
                GlossaryFile = o.Glossary,
                GlossaryOutput = stem + ".glossaire.json",
                CacheFile = stem + ".cache-traduction.json",
                ExtraInstructions = o.Instructions,
            }, Log);

            var translated = await translator.TranslateAsync(book);
            EpubWriter.Write(translated, translatedPath);
            Log($"✓ {translatedPath} ({new FileInfo(translatedPath).Length / 1024} Ko)");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"✗ {job.Output} : {ex.Message}");
        exitCode = 1;
    }
}

Log("");
Log("Astuce : envoyez le .epub sur https://www.amazon.fr/sendtokindle (ou par e-mail à votre adresse @kindle.com).");
return exitCode;

static ILlmClient CreateLlm(CliOptions o)
{
    switch (o.Provider)
    {
        case "claude":
        {
            var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("définissez la variable d'environnement ANTHROPIC_API_KEY " +
                                                    "(ou utilisez --provider openai --endpoint … pour un modèle local).");
            return new ClaudeClient(key, o.Model ?? "claude-sonnet-5-5", o.Endpoint);
        }
        case "openai":
        {
            var endpoint = o.Endpoint ?? "https://api.openai.com/v1";
            var model = o.Model ?? throw new InvalidOperationException("--model est obligatoire avec --provider openai");
            return new OpenAiCompatibleClient(endpoint, model, Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        }
        default:
            throw new InvalidOperationException($"fournisseur inconnu : {o.Provider} (claude | openai)");
    }
}

enum TranslateMode { Off, Auto, Force }

sealed class CliOptions
{
    public string? Input, Output, Pages, Title, Author, Language, Cover;
    public bool Recursive, Separate, NoImages, KeepHeadersFooters, ShowHelp;
    public TranslateMode Translate = TranslateMode.Off;
    public string TargetLanguage = "fr";
    public bool TranslatedOnly, NoAutoGlossary;
    public string Provider = "claude";
    public string? Model, Endpoint, Glossary, Instructions;
    public int Concurrency = 4;

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"valeur manquante après {a}");
            switch (a)
            {
                case "-h" or "--help" or "/?": o.ShowHelp = true; break;
                case "-o" or "--output": o.Output = Next(); break;
                case "-p" or "--pages": o.Pages = Next(); break;
                case "-r" or "--recursive": o.Recursive = true; break;
                case "--separate": o.Separate = true; break;
                case "-t" or "--title": o.Title = Next(); break;
                case "-a" or "--author": o.Author = Next(); break;
                case "-l" or "--lang": o.Language = Next(); break;
                case "--cover": o.Cover = Next(); break;
                case "--no-images": o.NoImages = true; break;
                case "--keep-headers": o.KeepHeadersFooters = true; break;
                case "--translate":
                    o.Translate = TranslateMode.Auto;
                    if (i + 1 < args.Length && args[i + 1] is "auto" or "force")
                        o.Translate = Next() == "force" ? TranslateMode.Force : TranslateMode.Auto;
                    break;
                case "--to": o.TargetLanguage = Next(); break;
                case "--translated-only": o.TranslatedOnly = true; break;
                case "--provider": o.Provider = Next().ToLowerInvariant(); break;
                case "--model": o.Model = Next(); break;
                case "--endpoint": o.Endpoint = Next(); break;
                case "--glossary": o.Glossary = Next(); break;
                case "--no-auto-glossary": o.NoAutoGlossary = true; break;
                case "--instructions": o.Instructions = Next(); break;
                case "--concurrency": o.Concurrency = int.Parse(Next()); break;
                default:
                    if (a.StartsWith('-')) throw new ArgumentException($"option inconnue : {a}");
                    if (o.Input is not null) throw new ArgumentException("un seul fichier ou dossier en entrée");
                    o.Input = a;
                    break;
            }
        }
        return o;
    }

    public static void PrintHelp() => Console.WriteLine(
        """
        pdf2kindle — convertit des PDF en EPUB lisible sur Kindle (texte reformaté + images)

        Usage :
          pdf2kindle <fichier.pdf | dossier> [options]

        Entrée / sortie :
          -o, --output <chemin>    Fichier .epub de sortie (ou dossier avec --separate)
          -p, --pages <plage>      Pages à convertir, ex : 1-20,25,30-
          -r, --recursive          Dossier : inclure les sous-dossiers
              --separate           Dossier : un EPUB par PDF au lieu d'un seul livre

        Livre :
          -t, --title <titre>      -a, --author <auteur>      -l, --lang <fr|en|…>
              --cover <image>      Couverture (jpg/png). Sinon : 1re page si c'est une image
              --no-images          Texte seul
              --keep-headers       Garder en-têtes / pieds de page / numéros de page

        Traduction IA (produit un 2e fichier « nom.fr.epub ») :
              --translate [auto|force]  auto = seulement si le texte n'est pas déjà en français
              --to <langue>             Langue cible (défaut : fr)
              --translated-only         Ne pas écrire l'EPUB d'origine
              --provider claude|openai  claude (ANTHROPIC_API_KEY) ou API compatible OpenAI
              --model <id>              ex : claude-sonnet-5-5 (défaut), claude-opus-5-5, llama3.1…
              --endpoint <url>          ex : http://localhost:11434/v1 pour Ollama
              --glossary <fichier>      Termes imposés (.txt « terme => traduction » ou .json généré)
              --no-auto-glossary        Ne pas faire analyser le livre avant traduction
              --instructions "<texte>"  Consignes de style libres
              --concurrency <n>         Requêtes en parallèle (défaut : 4)

        Exemples :
          pdf2kindle livre.pdf
          pdf2kindle ./cours --title "Cours Rust" --author "Moi"
          pdf2kindle book.pdf --translate --glossary termes.txt
          pdf2kindle ./papers --separate --translate --provider openai --endpoint http://localhost:11434/v1 --model qwen2.5:14b
        """);
}
