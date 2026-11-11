using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TaskAgent.Models;

namespace TaskAgent.Services;

/// <summary>Un endroit où une tâche peut être suivie : dépôt GitHub, dépôt git local…</summary>
public record LinkCandidate(string Tool, string Name, string Link, string? Description);

/// <summary>L'emplacement retenu par l'IA, avec sa justification.</summary>
public record LinkSuggestion(string Tool, string Project, string Link, string Why);

/// <summary>
/// Retrouve le lien (dépôt GitHub, dossier local…) d'une tâche.
/// L'appli dresse elle-même la liste des candidats — le jeton GitHub reste sur le PC —
/// puis l'IA choisit le plus pertinent. Comme l'IA n'a besoin d'aucun outil, ça marche
/// aussi bien avec Claude qu'avec un modèle local (Qwen…).
/// </summary>
public static class LinkFinder
{
    public const string GitHubTokenName = "github-token";
    public const string GitHubTokenHelpUrl = "https://github.com/settings/personal-access-tokens/new";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TaskAgent");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    /// <summary>Tes dépôts GitHub (perso, collaborateur, organisations), les plus récemment modifiés d'abord.</summary>
    public static async Task<List<LinkCandidate>> GitHubReposAsync(string token, CancellationToken ct, int max = 300)
    {
        var repos = new List<LinkCandidate>();
        for (var page = 1; repos.Count < max; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"user/repos?per_page=100&page={page}&sort=pushed&affiliation=owner,collaborator,organization_member");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await Http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
                throw new UnauthorizedAccessException("Jeton GitHub refusé (expiré ou révoqué).");
            res.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var items = doc.RootElement.EnumerateArray().ToList();
            foreach (var r in items)
                repos.Add(new LinkCandidate("GitHub",
                    r.GetProperty("full_name").GetString() ?? "",
                    r.GetProperty("html_url").GetString() ?? "",
                    r.TryGetProperty("description", out var d) ? d.GetString() : null));
            if (items.Count < 100) break;
        }
        return repos;
    }

    /// <summary>Dossiers contenant un .git sous les racines indiquées (profondeur limitée pour rester rapide).</summary>
    public static List<LinkCandidate> LocalRepos(IEnumerable<string> roots, int maxDepth = 3, int max = 200)
    {
        var found = new List<LinkCandidate>();
        void Walk(string dir, int depth)
        {
            if (found.Count >= max) return;
            try
            {
                if (Directory.Exists(Path.Combine(dir, ".git")))
                {
                    found.Add(new LinkCandidate("Dossier", Path.GetFileName(dir), dir, null));
                    return; // pas de dépôts imbriqués
                }
                if (depth >= maxDepth) return;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj") continue;
                    Walk(sub, depth + 1);
                }
            }
            catch { /* dossier inaccessible */ }
        }
        foreach (var root in roots.Where(Directory.Exists)) Walk(root, 0);
        return found;
    }

    public static string SystemPrompt =>
        "Tu relies une tâche à l'endroit où elle est suivie. Tu reçois la tâche et une liste numérotée de candidats " +
        "(dépôts GitHub, dossiers locaux). Choisis le candidat qui correspond le mieux (nom, description, sens). " +
        "Réponds UNIQUEMENT par un objet JSON sur une ligne, sans texte autour : " +
        "{\"index\": <numéro du candidat ou 0 si aucun ne correspond vraiment>, \"why\": \"<raison en une phrase, en français>\"}";

    public static string Request(TaskItem t, IReadOnlyList<LinkCandidate> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Tâche : {t.Title}");
        if (!string.IsNullOrWhiteSpace(t.Description)) sb.AppendLine($"Description : {t.Description}");
        if (!string.IsNullOrWhiteSpace(t.ToolProject)) sb.AppendLine($"Projet indiqué : {t.ToolProject}");
        sb.AppendLine().AppendLine("Candidats :");
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            sb.AppendLine($"{i + 1}. [{c.Tool}] {c.Name}{(string.IsNullOrWhiteSpace(c.Description) ? "" : " — " + c.Description)}");
        }
        return sb.ToString();
    }

    /// <summary>Lit la réponse JSON de l'IA (tolère un bloc ```json``` ou du texte autour).</summary>
    public static LinkSuggestion? Parse(string reply, IReadOnlyList<LinkCandidate> candidates)
    {
        var json = Regex.Match(reply, @"\{.*\}", RegexOptions.Singleline).Value;
        if (json.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var index = doc.RootElement.GetProperty("index").GetInt32();
            if (index < 1 || index > candidates.Count) return null;
            var why = doc.RootElement.TryGetProperty("why", out var w) ? w.GetString() ?? "" : "";
            var c = candidates[index - 1];
            return new LinkSuggestion(c.Tool, c.Name, c.Link, why);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
