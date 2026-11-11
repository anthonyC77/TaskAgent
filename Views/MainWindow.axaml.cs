using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TaskAgent.Models;
using TaskAgent.Services;

namespace TaskAgent.Views;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> PersistedProps = new()
    {
        nameof(TaskItem.Title), nameof(TaskItem.Description), nameof(TaskItem.State),
        nameof(TaskItem.BlockerReason), nameof(TaskItem.NextAction),
        nameof(TaskItem.DueDate), nameof(TaskItem.SubTasks), nameof(TaskItem.NextReminderAt),
        nameof(TaskItem.ToolName), nameof(TaskItem.ToolProject), nameof(TaskItem.ToolLink)
    };

    private static readonly string[] ToolSuggestions =
        { "Projet Claude", "Notion", "Trello", "Jira", "GitHub", "Google Drive", "OneNote", "Obsidian", "Dossier", "Agenda" };

    private readonly TaskStore _store = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<TaskItem> _tasks;
    private readonly ILlmProvider _llm;
    private readonly ReminderService _reminders;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _busyTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _busySince;
    private string _busyTask = "";
    private bool _userCancelled;

    public MainWindow()
    {
        InitializeComponent();

        _tasks = new(_store.Load().OrderBy(t => t.IsDone).ThenByDescending(t => t.CreatedAt));
        foreach (var t in _tasks) Hook(t);
        TaskList.ItemsSource = _tasks;

        _llm = LlmFactory.Create(_settings);
        TodayText.Text = DateTime.Now.ToString("dddd d MMMM yyyy", new CultureInfo("fr-FR")) + " · IA : " + _llm.Name +
                         (_llm.CanResearch ? " · web ✓" : "");
        try { Startup.Apply(_settings.LaunchAtStartup); }
        catch (Exception ex) { StatusText.Text = "⚠ Lancement au démarrage non configuré : " + ex.Message; }
        _busyTimer.Tick += (_, _) => ShowBusyStatus();

        // --- Événements UI ---
        AddBtn.Click += (_, _) => AddTask();
        NewTaskBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { AddTask(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);

        ChatInput.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                await SendFromInputAsync();
            }
        }, RoutingStrategies.Tunnel);

        SendBtn.Click += async (_, _) => await SendFromInputAsync();
        UnblockBtn.Click += async (_, _) => { if (Current is { } t) await SendAsync(t, PromptBuilder.UnblockRequest(t)); };
        ResearchBtn.Click += async (_, _) => { if (Current is { } t) await SendAsync(t, PromptBuilder.ResearchRequest(t)); };
        CancelBtn.Click += (_, _) => { _userCancelled = true; _cts?.Cancel(); };
        ProgressBtn.Click += async (_, _) => await ProgressAsync();
        BlockedBtn.Click += async (_, _) => await AskBlockerAsync(isReminder: false);
        DeleteBtn.Click += async (_, _) => await DeleteAsync();
        SettingsBtn.Click += (_, _) => OpenSettings();
        MemoryBtn.Click += (_, _) => OpenMemory();
        KindleBtn.Click += (_, _) => OpenKindle();
        SitePrintBtn.Click += (_, _) => OpenSitePrint();

        // --- Échéance & sous-tâches ---
        ClearDueBtn.Click += (_, _) => { if (Current is { } t) t.DueDate = null; };
        NewSubTaskBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { AddSubTask(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        ImportStepsBtn.Click += (_, _) => ImportStepsFromAgent();

        // --- Outil de gestion dédié ---
        ToolNameBox.ItemsSource = ToolSuggestions;
        OpenToolBtn.Click += (_, _) => OpenToolLink();
        BrowseToolBtn.Click += async (_, _) => await BrowseToolFolderAsync();
        FindToolBtn.Click += async (_, _) => await FindToolLinkAsync();
        ClearToolBtn.Click += (_, _) =>
        {
            if (Current is not { } t) return;
            t.ToolName = t.ToolProject = t.ToolLink = null;
        };
        // Les boutons ✕ ▲ ▼ des sous-tâches remontent jusqu'ici (événement routé)
        SubTaskList.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is not Button { DataContext: SubTask s } b || Current is not { } t) return;
            if (b.Classes.Contains("remove"))
            {
                t.SubTasks.Remove(s);
                t.AddLog("subtask", "Sous-tâche supprimée : " + s.Title, countsAsProgress: false);
                Save();
            }
            else if (b.Classes.Contains("up")) MoveSubTask(t, s, -1);
            else if (b.Classes.Contains("down")) MoveSubTask(t, s, +1);
        });
        // Édition du titre : Entrée valide, Échap annule, Alt+↑/↓ déplace
        SubTaskList.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Source is not TextBox { DataContext: SubTask s } box || Current is not { } t) return;
            switch (e.Key)
            {
                case Key.Enter:
                    s.Title = box.Text ?? ""; // valide sans attendre la perte du focus
                    SubTaskList.Focus(); // quitte le champ
                    e.Handled = true;
                    break;
                case Key.Escape:
                    box.Text = s.Title;
                    SubTaskList.Focus();
                    e.Handled = true;
                    break;
                case Key.Up or Key.Down when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                    s.Title = box.Text ?? ""; // valide sans attendre la perte du focus
                    MoveSubTask(t, s, e.Key == Key.Up ? -1 : +1);
                    e.Handled = true;
                    break;
            }
        }, RoutingStrategies.Tunnel);
        TaskList.SelectionChanged += (_, _) => ShowTask(Current);

        ShowTask(null);
        if (_tasks.Count > 0) TaskList.SelectedIndex = 0;

        // --- Relances automatiques ---
        TaskItem.DefaultReminderDays = Math.Max(1, _settings.ReminderEveryDays);
        _reminders = new ReminderService(_settings, () => _tasks);
        _reminders.StaleTask += OnStaleTaskAsync;
        _reminders.Start();

        // Rafraîchit les "sans avancée depuis X h" chaque minute
        var refresh = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        refresh.Tick += (_, _) => { foreach (var t in _tasks) t.NotifyComputed(); };
        refresh.Start();

        // --- Mémoire vive : jauge toutes les 5 s, alerte si saturée ---
        MemoryBtn.IsVisible = MemoryMonitor.IsSupported;
        if (MemoryMonitor.IsSupported)
        {
            var memTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            memTimer.Tick += (_, _) => UpdateMemory();
            memTimer.Start();
            UpdateMemory();
        }

        // --- Version : numéro dans le titre, bandeau quand une nouvelle version est déployée ---
        Title = $"TaskAgent {AppVersion.Label}";
        Avalonia.Controls.ToolTip.SetTip(VersionBtn, $"TaskAgent {AppVersion.Label} · clic = nouveautés");
        VersionBtn.Click += (_, _) => OpenReleaseNotes();
        UpdateNotesBtn.Click += (_, _) => OpenReleaseNotes();
        UpdateRestartBtn.Click += (_, _) => RestartOnLatest();
        var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        updateTimer.Tick += (_, _) => CheckForUpdate();
        updateTimer.Start();
        CheckForUpdate();

        Closing += (_, _) => Save();
    }

    // ---------------- Versions ----------------

    private void CheckForUpdate()
    {
        if (AppVersion.UpdateAvailable() is not { } u) { UpdateBanner.IsVisible = false; return; }
        UpdateText.Text = AppVersion.IsDev
            ? $"Tu es sur la version de travail (dev). La version {u.Version.ToString(3)} est déployée : passe dessus pour l'usage normal."
            : $"La version {u.Version.ToString(3)} est prête (tu es en {AppVersion.Label}). Redémarre quand ça t'arrange.";
        UpdateRestartBtn.Content = AppVersion.IsDev ? $"Passer à la {u.Version.ToString(3)}" : "🔄 Redémarrer";
        UpdateBanner.IsVisible = true;
    }

    private void OpenReleaseNotes()
    {
        var w = new ReleaseNotesWindow();
        w.RestartRequested += RestartOnLatest;
        w.Show(this);
    }

    /// <summary>Enregistre, lance la nouvelle version, puis ferme celle-ci (tes données sont dans %AppData%, partagées entre versions).</summary>
    private void RestartOnLatest()
    {
        if (AppVersion.UpdateAvailable() is not { } u) return;
        Save();
        if (!AppVersion.Launch(u.Exe)) { StatusText.Text = $"⚠ Impossible de lancer {u.Exe}"; return; }
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
            d.Shutdown();
        else Close();
    }

    private TaskItem? Current => TaskList.SelectedItem as TaskItem;

    private void Hook(TaskItem t) => t.PropertyChanged += OnTaskChanged;

    private void OnTaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } p && PersistedProps.Contains(p)) Save();
    }

    private void Save()
    {
        try { _store.Save(_tasks); }
        catch (Exception ex) { StatusText.Text = "⚠ Erreur de sauvegarde : " + ex.Message; }
    }

    // ---------------- Tâches ----------------

    private void AddTask()
    {
        var title = NewTaskBox.Text?.Trim();
        if (string.IsNullOrEmpty(title)) return;

        var t = new TaskItem { Title = title };
        t.AddLog("created", "Tâche créée");
        t.ScheduleReminder(Math.Max(1, _settings.FirstReminderAfterDays)); // on laisse du temps à une tâche neuve
        Hook(t);
        _tasks.Insert(0, t);
        NewTaskBox.Text = "";
        TaskList.SelectedItem = t;
        Save();

        // L'agent part chercher tout seul pendant que tu passes à autre chose.
        if (_settings.AutoResearchNewTasks && _llm.CanResearch && _cts is null)
            _ = SendAsync(t, PromptBuilder.ResearchRequest(t));
    }

    private void AddSubTask()
    {
        var title = NewSubTaskBox.Text?.Trim();
        if (Current is not { } t || string.IsNullOrEmpty(title)) return;
        t.SubTasks.Add(new SubTask { Title = title });
        NewSubTaskBox.Text = "";
        Save();
    }

    private void MoveSubTask(TaskItem t, SubTask s, int delta)
    {
        var from = t.SubTasks.IndexOf(s);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= t.SubTasks.Count) return;
        t.SubTasks.Move(from, to); // déclenche la sauvegarde via SubTasks
        // Garde le focus sur la sous-tâche déplacée pour pouvoir enchaîner Alt+↑/↓
        Dispatcher.UIThread.Post(() =>
            SubTaskList.ContainerFromIndex(to)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus(),
            DispatcherPriority.Background);
    }

    private void ImportStepsFromAgent()
    {
        if (Current is not { } t) return;
        var last = t.Chat.LastOrDefault(m => !m.IsUser);
        var steps = last is null ? new List<string>() : PromptBuilder.ExtractSteps(last.Content);
        var existing = t.SubTasks.Select(s => s.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var s in steps.Where(s => !existing.Contains(s)))
        {
            t.SubTasks.Add(new SubTask { Title = s });
            added++;
        }
        StatusText.Text = added > 0
            ? $"{added} sous-tâche(s) ajoutée(s) depuis la dernière réponse de l'agent."
            : "Aucune étape « - [ ] … » trouvée dans la dernière réponse. Demande-lui : « découpe cette tâche en étapes ».";
        Save();
    }

    private void ShowTask(TaskItem? t)
    {
        DetailPanel.DataContext = t;
        DetailPanel.IsVisible = t is not null;
        Placeholder.IsVisible = t is null;
        if (_cts is null) StatusText.Text = "";
        ScrollChatToEnd();
    }

    private async Task ProgressAsync()
    {
        if (Current is not { } t || t.IsDone) return;
        var note = await Dialogs.AskAsync(this, $"Qu'as-tu fait sur « {t.Title} » ?", "Note rapide (optionnel)");
        if (note is null) return;

        t.BlockerReason = null;
        t.State = TaskState.InProgress;
        t.AddLog("progress", string.IsNullOrWhiteSpace(note) ? "Avancée" : note.Trim());
        Save();
    }

    private async Task DeleteAsync()
    {
        if (Current is not { } t) return;
        if (!await Dialogs.ConfirmAsync(this, $"Supprimer « {t.Title} » et sa conversation ?")) return;
        t.PropertyChanged -= OnTaskChanged;
        _tasks.Remove(t);
        Save();
    }

    // ---------------- Relances / blocages ----------------

    private async Task OnStaleTaskAsync(TaskItem t)
    {
        Save();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false; // passe la fenêtre au premier plan
        TaskList.SelectedItem = t;
        await AskBlockerAsync(isReminder: true);
    }

    private async Task AskBlockerAsync(bool isReminder)
    {
        if (Current is not { } t || t.IsDone) return;
        var r = await new ReminderWindow(t, isReminder, _settings.ReminderEveryDays).ShowDialog<ReminderResult?>(this);

        if (r is null || r.Action == ReminderAction.Later)
        {
            t.LastReminderAt = DateTime.Now;
            t.ScheduleReminder(r?.NextInDays ?? _settings.ReminderEveryDays);
            Save();
            return;
        }

        switch (r.Action)
        {
            case ReminderAction.Done:
                t.IsDone = true;
                break;
            case ReminderAction.Progressed:
                t.BlockerReason = null;
                t.State = TaskState.InProgress;
                t.AddLog("progress", r.Details ?? "Avancée signalée lors d'une relance");
                break;
            default:
                t.BlockerReason = r.Reason;
                t.State = TaskState.Blocked;
                var text = r.Reason + (r.Details is { } d && d != r.Reason ? " — " + d : "");
                t.AddLog("blocker", text, countsAsProgress: false);
                t.LastReminderAt = DateTime.Now;
                break;
        }
        if (r.Action != ReminderAction.Done) t.ScheduleReminder(r.NextInDays); // ton choix l'emporte
        Save();

        if (r.Action == ReminderAction.Unblock)
            await SendAsync(t, PromptBuilder.UnblockRequest(t) + (r.Details is { } det ? $"\nDétails : {det}" : ""));
    }

    // ---------------- Chat ----------------

    private async Task SendFromInputAsync()
    {
        if (Current is not { } t) return;
        var text = ChatInput.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        ChatInput.Text = "";
        await SendAsync(t, text);
    }

    private async Task SendAsync(TaskItem t, string text)
    {
        if (_cts is not null) return; // une requête à la fois

        t.Chat.Add(new ChatMessage { Role = "user", Content = text });
        Save();
        ScrollChatToEnd();

        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(_settings.ReplyTimeoutMinutes));
        _busyTask = t.Title;
        SetBusy(true);
        try
        {
            var reply = await _llm.ChatAsync(PromptBuilder.System(t, _tasks, _settings, _llm.CanResearch), t.Chat.ToList(), _cts.Token);
            if (string.IsNullOrWhiteSpace(reply.Text))
                throw new InvalidOperationException("L'agent a renvoyé une réponse vide. Réessaie ou reformule.");
            t.Chat.Add(new ChatMessage { Role = "assistant", Content = reply.Text, Meta = reply.Meta });
            if (PromptBuilder.ExtractNextAction(reply.Text) is { } next) t.NextAction = next;
            if (t.State == TaskState.Todo) t.State = TaskState.InProgress;
            Save();
            StatusText.Text = Current == t ? "" : $"✔ L'agent a répondu sur « {t.Title} ».";
        }
        catch (OperationCanceledException)
        {
            ShowError(_userCancelled
                ? "Demande annulée."
                : $"Pas de réponse au bout de {_settings.ReplyTimeoutMinutes} min (ReplyTimeoutMinutes dans ⚙).");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _userCancelled = false;
            SetBusy(false);
            ScrollChatToEnd();
        }
    }

    private void SetBusy(bool busy)
    {
        SendBtn.IsEnabled = UnblockBtn.IsEnabled = ResearchBtn.IsEnabled = FindToolBtn.IsEnabled = !busy;
        CancelBtn.IsVisible = busy;
        StatusText.ClearValue(TextBlock.ForegroundProperty);
        if (busy)
        {
            _busySince = DateTime.Now;
            ShowBusyStatus();
            _busyTimer.Start();
        }
        else _busyTimer.Stop();
    }

    /// <summary>Compteur visible : on sait que ça travaille, sur quelle tâche, avec quel modèle.</summary>
    private void ShowBusyStatus()
    {
        if (_cts is null) return;
        var secs = (int)(DateTime.Now - _busySince).TotalSeconds;
        var what = _llm.CanResearch ? "réfléchit / cherche sur le web" : "réfléchit";
        StatusText.Text = $"⏳ {_llm.Name} {what}… {secs} s  (tâche « {_busyTask} »)";
    }

    private void ShowError(string message)
    {
        StatusText.Text = "⚠ " + message;
        StatusText.Foreground = Avalonia.Media.Brushes.IndianRed;
    }

    private void ScrollChatToEnd() =>
        Dispatcher.UIThread.Post(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);

    // ---------------- Mémoire vive ----------------

    private int _memHighStreak;
    private DateTime _lastMemAlert = DateTime.MinValue;
    private MemoryWindow? _memWindow;

    public static Avalonia.Media.IBrush MemoryBrush(int percent) => Avalonia.Media.Brush.Parse(
        percent >= 90 ? "#E5484D" : percent >= 80 ? "#F5A524" : "#30A46C");

    private void UpdateMemory()
    {
        if (MemoryMonitor.GetStatus() is not { } m) return;
        MemoryBtn.Content = $"RAM {m.UsedPercent} %";
        MemoryBtn.Foreground = MemoryBrush(m.UsedPercent);

        // Alerte seulement si ça dure (3 mesures ≈ 15 s), pour ignorer les pics passagers.
        var limit = _settings.MemoryAlertPercent;
        _memHighStreak = limit > 0 && m.UsedPercent >= limit ? _memHighStreak + 1 : 0;
        if (_memHighStreak >= 3 && _memWindow is null &&
            DateTime.Now - _lastMemAlert > TimeSpan.FromMinutes(_settings.MemoryAlertCooldownMinutes))
        {
            _lastMemAlert = DateTime.Now;
            OpenMemory($"⚠ Ta mémoire est à {m.UsedPercent} % depuis un moment : voici ce qui la remplit.");
        }
    }

    private void OpenMemory(string? alert = null)
    {
        if (_memWindow is not null) { _memWindow.Activate(); return; }
        _memWindow = new MemoryWindow(alert);
        _memWindow.AskAgent += snapshot => _ = AskAgentAboutMemoryAsync(snapshot);
        _memWindow.Closed += (_, _) => _memWindow = null;
        _memWindow.Show(this);
    }

    // ---------------- PDF → Kindle ----------------

    private KindleWindow? _kindleWindow;

    private void OpenKindle()
    {
        if (_kindleWindow is not null) { _kindleWindow.Activate(); return; }
        _kindleWindow = new KindleWindow(_llm, _settings);
        _kindleWindow.Closed += (_, _) => _kindleWindow = null;
        _kindleWindow.Show(this);
    }

    // ---------------- Imprimer un site ----------------

    private SitePrintWindow? _sitePrintWindow;

    private void OpenSitePrint()
    {
        if (_sitePrintWindow is not null) { _sitePrintWindow.Activate(); return; }
        _sitePrintWindow = new SitePrintWindow();
        _sitePrintWindow.Closed += (_, _) => _sitePrintWindow = null;
        _sitePrintWindow.Show(this);
    }

    /// <summary>Crée (ou réutilise) une tâche « Réduire l'usage mémoire » et y envoie l'état des lieux.</summary>
    private async Task AskAgentAboutMemoryAsync(string snapshot)
    {
        const string title = "Réduire l'usage mémoire du PC";
        var t = _tasks.FirstOrDefault(x => !x.IsDone && x.Title == title);
        if (t is null)
        {
            t = new TaskItem
            {
                Title = title,
                Description = "Mémoire vive souvent au-delà de 90 %. Objectif : rester durablement sous 80 % sans perdre mes onglets ni mon travail."
            };
            t.AddLog("created", "Tâche créée depuis la fenêtre Mémoire");
            Hook(t);
            _tasks.Insert(0, t);
        }
        TaskList.SelectedItem = t;
        Save();
        if (_cts is not null)
        {
            ChatInput.Text = snapshot;
            StatusText.Text = "L'agent est occupé : l'état des lieux est prêt dans la zone de saisie, envoie-le dès qu'il a fini.";
            return;
        }
        await SendAsync(t, snapshot);
    }

    private async Task BrowseToolFolderAsync()
    {
        if (Current is not { } t) return;
        var options = new FolderPickerOpenOptions { Title = $"Dossier de « {t.Title} »", AllowMultiple = false };
        // On rouvre là où pointe déjà le lien, s'il s'agit d'un dossier existant
        if (t.ToolLink is { } current && Directory.Exists(current.Trim().Trim('"')))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(current.Trim().Trim('"'));

        var picked = await StorageProvider.OpenFolderPickerAsync(options);
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;

        t.ToolLink = path;
        if (string.IsNullOrWhiteSpace(t.ToolName)) t.ToolName = "Dossier";
        if (string.IsNullOrWhiteSpace(t.ToolProject)) t.ToolProject = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
    }

    // ---------------- Retrouver le lien d'une tâche ----------------

    /// <summary>Jeton GitHub du coffre, demandé au premier usage. null = on se passe de GitHub cette fois-ci.</summary>
    private async Task<string?> GetGitHubTokenAsync()
    {
        if (Secrets.Get(LinkFinder.GitHubTokenName) is { } saved) return saved;
        var token = await Dialogs.AskTokenAsync(this, "Connexion à GitHub",
            "Pour retrouver tes dépôts, TaskAgent a besoin d'un jeton d'accès GitHub en lecture seule " +
            "(« fine-grained token », permission Metadata : Read-only, tous tes dépôts). " +
            "Il reste chiffré sur cet ordinateur et n'est jamais envoyé à l'IA : seuls les noms des dépôts le sont.",
            LinkFinder.GitHubTokenHelpUrl);
        if (string.IsNullOrEmpty(token)) return null;
        Secrets.Set(LinkFinder.GitHubTokenName, token);
        return token;
    }

    private async Task FindToolLinkAsync()
    {
        if (Current is not { } t || _cts is not null) return;

        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(_settings.ReplyTimeoutMinutes));
        _busyTask = t.Title;
        SetBusy(true);
        try
        {
            var candidates = new List<LinkCandidate>();
            if (_settings.UseGitHub && await GetGitHubTokenAsync() is { } token)
            {
                StatusText.Text = "Lecture de tes dépôts GitHub…";
                try { candidates.AddRange(await LinkFinder.GitHubReposAsync(token, _cts.Token)); }
                catch (UnauthorizedAccessException)
                {
                    Secrets.Delete(LinkFinder.GitHubTokenName); // il sera redemandé au prochain essai
                    throw new InvalidOperationException("Jeton GitHub refusé (expiré ou révoqué) : il a été oublié, reclique sur ✨ Trouver pour en saisir un nouveau.");
                }
            }
            candidates.AddRange(await Task.Run(() => LinkFinder.LocalRepos(_settings.LocalProjectRoots)));

            if (candidates.Count == 0)
                throw new InvalidOperationException("Aucun candidat : connecte GitHub ou renseigne LocalProjectRoots (⚙), ex. \"D:\\\\Dev\".");

            var reply = await _llm.ChatAsync(LinkFinder.SystemPrompt,
                new[] { new ChatMessage { Role = "user", Content = LinkFinder.Request(t, candidates) } }, _cts.Token);

            if (LinkFinder.Parse(reply.Text, candidates) is not { } s)
            {
                StatusText.Text = $"L'IA n'a trouvé aucun emplacement correspondant parmi {candidates.Count} candidats.";
                return;
            }
            SetBusy(false);
            if (!await Dialogs.ConfirmAsync(this, $"Lier « {t.Title} » à {s.Tool} « {s.Project} » ?\n{s.Link}\n\n{s.Why}")) return;

            t.ToolName = s.Tool;
            t.ToolProject = s.Project;
            t.ToolLink = s.Link;
            StatusText.Text = $"✔ Lien trouvé ({reply.Meta}).";
        }
        catch (OperationCanceledException)
        {
            ShowError(_userCancelled ? "Recherche annulée." : "Recherche trop longue, abandonnée.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _userCancelled = false;
            SetBusy(false);
        }
    }

    private void OpenToolLink()
    {
        if (Current is not { ToolLink: { } raw } || string.IsNullOrWhiteSpace(raw)) return;
        var link = raw.Trim().Trim('"');
        // « claude.ai/project/… » sans https:// → on l'ajoute
        if (!Uri.TryCreate(link, UriKind.Absolute, out _) && !Directory.Exists(link) && !File.Exists(link) && link.Contains('.'))
            link = "https://" + link;
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError($"Impossible d'ouvrir « {link} » : {ex.Message}");
        }
    }

    private void OpenSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppSettings.FilePath) { UseShellExecute = true });
            StatusText.Text = "Réglages ouverts. Redémarre l'appli pour appliquer les changements.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Impossible d'ouvrir {AppSettings.FilePath} : {ex.Message}";
        }
    }
}
