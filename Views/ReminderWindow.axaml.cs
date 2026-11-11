using Avalonia.Controls;
using TaskAgent.Models;

namespace TaskAgent.Views;

public enum ReminderAction { Later, Save, Unblock, Progressed, Done }

/// <param name="NextInDays">Dans combien de jours relancer cette tâche (choisi dans « Prochain rappel »).</param>
public record ReminderResult(ReminderAction Action, string? Reason, string? Details, double NextInDays);

public partial class ReminderWindow : Window
{
    private static readonly string[] Blockers =
    {
        "Il me manque une info ou une réponse",
        "J'attends quelqu'un / une dépendance",
        "C'est trop gros ou trop flou",
        "Problème technique que je ne sais pas résoudre",
        "Pas eu le temps / autre priorité",
        "Pas envie, ça me pèse"
    };
    private const string ProgressedLabel = "En fait, j'ai avancé";
    private const string DoneLabel = "C'est terminé";

    private static readonly (string Label, double Days)[] Delays =
    {
        ("demain", 1),
        ("dans 3 jours", 3),
        ("dans 1 semaine", 7),
        ("dans 2 semaines", 14),
        ("dans 1 mois", 30)
    };

    private double NextInDays => Delays[Math.Max(0, NextReminderBox.SelectedIndex)].Days;

    public ReminderWindow() : this(null, false) { } // requis par le designer

    public ReminderWindow(TaskItem? task, bool isReminder, double defaultDays = 7)
    {
        InitializeComponent();

        NextReminderBox.ItemsSource = Delays.Select(d => d.Label).ToList();
        var index = Array.FindIndex(Delays, d => d.Days >= defaultDays);
        NextReminderBox.SelectedIndex = index >= 0 ? index : 2;

        if (task is not null)
            HeaderText.Text = isReminder
                ? $"« {task.Title} » est {task.IdleLabel}."
                : $"Qu'est-ce qui bloque sur « {task.Title} » ?";

        foreach (var label in Blockers.Append(ProgressedLabel).Append(DoneLabel))
            Reasons.Children.Add(new RadioButton { Content = label, GroupName = "reason" });

        LaterBtn.IsVisible = isReminder;
        LaterBtn.Click += (_, _) => Close(new ReminderResult(ReminderAction.Later, null, null, NextInDays));
        SaveBtn.Click += (_, _) => Close(Build(ReminderAction.Save));
        UnblockBtn.Click += (_, _) => Close(Build(ReminderAction.Unblock));
    }

    private ReminderResult Build(ReminderAction action)
    {
        var selected = Reasons.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Content as string;
        var details = string.IsNullOrWhiteSpace(DetailsBox.Text) ? null : DetailsBox.Text.Trim();

        return selected switch
        {
            DoneLabel => new(ReminderAction.Done, null, details, NextInDays),
            ProgressedLabel => new(ReminderAction.Progressed, null, details, NextInDays),
            _ => new(action, selected ?? details ?? "Non précisé", details, NextInDays)
        };
    }
}
