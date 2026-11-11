using Avalonia.Media.Immutable;
using Avalonia.Media;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization;
using System;

namespace TaskAgent.Models;

public enum TaskState
{
    Todo,
    InProgress,
    Blocked,
    Done
}

public class LogEntry
{
    public DateTime At { get; set; } = DateTime.Now;

    public string Kind { get; set; } = "note";

    public string Text { get; set; } = "";

    [JsonIgnore]
    public string Display => $"{At:dd/MM HH:mm}  {Text}";
}

public class ChatMessage
{
    public string Role { get; set; } = "user";

    public string Content { get; set; } = "";

    public DateTime At { get; set; } = DateTime.Now;

    public string? Meta { get; set; }

    [JsonIgnore]
    public bool IsUser => Role == "user";

    [JsonIgnore]
    public string Header => (IsUser ? "Moi" : "Agent") + " · " + At.ToString("dd/MM HH:mm") + (string.IsNullOrEmpty(Meta) ? "" : (" · " + Meta));
}

public class SubTask : INotifyPropertyChanged
{
    private string _title = "";

    private bool _isDone;

    public string Title
    {
        get
        {
            return _title;
        }
        set
        {
            string text = value?.Trim() ?? "";
            if (text.Length == 0 || text == _title)
            {
                On("Title");
                return;
            }
            _title = text;
            On("Title");
        }
    }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? DoneAt { get; set; }

    public bool IsDone
    {
        get
        {
            return _isDone;
        }
        set
        {
            if (_isDone != value)
            {
                _isDone = value;
                DoneAt = (value ? new DateTime?(DateTime.Now) : ((DateTime?)null));
                On("IsDone");
                On("DoneLabel");
            }
        }
    }

