using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Tools for checking the status and results of background tasks
/// started via RunTests, BuildProject, or RunCoverage with <c>background=true</c>.
/// </summary>
[McpServerToolType]
public static class BackgroundTaskTool
{
    [McpServerTool, Description(
        "Check the status and results of a background task. " +
        "Returns status (running/completed/failed/cancelled) and the result if finished.")]
    public static async Task<string> GetBackgroundTaskResult(
        [Description("Task ID returned when starting a task with background=true.")]
        string taskId,
        [Description("Seconds to wait for the task to complete before returning. Default (0) waits up to 5 seconds. Pass -1 to return immediately without waiting.")]
        int waitTimeoutSeconds,
        IOutputFormatter fmt,
        BackgroundTaskStore taskStore,
        CancellationToken cancellationToken)
    {
        int effectiveTimeout = waitTimeoutSeconds < 0 ? 0 : waitTimeoutSeconds == 0 ? 5 : waitTimeoutSeconds;
        var task = effectiveTimeout > 0
            ? await taskStore.WaitForCompletionAsync(taskId, TimeSpan.FromSeconds(effectiveTimeout), cancellationToken).ConfigureAwait(false)
            : taskStore.Get(taskId);
        if (task is null)
            return $"Error: Task '{taskId}' not found. Use ListBackgroundTasks to see available tasks.";

        var sb = new StringBuilder();
        fmt.AppendHeader(sb, $"Background Task: {task.Kind}");
        fmt.AppendField(sb, "Task ID", task.Id);
        fmt.AppendField(sb, "Description", task.Description);
        fmt.AppendField(sb, "Status", task.Status.ToString());
        fmt.AppendField(sb, "Started", task.StartedAt.ToLocalTime().ToString("HH:mm:ss"));

        if (task.Status == BackgroundTaskStore.TaskStatus.Running)
        {
            var elapsed = DateTime.UtcNow - task.StartedAt;
            fmt.AppendField(sb, "Elapsed", $"{elapsed.TotalSeconds:F0}s");
            fmt.AppendSeparator(sb);
            fmt.AppendHints(sb, "Task is still running. Check again later.");
        }
        else
        {
            fmt.AppendField(sb, "Completed", task.CompletedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "");
            var duration = task.CompletedAt.HasValue ? (task.CompletedAt.Value - task.StartedAt) : TimeSpan.Zero;
            fmt.AppendField(sb, "Duration", $"{duration.TotalSeconds:F1}s");
            if (task.ExitCode.HasValue)
                fmt.AppendField(sb, "Exit Code", task.ExitCode.Value);
            fmt.AppendSeparator(sb);

            if (!string.IsNullOrWhiteSpace(task.Result))
                sb.AppendLine(task.Result);
        }

        return sb.ToString();
    }

    [McpServerTool, Description(
        "List all background tasks (running, completed, and failed). " +
        "Shows task IDs that can be used with GetBackgroundTaskResult.")]
    public static string ListBackgroundTasks(
        IOutputFormatter fmt,
        BackgroundTaskStore taskStore)
    {
        var tasks = taskStore.ListTasks();
        var sb = new StringBuilder();
        fmt.AppendHeader(sb, "Background Tasks");

        if (tasks.Count == 0)
        {
            fmt.AppendEmpty(sb, "No background tasks. Use RunTests, BuildProject, or RunCoverage with background=true to start one.");
            return sb.ToString();
        }

        var columns = new[] { "Task ID", "Kind", "Status", "Description", "Started", "Duration" };
        var rows = tasks.Select(t =>
        {
            var duration = (t.CompletedAt ?? DateTime.UtcNow) - t.StartedAt;
            return new[]
            {
                t.Id,
                t.Kind.ToString(),
                t.Status.ToString(),
                t.Description,
                t.StartedAt.ToLocalTime().ToString("HH:mm:ss"),
                $"{duration.TotalSeconds:F0}s"
            };
        }).ToList();

        fmt.AppendTable(sb, "Tasks", columns, rows, tasks.Count);
        return sb.ToString();
    }
}
