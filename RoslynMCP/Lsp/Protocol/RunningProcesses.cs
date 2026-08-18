using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

// Custom extension (roslynSense/runningProcesses, killProcess, registerProcess,
// unregisterProcess): the running applications, in both directions. Chat launches (run_project)
// reach the editor, which shows them in the status bar and can stop or restart them; the
// editor's own launches are announced back, so a chat can see the app the user has running.
// Backed by the cross-process RunningProcessRegistry.

public sealed record RunningProcess(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("startedAtUtc")] string StartedAtUtc);

public sealed record KillProcessParams(
    [property: JsonPropertyName("pid")] int Pid);

public sealed record ProcessOutputParams(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("text")] string Text);

public sealed record RegisterProcessParams(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("url")] string? Url);
