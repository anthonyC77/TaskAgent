using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.Json;
using System;
using TaskAgent.Models;

namespace TaskAgent.Services;

public static class AppPaths
{
    public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { (JsonConverter)new JsonStringEnumConverter() }
    };

    public static string DataDir { get; } = CreateDataDir();

    private static string CreateDataDir()
    {
        string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskAgent");
        Directory.CreateDirectory(text);
        return text;
    }
}

public class TaskStore
{
    private readonly string _path = Path.Combine(AppPaths.DataDir, "tasks.json");

    public List<TaskItem> Load()
    {
        if (!File.Exists(_path))
        {
            return new List<TaskItem>();
        }
        try
        {
            return JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(_path), AppPaths.Json) ?? new List<TaskItem>();
        }
        catch
        {
            File.Copy(_path, _path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            return new List<TaskItem>();
        }
    }

    public void Save(IEnumerable<TaskItem> tasks)
    {
        string text = _path + ".tmp";
        File.WriteAllText(text, JsonSerializer.Serialize(tasks, AppPaths.Json));
        File.Move(text, _path, overwrite: true);
    }
}

public class AppSettings
{
    public string Provider { get; set; } = "ClaudeCode";

    public string ClaudeCliPath { get; set; } = "claude";

    public string? ClaudeCliModel { get; set; } = "sonnet";

    public string? ClaudeCliEffort { get; set; } = "medium";

    public bool AllowWebResearch { get; set; } = true;

    public bool LaunchAtStartup { get; set; } = true;

    public bool AutoResearchNewTasks { get; set; } = true;

    public double ReplyTimeoutMinutes { get; set; } = 8.0;

    public bool UseGitHub { get; set; } = true;

    public List<string> LocalProjectRoots { get; set; } = new List<string>();

    public string AnthropicModel { get; set; } = "claude-sonnet-5-5";

    public string AnthropicApiKeyEnvVar { get; set; } = "ANTHROPIC_API_KEY";

    public string OpenAIBaseUrl { get; set; } = "http://localhost:11434/v1";

    public string OpenAIModel { get; set; } = "qwen3:8b";

    public string? OpenAIApiKey { get; set; }

    public double ReminderEveryDays { get; set; } = 7.0;

    public double FirstReminderAfterDays { get; set; } = 14.0;

    public double SnoozeHours { get; set; } = 4.0;

    public int CheckIntervalMinutes { get; set; } = 15;

    public int MemoryAlertPercent { get; set; } = 90;

    public double MemoryAlertCooldownMinutes { get; set; } = 60.0;

    public string AboutMe { get; set; } = "Développeur C#/Angular, intéressé par Python, Unity, Rust (en formation), Solidity et la blockchain. Aime les explications vulgarisées avec des analogies imagées quand le sujet est très technique.";

    public static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    public static AppSettings Load()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                AppSettings? obj = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), AppPaths.Json) ?? new AppSettings();
                obj.Save();
                return obj;
            }
            catch
            {
            }
        }
        AppSettings appSettings = new AppSettings();
        appSettings.Save();
        return appSettings;
    }

    public void Save()
    {
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppPaths.Json));
    }
}
