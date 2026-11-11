using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskAgent.Models;

namespace TaskAgent.Services;

/// <summary>
/// Examen de l'agent de chat par tâche, sans fenêtre :
///   dotnet run -- --eval eval/cas/cas.jsonl --sortie eval/runs/2026-11-07.jsonl [--fournisseur OpenAICompatible] [--modele qwen3:8b] [--cas 01,12] [--parallele 3]
/// Le candidat est le VRAI prompt (PromptBuilder.System) + le vrai cerveau (LlmFactory) : rien n'est recopié.
/// Prochaine action et étapes sont extraites ici par le code de l'appli et écrites dans la sortie,
/// pour que le juge (eval/juger.py) note exactement ce que l'interface verrait.
/// Codes de sortie : 0 = toutes les réponses obtenues, 2 = panne (arguments, fichier, au moins un cas sans réponse).
/// </summary>
public static class EvalRunner
{
    /// <summary>Profil fictif fixe : l'examen ne dépend pas du « À propos de moi » réel de l'utilisateur.</summary>
    public const string AboutMeFictif =
        "Salarié à temps plein, gère ses tâches personnelles et professionnelles le soir ; préfère des réponses courtes et concrètes.";

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    public static int Run(string[] args)
    {
        // Appli « fenêtrée » (WinExe) : sans console, Console.WriteLine ne s'afficherait nulle part.
        if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected) AttachConsole(-1);
        try
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { /* sortie redirigée sans console */ }
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PANNE : " + ex.Message);
            return 2;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

        var casPath = Opt("--eval") ?? throw new ArgumentException("--eval <cas.jsonl> manquant");
        var sortie = Opt("--sortie") ?? Path.Combine("eval", "runs", $"{DateTime.Now:yyyy-MM-dd_HHmm}.jsonl");
        var filtre = Opt("--cas")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var settings = AppSettings.Load();
        settings.AboutMe = AboutMeFictif;
        settings.AllowWebResearch = false; // notes comparables d'un passage à l'autre, et moins de quota
        if (Opt("--fournisseur") is { } f) settings.Provider = f;
        if (Opt("--modele") is { } m)
        {
            if (settings.Provider == "OpenAICompatible") settings.OpenAIModel = m;
            else if (settings.Provider == "AnthropicApi") settings.AnthropicModel = m;
            else settings.ClaudeCliModel = m;
        }
        var llm = LlmFactory.Create(settings);
        var parallele = int.TryParse(Opt("--parallele"), out var p) && p > 0 ? p : settings.Provider == "OpenAICompatible" ? 1 : 3;

