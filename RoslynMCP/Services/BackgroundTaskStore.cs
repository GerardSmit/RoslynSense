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

    /// <summary>Creates a new background task entry and returns its ID.</summary>
    public string CreateTask(TaskKind kind, string description)
    {
        EvictExpired();
        var id = $"bg-{kind.ToString().ToLowerInvariant()}-{DateTime.UtcNow:HHmmss}-{Guid.NewGuid().ToString()[..4]}";
        var task = new BackgroundTask(id, kind, description, DateTime.UtcNow);
        _tasks[id] = task;
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
        var task = Get(taskId);
        if (task is null || task.Status != TaskStatus.Running)
            return task;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
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
}
