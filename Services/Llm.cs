using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TaskAgent.Models;

namespace TaskAgent.Services;

/// <summary>
/// Le "cerveau" est une prise électrique standard : n'importe quel modèle qui respecte
/// cette interface peut y être branché (Claude, API Anthropic, Qwen local…).
/// </summary>
public interface ILlmProvider
{
    string Name { get; }
    /// <summary>Vrai si l'agent peut chercher sur le web (WebSearch/WebFetch).</summary>
    bool CanResearch { get; }
    Task<LlmReply> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, CancellationToken ct);

    /// <summary>Appel « brut » sans conversation ni outils (traduction, extraction JSON…).</summary>
    Task<LlmReply> CompleteAsync(string systemPrompt, string user, CancellationToken ct) =>
        ChatAsync(systemPrompt, new[] { new ChatMessage { Role = "user", Content = user } }, ct);
}

/// <summary>Réponse du modèle + infos affichées sous le message (modèle réellement utilisé, effort, durée).</summary>
public record LlmReply(string Text, string? Model, string? Effort, TimeSpan Duration)
{
    public string Meta => string.Join(" · ", new[]
    {
        Model, Effort is null ? null : "effort " + Effort, $"{Duration.TotalSeconds:0} s"
    }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

public static class LlmFactory
{
    public static ILlmProvider Create(AppSettings s) => s.Provider switch
    {
        "AnthropicApi" => new AnthropicApiProvider(s),
        "OpenAICompatible" => new OpenAiCompatibleProvider(s),
        _ => new ClaudeCodeProvider(s)
    };
}

// ---------------------------------------------------------------------------
// 1) Claude Code CLI : utilise TON abonnement (Pro/Max) une fois `claude` connecté.
//    On lance `claude -p --output-format json`, avec notre prompt système à la place de celui
//    de Claude Code (sinon il charge ~40k tokens de contexte de dev à chaque question),
//    on envoie la conversation sur stdin et on lit le champ "result" du JSON renvoyé.
// ---------------------------------------------------------------------------
public sealed class ClaudeCodeProvider : ILlmProvider
{
    private readonly AppSettings _s;
    public ClaudeCodeProvider(AppSettings s) => _s = s;
    public string Name => $"Claude ({_s.ClaudeCliModel ?? "modèle par défaut"} · effort {_s.ClaudeCliEffort ?? "par défaut"})";
    public bool CanResearch => _s.AllowWebResearch;

    public Task<LlmReply> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, CancellationToken ct) =>
        RunWithPromptFileAsync(systemPrompt, BuildPrompt(history), _s.AllowWebResearch, ct);

    public Task<LlmReply> CompleteAsync(string systemPrompt, string user, CancellationToken ct) =>
        RunWithPromptFileAsync(systemPrompt, user, allowTools: false, ct);

    private async Task<LlmReply> RunWithPromptFileAsync(string systemPrompt, string stdin, bool allowTools, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var promptFile = Path.Combine(AppPaths.DataDir, $"system-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(promptFile, systemPrompt, new UTF8Encoding(false), ct);
        try
        {
            return await RunAsync(promptFile, stdin, allowTools, sw, ct);
        }
        finally
        {
            try { File.Delete(promptFile); } catch { }
        }
    }

    private async Task<LlmReply> RunAsync(string promptFile, string stdin, bool allowTools, Stopwatch sw, CancellationToken ct)
    {
        var (file, prefixArgs) = ResolveCli(_s.ClaudeCliPath);
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = AppPaths.DataDir
        };
        foreach (var a in prefixArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        if (!string.IsNullOrWhiteSpace(_s.ClaudeCliModel))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(_s.ClaudeCliModel);
        }
        if (!string.IsNullOrWhiteSpace(_s.ClaudeCliEffort))
        {
            psi.ArgumentList.Add("--effort");
            psi.ArgumentList.Add(_s.ClaudeCliEffort);
        }
        psi.ArgumentList.Add("--system-prompt-file");
        psi.ArgumentList.Add(promptFile);
        // Seuls outils autorisés : la recherche web (ou aucun). Pas de hooks/plugins/MCP de l'utilisateur.
        psi.ArgumentList.Add("--tools");
        if (allowTools)
        {
            psi.ArgumentList.Add("WebSearch,WebFetch");
            psi.ArgumentList.Add("--allowedTools");
            psi.ArgumentList.Add("WebSearch,WebFetch");
        }
        else psi.ArgumentList.Add(""); // "" = aucun outil
        psi.ArgumentList.Add("--strict-mcp-config");
        psi.ArgumentList.Add("--setting-sources");
        psi.ArgumentList.Add("project");
        psi.ArgumentList.Add("--disable-slash-commands");
        psi.ArgumentList.Add("--no-session-persistence");

        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException("Impossible de lancer Claude Code.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "Claude Code introuvable. Installe-le puis lance `claude` une fois dans un terminal pour te connecter " +
                "avec ton abonnement, ou indique son chemin complet dans ClaudeCliPath (⚙).");
        }

        using (p)
        using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
        {
            await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();

            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (string.IsNullOrWhiteSpace(stdout))
                throw new InvalidOperationException($"Claude Code a échoué (code {p.ExitCode}) : {stderr.Trim()}");

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var result = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
            if (root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True)
                throw new InvalidOperationException("Claude Code : " + (result.Length > 0 ? result : stdout));
            return new LlmReply(result, MainModel(root), _s.ClaudeCliEffort, sw.Elapsed);
        }
    }

