using Avalonia.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using TaskAgent.Models;

namespace TaskAgent.Services;

public class ReminderService
{
    private readonly AppSettings _s;

    private readonly Func<IEnumerable<TaskItem>> _tasks;

    private readonly DispatcherTimer _timer;

    private bool _busy;

    public event Func<TaskItem, Task>? StaleTask;

    public ReminderService(AppSettings s, Func<IEnumerable<TaskItem>> tasks)
    {
        _s = s;
        _tasks = tasks;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(Math.Max(1, s.CheckIntervalMinutes))
        };
        _timer.Tick += async (object? _, EventArgs _) =>
        {
            await CheckAsync();
        };
    }

    public void Start()
    {
        _timer.Start();
        DispatcherTimer.RunOnce(async () =>
        {
            await CheckAsync();
        }, TimeSpan.FromSeconds(20.0));
    }

    public async Task CheckAsync()
    {
        if (_busy || StaleTask == null)
        {
            return;
        }
        _busy = true;
        try
        {
            DateTime now = DateTime.Now;
            TaskItem taskItem = (from t in _tasks()
                where !t.IsDone
                where now >= t.NextReminderDue || (t.DaysLeft <= 1 && (now - t.LastProgressAt).TotalHours >= _s.SnoozeHours && (!t.LastReminderAt.HasValue || (now - t.LastReminderAt.Value).TotalHours >= _s.SnoozeHours))
                orderby t.DaysLeft ?? int.MaxValue, t.LastProgressAt
                select t).FirstOrDefault();
            if (taskItem != null)
            {
                taskItem.LastReminderAt = now;
                await StaleTask(taskItem);
            }
        }
        finally
        {
            _busy = false;
        }
    }
}