    [JsonIgnore]
    public string DoneLabel
    {
        get
        {
            if (IsDone)
            {
                DateTime? doneAt = DoneAt;
                if (doneAt.HasValue)
                {
                    DateTime valueOrDefault = doneAt.GetValueOrDefault();
                    return $"fait le {valueOrDefault:dd/MM}";
                }
            }
            return "";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void On([CallerMemberName] string? n = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public class TaskItem : INotifyPropertyChanged
{
    private static readonly IBrush Red = new ImmutableSolidColorBrush(Color.Parse("#E5484D"));

    private static readonly IBrush Orange = new ImmutableSolidColorBrush(Color.Parse("#F5A524"));

    private static readonly IBrush Green = new ImmutableSolidColorBrush(Color.Parse("#30A46C"));

    private static readonly IBrush Grey = new ImmutableSolidColorBrush(Color.Parse("#808080"));

    private string _title = "";

    private string _description = "";

    private TaskState _state;

    private DateTime? _nextReminderAt;

    private DateTime? _dueDate;

    private string? _blockerReason;

    private string? _nextAction;

    private string? _toolName;

    private string? _toolProject;

    private string? _toolLink;

    private ObservableCollection<SubTask> _subTasks = new ObservableCollection<SubTask>();

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title
    {
        get
        {
            return _title;
        }
        set
        {
            _title = value;
            On("Title");
        }
    }

    public string Description
    {
        get
        {
            return _description;
        }
        set
        {
            _description = value;
            On("Description");
        }
    }

    public TaskState State
    {
        get
        {
            return _state;
        }
        set
        {
            _state = value;
            On("State");
            On("IsDone");
            NotifyComputed();
        }
    }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime LastProgressAt { get; set; } = DateTime.Now;

    public DateTime? DoneAt { get; set; }

    public DateTime? LastReminderAt { get; set; }

    public static double DefaultReminderDays { get; set; } = 7.0;

    public DateTime? NextReminderAt
    {
        get
        {
            return _nextReminderAt;
        }
        set
        {
            _nextReminderAt = value;
            On("NextReminderAt");
            NotifyComputed();
        }
    }

    [JsonIgnore]
    public DateTime NextReminderDue => NextReminderAt ?? ((LastReminderAt > LastProgressAt) ? LastReminderAt.Value : LastProgressAt).AddDays(DefaultReminderDays);

    [JsonIgnore]
    public DateTime? ReminderDate
    {
        get
        {
            return NextReminderDue.Date;
        }
        set
        {
            NextReminderAt = value?.Date;
        }
    }

    [JsonIgnore]
    public string NextReminderLabel
    {
        get
        {
            if (IsDone)
            {
                return "";
            }
            int days = (NextReminderDue.Date - DateTime.Today).Days;
            if (days > 0)
            {
                if (days != 1)
                {
                    return $"rappel dans {days} j";
                }
                return "rappel : demain";
            }
            return "rappel : aujourd'hui";
        }
    }

    public DateTime? DueDate
    {
        get
        {
            return _dueDate;
        }
        set
        {
            _dueDate = value?.Date;
            On("DueDate");
            NotifyComputed();
        }
    }

    public string? BlockerReason
    {
        get
        {
            return _blockerReason;
        }
        set
        {
            _blockerReason = value;
            On("BlockerReason");
            NotifyComputed();
        }
    }

    public string? NextAction
    {
        get
        {
            return _nextAction;
        }
        set
        {
            _nextAction = value;
            On("NextAction");
            NotifyComputed();
        }
    }

    public string? ToolName
    {
        get
        {
            return _toolName;
        }
        set
        {
            _toolName = value;
            On("ToolName");
            NotifyComputed();
        }
    }

    public string? ToolProject
    {
        get
        {
            return _toolProject;
        }
        set
        {
            _toolProject = value;
            On("ToolProject");
            NotifyComputed();
        }
    }

    public string? ToolLink
    {
        get
        {
            return _toolLink;
        }
        set
        {
            _toolLink = value;
            On("ToolLink");
            NotifyComputed();
        }
    }

    public ObservableCollection<LogEntry> Log { get; set; } = new ObservableCollection<LogEntry>();

    public ObservableCollection<ChatMessage> Chat { get; set; } = new ObservableCollection<ChatMessage>();

    public ObservableCollection<SubTask> SubTasks
    {
        get
        {
            return _subTasks;
        }
        set
        {
            _subTasks = value;
            AttachSubTasks();
        }
    }

    [JsonIgnore]
    public bool IsDone
    {
        get
        {
            return State == TaskState.Done;
        }
        set
        {
            if (value != IsDone)
            {
                if (value)
                {
                    DoneAt = DateTime.Now;
                    BlockerReason = null;
                    State = TaskState.Done;
                    AddLog("done", "Tâche terminée ✔");
                }
                else
                {
                    DoneAt = null;
                    State = TaskState.InProgress;
                    AddLog("reopen", "Tâche rouverte");
                }
            }
        }
    }

    [JsonIgnore]
    public TimeSpan Idle => DateTime.Now - LastProgressAt;

    [JsonIgnore]
    public string StatusLabel => State switch
    {
        TaskState.Todo => "À faire", 
        TaskState.InProgress => "En cours", 
        TaskState.Blocked => string.IsNullOrWhiteSpace(BlockerReason) ? "Bloquée" : ("Bloquée : " + BlockerReason), 
        TaskState.Done => "Terminée", 
        _ => "", 
    };

    [JsonIgnore]
    public string IdleLabel
    {
        get
        {
            if (!IsDone)
            {
                if (!(Idle.TotalHours < 1.0))
                {
                    if (Idle.TotalDays < 1.0)
                    {
                        return $"sans avancée depuis {(int)Idle.TotalHours} h";
                    }
                    return $"sans avancée depuis {(int)Idle.TotalDays} j";
                }
                return "bougé il y a moins d'1 h";
            }
            return $"finie le {DoneAt:dd/MM}";
        }
    }

    [JsonIgnore]
    public string AgeLabel
    {
        get
        {
            TimeSpan timeSpan = DateTime.Now - CreatedAt;
            string value;
            if (timeSpan.TotalDays >= 1.0)
            {
                value = $"{(int)timeSpan.TotalDays} j";
            }
            else
            {
                value = ((timeSpan.TotalHours >= 1.0) ? $"{(int)timeSpan.TotalHours} h" : "moins d'1 h");
            }
            return $"créée il y a {value} ({CreatedAt:dd/MM})";
        }
    }

    [JsonIgnore]
    public int? DaysLeft
    {
        get
        {
            DateTime? dueDate = DueDate;
            if (dueDate.HasValue)
            {
                DateTime valueOrDefault = dueDate.GetValueOrDefault();
                return (int)(valueOrDefault - DateTime.Today).TotalDays;
            }
            return null;
        }
    }

    [JsonIgnore]
    public string DueLabel
    {
        get
        {
            int? daysLeft = DaysLeft;
            if (daysLeft.HasValue)
            {
                if (IsDone)
                {
                    return $"échéance {DueDate:dd/MM}";
                }
                int valueOrDefault = daysLeft.GetValueOrDefault();
                if (valueOrDefault >= 0)
                {
                    return valueOrDefault switch
                    {
                        0 => "échéance aujourd'hui", 
                        1 => "échéance demain", 
                        _ => $"échéance dans {DaysLeft} j ({DueDate:dd/MM})", 
                    };
                }
                return $"⚠ en retard de {-DaysLeft} j ({DueDate:dd/MM})";
            }
            return "pas d'échéance";
        }
    }

    [JsonIgnore]
    public IBrush DueBrush
    {
        get
        {
            if (!IsDone && DaysLeft.HasValue)
            {
                if (!(DaysLeft < 0))
                {
                    if (!(DaysLeft <= 2))
                    {
                        return Green;
                    }
                    return Orange;
                }
                return Red;
            }
            return Grey;
        }
    }

    [JsonIgnore]
    public int SubDone => Enumerable.Count(SubTasks, (SubTask s) => s.IsDone);

    [JsonIgnore]
    public bool HasSubTasks => SubTasks.Count > 0;

    [JsonIgnore]
    public string SubProgressLabel
    {
        get
        {
            if (!HasSubTasks)
            {
                return "aucune sous-tâche";
            }
            return $"{SubDone}/{SubTasks.Count} sous-tâches";
        }
    }

    [JsonIgnore]
    public double SubProgressPercent
    {
        get
        {
            if (!HasSubTasks)
            {
                return 0.0;
            }
            return 100.0 * (double)SubDone / (double)SubTasks.Count;
        }
    }

    [JsonIgnore]
    public string Subtitle => StatusLabel + " · " + IdleLabel;

    [JsonIgnore]
    public string MetaLine
    {
        get
        {
            if (!HasSubTasks)
            {
                return AgeLabel;
            }
            return SubProgressLabel + " · " + AgeLabel;
        }
    }

    [JsonIgnore]
    public bool HasNextAction
    {
        get
        {
            if (!IsDone)
            {
                return !string.IsNullOrWhiteSpace(NextAction);
            }
            return false;
        }
    }

    [JsonIgnore]
    public string NextActionLabel => "→ " + NextAction;

    [JsonIgnore]
    public bool HasTool
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ToolName) && string.IsNullOrWhiteSpace(ToolProject))
            {
                return !string.IsNullOrWhiteSpace(ToolLink);
            }
            return true;
        }
    }

