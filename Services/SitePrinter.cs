using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using UglyToad.PdfPig.Writer;

namespace TaskAgent.Services;

public class SitePrintJob
{
    public List<Uri> Urls { get; init; } = new();  // pages saisies, imprimées dans cet ordre
    public string OutputFolder { get; init; } = "";
    public bool FollowLinks { get; init; }          // false = seulement les pages saisies ; true = explorer aussi les pages reliées
    public int MaxPages { get; init; } = 100;       // garde-fou de l'exploration
    public bool SamePathOnly { get; init; }       // exploration : ne suivre que les pages sous le chemin de départ (ex. /docs/…)
    public bool UseSitemap { get; init; } = true; // exploration : lire /sitemap.xml pour trouver aussi les pages non reliées
    public bool Merge { get; init; } = true;      // en plus des PDF individuels, un PDF unique « site complet »
    public bool Landscape { get; init; }
    public bool LoginFirst { get; init; }         // ouvrir d'abord un navigateur visible pour se connecter
}

/// <summary>Un lien trouvé sur une page : son texte, son adresse, et s'il reste sur le même site.</summary>
public record PageLink(string Text, Uri Url, bool SameSite);

/// <summary>
/// « Imprimer » tout un site en PDF avec Playwright : un vrai navigateur (Edge, déjà présent sur Windows) ouvre chaque page,
/// attend qu'elle soit chargée et l'enregistre comme le ferait Ctrl+P → « Enregistrer au format PDF ».
/// Soit uniquement les pages saisies, soit en suivant aussi leurs liens internes (exploration).
/// Analogie : un stagiaire qui visite le site page par page et imprime chacune, sans jamais sortir du site.
/// </summary>
public static class SitePrinter
{
    /// <summary>Profil de navigateur dédié : la connexion faite une fois (option « Me connecter d'abord ») est réutilisée.</summary>
    public static readonly string ProfileDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskAgent", "site-printer-profile");

    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".zip", ".rar", ".7z", ".gz", ".exe", ".msi", ".dmg", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".ico",
        ".mp3", ".mp4", ".avi", ".mov", ".webm", ".wav", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".csv", ".xml",
        ".json", ".css", ".js", ".woff", ".woff2", ".ttf", ".rss", ".atom"
    };

    /// <summary>
    /// Extrait les adresses d'un texte collé : une par ligne, ou mélangées à du texte (liste de favoris, e-mail…).
    /// « exemple.com/page » sans http:// est accepté. Les doublons sont retirés, l'ordre est gardé.
    /// </summary>
    public static List<Uri> ParseUrls(string? text)
    {
        var result = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var found = Regex.Matches(line, @"https?://[^\s<>""']+", RegexOptions.IgnoreCase).Select(m => m.Value).ToList();
            if (found.Count == 0 && !line.Contains(' ') && line.Contains('.')) found.Add("https://" + line);
            foreach (var f in found.Select(TrimPunctuation))
                if (Uri.TryCreate(f, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && seen.Add(Key(u)))
                    result.Add(u);
        }
        return result;
    }

    /// <summary>
    /// Tous les liens d'une page, dans l'ordre où ils apparaissent, avec leur texte (« Installation », « Chapitre 3 »…).
    /// Utilise le même profil que l'impression : une page réservée aux membres marche si tu t'y es déjà connecté.
    /// </summary>
    public static async Task<List<PageLink>> ExtractLinksAsync(Uri url, CancellationToken ct)
    {
        using var pw = await Playwright.CreateAsync();
        await using var ctx = await LaunchAsync(pw, headless: true, _ => { });
        var page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();
        var resp = await page.GotoAsync(url.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 45_000 });
        if (resp is { Ok: false }) throw new InvalidOperationException(Explain(resp.Status));
        await SettleAsync(page);
        ct.ThrowIfCancellationRequested();
        var here = Uri.TryCreate(page.Url, UriKind.Absolute, out var final) ? final : url;

        // Toutes les « frames » (iframes comprises), et dans chacune les composants web (shadow DOM) :
        // ce que F12 montre mais qu'un simple querySelectorAll de la page principale ne voit pas.
        var raw = new List<string[]>();
        foreach (var frame in page.Frames)
        {
            try { raw.AddRange(await frame.EvaluateAsync<string[][]>(CollectLinksJs)); }
            catch (PlaywrightException) { /* frame fermée entre-temps */ }
        }
        return ToLinks(raw, here);
    }

    private const string CollectLinksJs = @"() => {
        const out = [];
        const visit = root => {
            root.querySelectorAll('a[href], area[href], [data-href], [data-url]').forEach(a => {
                const h = typeof a.href === 'string' && a.href ? a.href : (a.getAttribute('href') || a.dataset.href || a.dataset.url);
                try {
                    if (h) out.push([new URL(h, document.baseURI).href,
                        (a.innerText || a.textContent || a.getAttribute('aria-label') || a.title || '').replace(/\s+/g, ' ').trim()]);
                } catch { }
            });
            root.querySelectorAll('*').forEach(e => { if (e.shadowRoot) visit(e.shadowRoot); });
        };
        visit(document);
        return out;
    }";

    /// <summary>
    /// Liens d'un code HTML collé ou d'une page enregistrée (Ctrl+S) : la page a été chargée par TON navigateur,
    /// donc ça marche même quand le site refuse les navigateurs automatisés.
    /// L'adresse de base (pour les liens relatifs « /docs/x ») vient de &lt;base&gt;, du commentaire « saved from url= »
    /// de Chrome/Edge, du lien canonique, ou à défaut de <paramref name="fallbackBase"/>.
    /// </summary>
    public static (Uri? baseUri, List<PageLink> links) LinksFromHtml(string html, Uri? fallbackBase)
    {
        Uri? Find(string pattern) =>
            Regex.Match(html, pattern, RegexOptions.IgnoreCase) is { Success: true } m &&
            Uri.TryCreate(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value), UriKind.Absolute, out var u) ? u : null;
        var baseUri = Find(@"<base\s[^>]*href\s*=\s*[""']([^""']+)")
                      ?? Find(@"saved from url=\(\d+\)(\S+?)\s*-->")
                      ?? Find(@"<link\s[^>]*rel\s*=\s*[""']canonical[""'][^>]*href\s*=\s*[""']([^""']+)")
                      ?? Find(@"<meta\s[^>]*property\s*=\s*[""']og:url[""'][^>]*content\s*=\s*[""']([^""']+)")
                      ?? fallbackBase;

        var raw = new List<string[]>();
        foreach (Match m in Regex.Matches(html,
                     @"<a\s[^>]*?(?:href|data-href|data-url)\s*=\s*(?:""([^""]*)""|'([^']*)')[^>]*>(.*?)</a>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var href = System.Net.WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            Uri? u = Uri.TryCreate(href, UriKind.Absolute, out var abs) ? abs
                : baseUri is not null && Uri.TryCreate(baseUri, href, out var rel) ? rel : null;
            if (u is null) continue;
            var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups[3].Value, "<[^>]+>", " "));
            raw.Add(new[] { u.ToString(), Regex.Replace(text, @"\s+", " ").Trim() });
        }
        return (baseUri, ToLinks(raw, baseUri));
    }

    /// <summary>Dédoublonne (ancres #… comprises), retire la page elle-même et les liens non web (mailto:, javascript:…).</summary>
    private static List<PageLink> ToLinks(IEnumerable<string[]> raw, Uri? here)
    {
        var links = new List<PageLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (here is not null) seen.Add(Key(here));
        foreach (var r in raw)
        {
            if (r.Length < 2 || !Uri.TryCreate(r[0], UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) continue;
            if (!seen.Add(Key(u))) continue;
            links.Add(new PageLink(r[1].Length > 0 ? r[1] : u.AbsolutePath, u, here is null || SameSite(u, here)));
        }
        return links;
    }

    /// <summary>Retire la ponctuation collée à la fin (« voir https://x.com/a. ») sans casser « /Foo_(bar) ».</summary>
    private static string TrimPunctuation(string url)
    {
        url = url.TrimEnd('.', ',', ';', ':', '!', '?');
        while (url.EndsWith(')') && url.Count(c => c == ')') > url.Count(c => c == '(')) url = url[..^1];
        return url;
    }

    /// <param name="waitForLogin">Appelé quand le navigateur visible est ouvert ; renvoie false pour abandonner.</param>
    /// <returns>Les fichiers PDF produits (le PDF fusionné en dernier).</returns>
    public static async Task<List<string>> RunAsync(SitePrintJob job, Action<string> log, Func<Task<bool>>? waitForLogin, CancellationToken ct)
    {
        if (job.Urls.Count == 0) throw new ArgumentException("Aucune adresse valide : mets une adresse par ligne (https://…).");
        var start = job.Urls[0];
        Directory.CreateDirectory(job.OutputFolder);

        using var pw = await Playwright.CreateAsync();

        if (job.LoginFirst)
        {
            log("🔐 Ouverture d'un navigateur visible : connecte-toi au site, puis valide dans TaskAgent.");
            await using (var visible = await LaunchAsync(pw, headless: false, log))
            {
                var p = visible.Pages.FirstOrDefault() ?? await visible.NewPageAsync();
                await p.GotoAsync(start.ToString());
                if (waitForLogin is not null && !await waitForLogin()) throw new OperationCanceledException();
            }
            log("✔ Session enregistrée, on passe à l'impression.");
        }

        // L'impression PDF n'existe qu'en mode invisible (headless) chez Chromium/Edge.
        await using var ctx = await LaunchAsync(pw, headless: true, log);
        var page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();

        var queue = new Queue<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Exploration : on n'accepte que les liens qui restent dans le périmètre d'une des adresses saisies.
        void Enqueue(Uri u)
        {
            if (job.Urls.Any(s => Accept(u, s, job.SamePathOnly)) && seen.Add(Key(u))) queue.Enqueue(u);
        }
        // Les pages saisies passent toujours, même hors périmètre, et dans l'ordre donné.
        foreach (var u in job.Urls)
            if (seen.Add(Key(u))) queue.Enqueue(u);
        if (job.FollowLinks && job.UseSitemap)
            foreach (var site in job.Urls.DistinctBy(u => StripWww(u.Host).ToLowerInvariant()))
            {
                var fromSitemap = await ReadSitemapAsync(ctx.APIRequest, site, ct);
                foreach (var u in fromSitemap) Enqueue(u);
                if (fromSitemap.Count > 0) log($"🗺 sitemap.xml de {site.Host} : {fromSitemap.Count} adresse(s) trouvée(s).");
            }
        var limit = job.FollowLinks ? job.MaxPages : int.MaxValue;
        log(job.FollowLinks
            ? $"🔎 Exploration à partir de {job.Urls.Count} adresse(s), {job.MaxPages} pages max."
            : $"📋 {job.Urls.Count} page(s) à imprimer.");

        var produced = new List<string>();
        var index = new StringBuilder();
        var failures = 0;
        var denied = 0; // refus (401/403) consécutifs

        while (queue.Count > 0 && produced.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            var url = queue.Dequeue();
            try
            {
                var resp = await page.GotoAsync(url.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 45_000 });
                if (resp is null || !resp.Ok)
                {
                    log($"  ↷ {url}  ({(resp is null ? "pas de réponse" : Explain(resp.Status))})");
                    // Un site qui refuse 3 fois de suite bloque les robots (Akamai, Cloudflare…) : insister risque
                    // de faire bannir ton adresse IP, même dans ton navigateur normal. On s'arrête.
                    denied = resp?.Status is 401 or 403 ? denied + 1 : 0;
                    if (denied >= 3)
                    {
                        log("⛔ Le site refuse les navigateurs automatisés : arrêt pour ne pas faire bloquer ton adresse. " +
                            "Utilise « 🖐 Impression assistée » (les pages s'ouvrent dans ton navigateur, tu fais Ctrl+P).");
                        break;
                    }
                    continue;
                }
                denied = 0;
                if (resp.Headers.TryGetValue("content-type", out var type) && !type.Contains("html", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var final)) continue;
                // En exploration, une redirection hors des sites saisis (pub, connexion externe…) est ignorée.
                if (job.FollowLinks && !job.Urls.Any(s => SameSite(final, s))) continue;
                seen.Add(Key(final));

                await SettleAsync(page);
                if (job.FollowLinks)
                    foreach (var href in await page.EvaluateAsync<string[]>("() => Array.from(document.querySelectorAll('a[href]'), a => a.href)"))
                        if (Uri.TryCreate(href, UriKind.Absolute, out var link)) Enqueue(link);

                var title = (await page.TitleAsync()).Trim();
                var file = Path.Combine(job.OutputFolder, FileName(produced.Count + 1, title, final));
                await page.PdfAsync(new PagePdfOptions
                {
                    Path = file, Format = "A4", Landscape = job.Landscape, PrintBackground = true,
                    Margin = new Margin { Top = "12mm", Bottom = "12mm", Left = "10mm", Right = "10mm" }
                });
                produced.Add(file);
                index.AppendLine($"{Path.GetFileName(file)}\t{final}");
                log($"🖨 {produced.Count,3}  {(title.Length > 0 ? title : final.AbsolutePath)}");
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                failures++;
                log($"  ✗ {url} : {ex.Message.Split('\n')[0]}");
            }
        }

        if (job.FollowLinks && queue.Count > 0) log($"⏹ Limite de {job.MaxPages} pages atteinte ({queue.Count} adresse(s) non visitée(s)) : augmente-la pour aller plus loin.");
        await File.WriteAllTextAsync(Path.Combine(job.OutputFolder, "_sommaire.txt"), index.ToString(), ct);

        if (job.Merge && produced.Count > 1)
        {
            ct.ThrowIfCancellationRequested();
            var oneSite = job.Urls.All(u => SameSite(u, start));
            var merged = Path.Combine(job.OutputFolder,
                $"_{(oneSite ? Sanitize(StripWww(start.Host)) : "plusieurs sites")} - {(job.FollowLinks ? "site complet" : "pages choisies")}.pdf");
            try
            {
                await File.WriteAllBytesAsync(merged, PdfMerger.Merge(produced.ToArray()), ct);
                produced.Add(merged);
                log($"📕 PDF unique : {Path.GetFileName(merged)}");
            }
            catch (Exception ex) { log("⚠ Fusion impossible (les PDF individuels sont là) : " + ex.Message); }
        }

        log($"✔ Terminé : {produced.Count} fichier(s)" + (failures > 0 ? $", {failures} page(s) en échec" : "") + ".");
        return produced;
    }

    // ---------------- Navigateur ----------------

    /// <summary>Edge (toujours installé sous Windows), sinon Chrome, sinon le Chromium de Playwright (téléchargé une fois, ~150 Mo).</summary>
    private static async Task<IBrowserContext> LaunchAsync(IPlaywright pw, bool headless, Action<string> log)
    {
        Directory.CreateDirectory(ProfileDir);
        foreach (var channel in new[] { "msedge", "chrome", null })
        {
            try
            {
                return await pw.Chromium.LaunchPersistentContextAsync(ProfileDir, new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = headless, Channel = channel, ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
                });
            }
            catch (PlaywrightException) when (channel is not null) { /* navigateur absent : on essaie le suivant */ }
            catch (PlaywrightException) when (channel is null)
            {
                log("⬇ Ni Edge ni Chrome trouvés : téléchargement du navigateur Chromium de Playwright (une seule fois)…");
                if (await Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium" })) != 0)
                    throw new InvalidOperationException("Installation de Chromium impossible.");
                return await pw.Chromium.LaunchPersistentContextAsync(ProfileDir, new BrowserTypeLaunchPersistentContextOptions { Headless = headless });
            }
        }
        throw new InvalidOperationException("Aucun navigateur disponible.");
    }

    /// <summary>Message clair pour les refus d'accès, au lieu d'un simple code.</summary>
    public static string Explain(int status) => status switch
    {
        401 or 403 => $"HTTP {status} : le site refuse l'accès. Soit la page est réservée (coche « Me connecter d'abord »), " +
                      "soit le site bloque les navigateurs automatisés : dans ce cas, imprime depuis ton propre navigateur (Ctrl+P).",
        404 => "HTTP 404 : page introuvable.",
        429 => "HTTP 429 : trop de requêtes, le site demande de ralentir. Réessaie dans quelques minutes.",
        _ => $"HTTP {status}"
    };

    /// <summary>Attend la fin des chargements et fait défiler la page pour déclencher les images « paresseuses » (lazy loading).</summary>
    private static async Task SettleAsync(IPage page)
    {
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 8_000 }); } catch { }
        try
        {
            await page.EvaluateAsync(@"async () => {
                for (let y = 0; y < document.body.scrollHeight && y < 30000; y += 800) {
                    window.scrollTo(0, y); await new Promise(r => setTimeout(r, 120));
                }
                window.scrollTo(0, 0);
            }");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 4_000 });
        }
        catch { }
    }

    // ---------------- Liens ----------------

    private static bool SameSite(Uri a, Uri b) =>
        string.Equals(StripWww(a.Host), StripWww(b.Host), StringComparison.OrdinalIgnoreCase);

    private static string StripWww(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    private static bool Accept(Uri u, Uri start, bool samePathOnly)
    {
        if (u.Scheme is not ("http" or "https") || !SameSite(u, start)) return false;
        if (SkippedExtensions.Contains(Path.GetExtension(u.AbsolutePath))) return false;
        if (!samePathOnly) return true;
        var root = start.AbsolutePath.EndsWith('/') ? start.AbsolutePath : start.AbsolutePath[..(start.AbsolutePath.LastIndexOf('/') + 1)];
        return u.AbsolutePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Même page = même hôte (sans www), même chemin (sans / final), même requête ; l'ancre #… est ignorée.</summary>
    private static string Key(Uri u) => $"{StripWww(u.Host)}{u.AbsolutePath.TrimEnd('/')}{u.Query}";

    private static async Task<List<Uri>> ReadSitemapAsync(IAPIRequestContext http, Uri start, CancellationToken ct)
    {
        var found = new List<Uri>();
        var pending = new Queue<string>(new[] { new Uri(start, "/sitemap.xml").ToString() });
        var visited = 0;
        while (pending.Count > 0 && visited++ < 20 && found.Count < 5000)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await http.GetAsync(pending.Dequeue(), new APIRequestContextOptions { Timeout = 15_000 });
                if (!resp.Ok) continue;
                var xml = await resp.TextAsync();
                var isIndex = xml.Contains("<sitemapindex", StringComparison.OrdinalIgnoreCase);
                foreach (Match m in Regex.Matches(xml, @"<loc>\s*(.*?)\s*</loc>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    var loc = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                    if (isIndex) pending.Enqueue(loc);
                    else if (Uri.TryCreate(loc, UriKind.Absolute, out var u)) found.Add(u);
                }
            }
            catch { /* pas de sitemap : on se contente des liens */ }
        }
        return found;
    }

    // ---------------- Noms de fichiers ----------------

    /// <summary>« 007 - Titre de la page.pdf » : le numéro garde l'ordre de visite, le titre rend le dossier lisible.</summary>
    private static string FileName(int n, string title, Uri url)
    {
        var label = Sanitize(title.Length > 0 ? title : url.AbsolutePath.Trim('/').Replace('/', ' '));
        if (label.Length == 0) label = "accueil";
        if (label.Length > 80) label = label[..80].Trim();
        return $"{n:000} - {label}.pdf";
    }

    private static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => bad.Contains(c) ? ' ' : c).ToArray());
        return Regex.Replace(clean, @"\s+", " ").Trim(' ', '.');
    }
}
