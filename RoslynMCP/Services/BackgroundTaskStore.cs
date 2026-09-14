using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace RoslynMCP.Services;

/// <summary>
/// Manages background tasks (tests, builds, coverage) that run asynchronously.
/// The LLM can start a task, continue other work, and check results later.
/// </summary>
public sealed class BackgroundTaskStore
{
    public enum TaskKind { Tests, Coverage, Build, Profile }
    public enum TaskStatus { Running, Completed, Failed, Cancelled }

    public record BackgroundTask(
        string Id,
        TaskKind Kind,
        string Description,
        DateTime StartedAt)
    {
        public TaskStatus Status { get; set; } = TaskStatus.Running;
        public DateTime? CompletedAt { get; set; }
        public string? Result { get; set; }
        public int? ExitCode { get; set; }
    }

    private static readonly TimeSpan TaskTtl = TimeSpan.FromMinutes(60);
    private readonly ConcurrentDictionary<string, BackgroundTask> _tasks = new();
    private long _collisionCounter;

    /// <summary>Creates a new background task entry and returns its ID.</summary>
    public string CreateTask(TaskKind kind, string description)
    {
        EvictExpired();
        var slug = GenerateWordSlug();
        var id = $"bg-{kind.ToString().ToLowerInvariant()}-{slug}";

        // Reserve atomically: concurrent callers must never overwrite another task. A suffix
        // also lets a busy host retain more tasks than the 1024 available word combinations.
        while (!_tasks.TryAdd(id, new BackgroundTask(id, kind, description, DateTime.UtcNow)))
        {
            id = $"bg-{kind.ToString().ToLowerInvariant()}-{slug}-{Interlocked.Increment(ref _collisionCounter)}";
        }
        return id;
    }

    /// <summary>Marks a task as completed with its result.</summary>
    public void Complete(string taskId, string result, int exitCode)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.Status = exitCode == 0 ? TaskStatus.Completed : TaskStatus.Failed;
            task.CompletedAt = DateTime.UtcNow;
            task.Result = result;
            task.ExitCode = exitCode;
        }
    }

    /// <summary>Marks a task as cancelled.</summary>
    public void Cancel(string taskId, string? message = null)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.Status = TaskStatus.Cancelled;
            task.CompletedAt = DateTime.UtcNow;
            task.Result = message ?? "Task was cancelled.";
        }
    }

    /// <summary>Gets a task by ID.</summary>
    public BackgroundTask? Get(string taskId) => _tasks.GetValueOrDefault(taskId);

    /// <summary>Waits until the task completes or the timeout expires, then returns the task.</summary>
    public async Task<BackgroundTask?> WaitForCompletionAsync(string taskId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = Get(taskId);
        if (task is null || task.Status != TaskStatus.Running)
            return task;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            task = Get(taskId);
            if (task is null || task.Status != TaskStatus.Running)
                break;
        }
        return task;
    }

    /// <summary>Lists all tasks, optionally filtered by status.</summary>
    public IReadOnlyList<BackgroundTask> ListTasks(TaskStatus? statusFilter = null)
    {
        EvictExpired();
        var query = _tasks.Values.AsEnumerable();
        if (statusFilter.HasValue)
            query = query.Where(t => t.Status == statusFilter.Value);
        return query.OrderByDescending(t => t.StartedAt).ToList();
    }

    private void EvictExpired()
    {
        var cutoff = DateTime.UtcNow - TaskTtl;
        foreach (var key in _tasks.Keys)
        {
            if (_tasks.TryGetValue(key, out var task) &&
                task.Status != TaskStatus.Running &&
                task.CompletedAt.HasValue &&
                task.CompletedAt.Value < cutoff)
            {
                _tasks.TryRemove(key, out _);
            }
        }
    }

    private static string GenerateWordSlug()
    {
        var adj = Adjectives[Random.Shared.Next(Adjectives.Length)];
        var noun = Nouns[Random.Shared.Next(Nouns.Length)];
        return $"{adj}-{noun}";
    }

    // 32 adjectives × 32 nouns = 1024 combinations — plenty for background tasks
    private static readonly string[] Adjectives =
    [
        "bold", "calm", "cool", "dark", "fast", "fond", "glad", "gold",
        "keen", "kind", "late", "lean", "live", "mild", "neat", "nice",
        "pale", "pure", "rare", "rich", "safe", "slim", "soft", "tall",
        "tidy", "trim", "true", "vast", "warm", "wide", "wild", "wise"
    ];

    private static readonly string[] Nouns =
    [
        "arch", "bark", "beam", "bell", "bird", "bolt", "claw", "coin",
        "crow", "dawn", "deer", "dove", "drum", "dusk", "fawn", "fern",
        "flag", "frog", "gate", "gull", "hare", "hawk", "hill", "iris",
        "jade", "kite", "lake", "leaf", "lynx", "mist", "moth", "plum"
    ];
}