    [JsonIgnore]
    public bool HasToolLink => !string.IsNullOrWhiteSpace(ToolLink);

    [JsonIgnore]
    public string ToolSummary
    {
        get
        {
            List<string> list = (from p in new string[2] { ToolName, ToolProject }
                where !string.IsNullOrWhiteSpace(p)
                select p.Trim()).ToList();
            if (list.Count <= 0)
            {
                return "projet dédié";
            }
            return string.Join(" · ", list);
        }
    }

    [JsonIgnore]
    public string ToolLabel => "\ud83d\uddc2 " + ToolSummary + (HasToolLink ? " \ud83d\udd17" : "");

    [JsonIgnore]
    public string ToolHeader
    {
        get
        {
            if (!HasTool)
            {
                return "Outil de gestion / projet dédié (aucun)";
            }
            return "Outil de gestion · " + ToolSummary;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void On([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public TaskItem()
    {
        AttachSubTasks();
    }

    public void ScheduleReminder(double days)
    {
        DateTime dateTime = DateTime.Now.AddDays(days);
        DateTime? dueDate = DueDate;
        if (dueDate.HasValue)
        {
            DateTime valueOrDefault = dueDate.GetValueOrDefault();
            if (dateTime > valueOrDefault.AddDays(-1.0))
            {
                dateTime = ((valueOrDefault.AddDays(-1.0) > DateTime.Now.AddDays(1.0)) ? valueOrDefault.AddDays(-1.0) : DateTime.Now.AddDays(1.0));
            }
        }
        NextReminderAt = dateTime;
    }

    private void AttachSubTasks()
    {
        foreach (SubTask subTask2 in _subTasks)
        {
            subTask2.PropertyChanged += OnSubTaskChanged;
        }
        _subTasks.CollectionChanged += (object? _, NotifyCollectionChangedEventArgs e) =>
        {
            if (e.NewItems != null)
            {
                foreach (SubTask newItem in e.NewItems)
                {
                    newItem.PropertyChanged += OnSubTaskChanged;
                }
            }
            if (e.OldItems != null)
            {
                foreach (SubTask oldItem in e.OldItems)
                {
                    oldItem.PropertyChanged -= OnSubTaskChanged;
                }
            }
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (SubTask newItem2 in e.NewItems)
                {
                    AddLog("subtask", "Sous-tâche ajoutée : " + newItem2.Title, countsAsProgress: false);
                }
            }
            On("SubTasks");
            NotifyComputed();
        };
    }

    private void OnSubTaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!(sender is SubTask subTask))
        {
            return;
        }
        if (e.PropertyName == "Title")
        {
            On("SubTasks");
        }
        else if (!(e.PropertyName != "IsDone"))
        {
            AddLog("subtask", (subTask.IsDone ? "✔ " : "↺ Rouverte : ") + subTask.Title, subTask.IsDone);
            if (subTask.IsDone && State == TaskState.Todo)
            {
                State = TaskState.InProgress;
            }
            On("SubTasks");
        }
    }

    public void AddLog(string kind, string text, bool countsAsProgress = true)
    {
        Log.Add(new LogEntry
        {
            Kind = kind,
            Text = text
        });
        if (countsAsProgress)
        {
            LastProgressAt = DateTime.Now;
            DateTime? nextReminderAt = NextReminderAt;
            if (nextReminderAt.HasValue)
            {
                DateTime valueOrDefault = nextReminderAt.GetValueOrDefault();
                if (valueOrDefault < DateTime.Now.AddDays(DefaultReminderDays))
                {
                    NextReminderAt = DateTime.Now.AddDays(DefaultReminderDays);
                }
            }
        }
        NotifyComputed();
    }

    public void NotifyComputed()
    {
        string[] array = new string[19]
        {
            "StatusLabel", "IdleLabel", "Subtitle", "AgeLabel", "DueLabel", "DueBrush", "SubProgressLabel", "SubProgressPercent", "HasSubTasks", "MetaLine",
            "HasNextAction", "NextActionLabel", "NextReminderLabel", "ReminderDate", "HasTool", "HasToolLink", "ToolSummary", "ToolLabel", "ToolHeader"
        };
        foreach (string name in array)
        {
            On(name);
        }
    }
}
