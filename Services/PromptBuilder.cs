using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TaskAgent.Models;

namespace TaskAgent.Services;

public static class PromptBuilder
{
    private static readonly CultureInfo Fr = new("fr-FR");

    public static string System(TaskItem t, IEnumerable<TaskItem> all, AppSettings s, bool canResearch)
    {
        var now = DateTime.Now;
        var sb = new StringBuilder();

        sb.AppendLine("Tu es l'agent personnel de suivi de tâches de l'utilisateur.");
        sb.AppendLine($"Nous sommes le {now.ToString("dddd d MMMM yyyy, HH:mm", Fr)}. Raisonne toujours à partir de cette date.");
        sb.AppendLine($"À propos de l'utilisateur : {s.AboutMe}");
        sb.AppendLine();
        sb.AppendLine("Ton rôle : l'aider à faire avancer et terminer ses tâches, comprendre pourquoi une tâche n'avance pas, et la débloquer.");
        sb.AppendLine("Règles :");
        sb.AppendLine("- Réponds en français, de façon concise et concrète.");
        sb.AppendLine("- Identifie la vraie cause du blocage (info manquante, dépendance externe, tâche trop grosse ou floue, manque de temps, aversion, problème technique) et traite la cause, pas le symptôme.");
        sb.AppendLine(canResearch
            ? "- Tu as accès au web (WebSearch, WebFetch). Dès qu'une réponse dépend de faits (démarches, prix, dates, règles, coordonnées), cherche plutôt que de deviner, privilégie les sources officielles et cite les liens utilisés."
            : "- Tu n'as pas accès au web : signale quand une information mérite d'être vérifiée.");
        sb.AppendLine("- Si une information indispensable te manque, pose au plus 2 questions précises.");
        sb.AppendLine("- Quand tu découpes la tâche en étapes, écris chaque nouvelle étape sur sa propre ligne au format exact « - [ ] étape » (l'utilisateur pourra les importer en sous-tâches). Ne répète pas les sous-tâches qui existent déjà.");
        sb.AppendLine("- Tiens compte de l'échéance pour prioriser, et signale clairement si elle paraît intenable.");
        sb.AppendLine("- Termine TOUJOURS ta réponse par une ligne unique commençant exactement par « Prochaine action : », suivie d'une action concrète faisable en moins de 15 minutes.");
        sb.AppendLine();
        sb.AppendLine("## Tâche concernée");
        sb.AppendLine($"Titre : {t.Title}");
        if (!string.IsNullOrWhiteSpace(t.Description)) sb.AppendLine($"Description : {t.Description}");
        sb.AppendLine($"Statut : {t.StatusLabel}");
        sb.AppendLine($"Créée le : {t.CreatedAt.ToString("d MMMM yyyy", Fr)}");
        sb.AppendLine($"Dernière avancée : {t.LastProgressAt.ToString("d MMMM yyyy HH:mm", Fr)} ({t.IdleLabel})");
        sb.AppendLine(t.DueDate is { } due
            ? $"Échéance : {due.ToString("dddd d MMMM yyyy", Fr)} ({t.DueLabel})"
            : "Échéance : aucune");
        if (t.HasTool)
        {
            var where = string.Join(" — ", new[] { t.ToolName, t.ToolProject is { Length: > 0 } p ? $"projet « {p} »" : null, t.ToolLink }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            sb.AppendLine($"Outil de gestion dédié : {where}");
            sb.AppendLine("(Le suivi détaillé de cette tâche se fait dans cet outil. Ne le duplique pas : aide surtout à avancer, " +
                          "et propose quand c'est utile ce qu'il faut y noter ou y mettre à jour.)");
        }
        if (t.HasSubTasks)
        {
            sb.AppendLine($"Sous-tâches ({t.SubProgressLabel}) :");
            foreach (var st in t.SubTasks)
                sb.AppendLine(st.IsDone ? $"- [x] {st.Title} (faite le {st.DoneAt:dd/MM})" : $"- [ ] {st.Title}");
        }
        if (!string.IsNullOrWhiteSpace(t.NextAction)) sb.AppendLine($"Prochaine action proposée précédemment : {t.NextAction}");

        var log = t.Log.TakeLast(10).ToList();
        if (log.Count > 0)
        {
            sb.AppendLine("Historique récent :");
            foreach (var l in log) sb.AppendLine($"- {l.At.ToString("dd/MM HH:mm", Fr)} [{l.Kind}] {l.Text}");
        }

        var others = all.Where(o => o != t && !o.IsDone).Take(15).ToList();
        if (others.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Autres tâches ouvertes (pour contexte et priorisation)");
            foreach (var o in others)
                sb.AppendLine($"- {o.Title} ({o.Subtitle} · {o.DueLabel}{(o.HasTool ? " · géré dans " + o.ToolSummary : "")})");
        }
        return sb.ToString();
    }

    public static string UnblockRequest(TaskItem t) =>
        "Je suis bloqué sur cette tâche." +
        (string.IsNullOrWhiteSpace(t.BlockerReason) ? "" : $" Raison : {t.BlockerReason}.") +
        " Aide-moi à la débloquer : diagnostique la vraie cause, puis propose LA plus petite prochaine action concrète.";

    public static string ResearchRequest(TaskItem t) =>
        "Fais les recherches utiles pour cette tâche à ma place : en quoi elle consiste concrètement, les démarches ou options " +
        "(avec liens officiels, coûts et délais quand c'est pertinent), les pièges à éviter, puis découpe-la en étapes « - [ ] ».";

    private static readonly Regex NextActionRx =
        new(@"^[\s>*_-]*Prochaine action\s*[*_]*\s*:\s*[*_]*\s*(?<a>.+?)\s*[*_]*\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex StepRx = new(@"^\s*[-*]\s*\[\s\]\s*(?<s>.+?)\s*$", RegexOptions.Multiline);

    /// <summary>Récupère les lignes « - [ ] étape » d'une réponse de l'agent.</summary>
    public static List<string> ExtractSteps(string reply) =>
        StepRx.Matches(reply).Select(m => m.Groups["s"].Value.Trim('*', '_', ' ')).Where(s => s.Length > 0).Distinct().ToList();

    public static string? ExtractNextAction(string reply)
    {
        var matches = NextActionRx.Matches(reply);
        return matches.Count == 0 ? null : matches[^1].Groups["a"].Value.Trim();
    }
}
