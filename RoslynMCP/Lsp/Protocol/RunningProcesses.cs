using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

// Custom extension (roslynSense/runningProcesses, roslynSense/killProcess): surfaces the
// applications launched by MCP chats (run_project) to the editor, which shows them in the
// status bar and can kill them. Backed by the cross-process RunningProcessRegistry.

public sealed record RunningProcess(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("startedAtUtc")] string StartedAtUtc);

public sealed record KillProcessParams(
    [property: JsonPropertyName("pid")] int Pid);