        var cas = File.ReadAllLines(casPath, Encoding.UTF8)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonNode.Parse(l)!.AsObject())
            .Where(c => filtre is null || filtre.Any(x => ((string)c["id"]!).StartsWith(x)))
            .ToList();
        if (cas.Count == 0) throw new ArgumentException("aucun cas à jouer");

        Console.WriteLine($"Examen : {cas.Count} cas · candidat {llm.Name} · {parallele} en parallèle");
        var timeout = TimeSpan.FromMinutes(settings.ReplyTimeoutMinutes);
        using var gate = new SemaphoreSlim(parallele);
        var resultats = await Task.WhenAll(cas.Select(async c =>
        {
            await gate.WaitAsync();
            try { return await JouerAsync(c, llm, settings, timeout); }
            finally { gate.Release(); }
        }));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(sortie))!);
        await File.WriteAllLinesAsync(sortie, resultats.Select(r => r.ToJsonString(new JsonSerializerOptions
            { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })), new UTF8Encoding(false));

        var pannes = resultats.Count(r => r["erreur"] is not null);
        Console.WriteLine($"Sortie : {sortie} ({resultats.Length - pannes}/{resultats.Length} réponses)");
        return pannes == 0 ? 0 : 2;
    }

    private static async Task<JsonObject> JouerAsync(JsonObject c, ILlmProvider llm, AppSettings settings, TimeSpan timeout)
    {
        var id = (string)c["id"]!;
        var t = BuildTask(c["tache"]!.AsObject());
        var message = (string)c["message"]! switch
        {
            "@debloquer" => PromptBuilder.UnblockRequest(t),
            "@rechercher" => PromptBuilder.ResearchRequest(t),
            var texte => texte
        };
        var res = new JsonObject
        {
            ["id"] = id,
            ["fournisseur"] = settings.Provider,
            ["candidat"] = llm.Name,
            ["date"] = DateTime.Now.ToString("s"),
            ["message"] = message,
            ["sous_taches_existantes"] = new JsonArray(t.SubTasks.Select(s => (JsonNode)s.Title).ToArray()),
            ["prochaine_action_precedente"] = t.NextAction,
        };
        try
        {
            t.Chat.Add(new ChatMessage { Role = "user", Content = message });
            using var cts = new CancellationTokenSource(timeout);
            var reply = await llm.ChatAsync(PromptBuilder.System(t, new[] { t }, settings, llm.CanResearch), t.Chat.ToList(), cts.Token);
            res["modele"] = reply.Model;
            res["duree_s"] = Math.Round(reply.Duration.TotalSeconds, 1);
            res["reponse"] = reply.Text;
            res["prochaine_action"] = PromptBuilder.ExtractNextAction(reply.Text);
            res["etapes"] = new JsonArray(PromptBuilder.ExtractSteps(reply.Text).Select(s => (JsonNode)s).ToArray());
            Console.WriteLine($"  ✔ {id} ({reply.Duration.TotalSeconds:0} s)");
        }
        catch (Exception ex)
        {
            res["erreur"] = ex is OperationCanceledException ? "délai dépassé" : ex.Message;
            Console.WriteLine($"  ✘ {id} : {res["erreur"]}");
        }
        return res;
    }

    /// <summary>Tâche fictive du cas. Les dates sont relatives au jour de l'examen (le prompt contient la date du jour).</summary>
    private static TaskItem BuildTask(JsonObject j)
    {
        var now = DateTime.Now;
        DateTime Ago(string key, double def) => now.AddDays(-(j[key]?.GetValue<double>() ?? def));

        // Sous-tâches assemblées avant d'être rattachées : rien n'est écrit dans l'historique fictif.
        var subs = new ObservableCollection<SubTask>();
        foreach (var s in j["sous_taches"]?.AsArray() ?? new JsonArray())
        {
            var st = new SubTask { Title = (string)s!["titre"]! };
            if (s["faite"]?.GetValue<bool>() == true) { st.IsDone = true; st.DoneAt = now.AddDays(-(s["il_y_a_jours"]?.GetValue<double>() ?? 3)); }
            subs.Add(st);
        }

        var t = new TaskItem
        {
            Title = (string)j["titre"]!,
            Description = (string?)j["description"] ?? "",
            SubTasks = subs,
            State = Enum.Parse<TaskState>((string?)j["statut"] ?? "Todo"),
            BlockerReason = (string?)j["blocage"],
            NextAction = (string?)j["prochaine_action"],
            DueDate = j["echeance_jours"] is { } e ? now.Date.AddDays(e.GetValue<int>()) : null,
        };
        if (j["outil"] is JsonObject o)
        {
            t.ToolName = (string?)o["nom"];
            t.ToolProject = (string?)o["projet"];
            t.ToolLink = (string?)o["lien"];
        }
        t.Log = new ObservableCollection<LogEntry>((j["historique"]?.AsArray() ?? new JsonArray())
            .Select(h => new LogEntry
            {
                At = now.AddDays(-(h!["il_y_a_jours"]?.GetValue<double>() ?? 1)),
                Kind = (string?)h["type"] ?? "note",
                Text = (string)h["texte"]!
            }));
        t.CreatedAt = Ago("creee_il_y_a_jours", 3);
        t.LastProgressAt = Ago("sans_avancee_jours", 1);
        return t;
    }
}