    /// <summary>
    /// "modelUsage" liste aussi le petit modèle utilitaire (Haiku, qui résume les pages web et peut coûter plus
    /// que le modèle principal) : on l'écarte sauf s'il est seul, puis on garde celui qui a le plus servi.
    /// </summary>
    private static string? MainModel(JsonElement root)
    {
        if (!root.TryGetProperty("modelUsage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        return usage.EnumerateObject()
            .Select(m => m.Value.TryGetProperty("canonicalModel", out var n) ? n.GetString() ?? m.Name : m.Name)
            .OrderBy(name => name.Contains("haiku", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    /// <summary>La CLI n'a pas d'historique côté app : on lui envoie la conversation sous forme de transcript.</summary>
    private static string BuildPrompt(IReadOnlyList<ChatMessage> history)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<conversation>");
        foreach (var m in history)
            sb.Append(m.IsUser ? "[Utilisateur] " : "[Agent] ").AppendLine(m.Content).AppendLine();
        sb.AppendLine("</conversation>").AppendLine();
        sb.AppendLine("Réponds uniquement au dernier message de l'utilisateur, en tant qu'Agent.");
        return sb.ToString();
    }

    /// <summary>
    /// Trouve l'exécutable `claude`. Sous Windows une install npm donne un claude.cmd qu'il faut lancer via cmd.exe ;
    /// sous macOS les applis graphiques n'héritent pas du PATH du terminal, d'où les dossiers en plus.
    /// </summary>
    private static (string file, List<string> prefixArgs) ResolveCli(string cli)
    {
        var found = File.Exists(cli) ? cli : FindExecutable(cli);
        if (found is null) return (cli, new());
        if (OperatingSystem.IsWindows() &&
            (found.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || found.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
            return ("cmd.exe", new() { "/c", found });
        return (found, new());
    }

    private static string? FindExecutable(string name)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(new[]
            {
                Path.Combine(home, ".local", "bin"),
                Path.Combine(home, ".claude", "local"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                "/opt/homebrew/bin", "/usr/local/bin"
            });
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : new[] { "" };

        foreach (var dir in dirs)
            foreach (var ext in exts)
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), name + ext);
                    if (File.Exists(p)) return p;
                }
                catch { /* entrée de PATH invalide */ }
            }
        return null;
    }
}

// ---------------------------------------------------------------------------
// 2) API Anthropic directe (clé API — facturée séparément de l'abonnement).
// ---------------------------------------------------------------------------
public sealed class AnthropicApiProvider : ILlmProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly AppSettings _s;
    public AnthropicApiProvider(AppSettings s) => _s = s;
    public string Name => $"API Anthropic ({_s.AnthropicModel})";
    public bool CanResearch => false;

    public async Task<LlmReply> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var key = Environment.GetEnvironmentVariable(_s.AnthropicApiKeyEnvVar)
                  ?? throw new InvalidOperationException($"Variable d'environnement {_s.AnthropicApiKeyEnvVar} absente.");

        var body = new
        {
            model = _s.AnthropicModel,
            max_tokens = 1500,
            system = systemPrompt,
            messages = history.Select(m => new { role = m.Role, content = m.Content })
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var res = await Http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"API Anthropic {(int)res.StatusCode} : {json}");

        using var doc = JsonDocument.Parse(json);
        var text = string.Concat(doc.RootElement.GetProperty("content").EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "text")
            .Select(b => b.GetProperty("text").GetString()));
        var model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : _s.AnthropicModel;
        return new LlmReply(text, model, null, sw.Elapsed);
    }
}

// ---------------------------------------------------------------------------
// 3) Tout serveur compatible OpenAI : Ollama, LM Studio, llama.cpp… (ex. Qwen en local).
// ---------------------------------------------------------------------------
public sealed class OpenAiCompatibleProvider : ILlmProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly AppSettings _s;
    public OpenAiCompatibleProvider(AppSettings s) => _s = s;
    public string Name => $"Local ({_s.OpenAIModel})";
    public bool CanResearch => false;

    public async Task<LlmReply> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        messages.AddRange(history.Select(m => new { role = m.Role, content = m.Content }));

        using var req = new HttpRequestMessage(HttpMethod.Post, _s.OpenAIBaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = JsonContent.Create(new { model = _s.OpenAIModel, messages, stream = false })
        };
        if (!string.IsNullOrWhiteSpace(_s.OpenAIApiKey))
            req.Headers.Add("Authorization", "Bearer " + _s.OpenAIApiKey);

        using var res = await Http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Serveur local {(int)res.StatusCode} : {json}");

        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        // Les modèles "raisonneurs" (Qwen3…) peuvent renvoyer leur réflexion : on la retire.
        text = Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
        return new LlmReply(text, _s.OpenAIModel, null, sw.Elapsed);
    }
}
