using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record DebugBreakpointInfo(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("condition")] string? Condition);

/// <summary>One LLM-owned debug session, as shown in the editor (roslynSense/debugSessions).</summary>
public sealed record DebugSessionInfo(
    [property: JsonPropertyName("ownerPid")] int OwnerPid,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("function")] string? Function,
    [property: JsonPropertyName("filePath")] string? FilePath,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("updatedAtUtc")] string UpdatedAtUtc,
    [property: JsonPropertyName("breakpoints")] DebugBreakpointInfo[] Breakpoints);

/// <summary>Editor command against an LLM-owned debug session (roslynSense/debugCommand).</summary>
public sealed record DebugCommandParams(
    [property: JsonPropertyName("ownerPid")] int OwnerPid,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("expression")] string? Expression = null,
    [property: JsonPropertyName("file")] string? File = null,
    [property: JsonPropertyName("line")] int Line = 0,
    [property: JsonPropertyName("condition")] string? Condition = null,
    [property: JsonPropertyName("breakpointId")] int BreakpointId = 0,
    [property: JsonPropertyName("hitCondition")] string? HitCondition = null,
    [property: JsonPropertyName("logMessage")] string? LogMessage = null,
    [property: JsonPropertyName("frameId")] int FrameId = 0,
    [property: JsonPropertyName("variablesReference")] int VariablesReference = 0,
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("filters")] string[]? Filters = null,
    [property: JsonPropertyName("dataBreakpoints")] DataBreakpointParams[]? DataBreakpoints = null);

/// <summary>One value watch in a <c>set_data_breakpoints</c> command.</summary>
public sealed record DataBreakpointParams(
    [property: JsonPropertyName("dataId")] string DataId,
    [property: JsonPropertyName("expression")] string Expression,
    [property: JsonPropertyName("accessType")] string AccessType = "write",
    [property: JsonPropertyName("condition")] string? Condition = null,
    [property: JsonPropertyName("hitCondition")] string? HitCondition = null);

public sealed record DebugCommandResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("result")] string Result);

/// <summary>The editor's own debug-session state, reported by the extension
/// (roslynSense/editorDebugState notification).</summary>
public sealed record EditorDebugStateParams(
    [property: JsonPropertyName("solutionPath")] string SolutionPath,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("sessionName")] string? SessionName = null,
    [property: JsonPropertyName("adapterType")] string? AdapterType = null,
    [property: JsonPropertyName("executionState")] string ExecutionState = "running",
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("filePath")] string? FilePath = null,
    [property: JsonPropertyName("line")] int Line = 0);

/// <summary>Full editor breakpoint snapshot for the shared per-solution set
/// (roslynSense/syncBreakpoints notification).</summary>
public sealed record SyncBreakpointsParams(
    [property: JsonPropertyName("solutionPath")] string SolutionPath,
    [property: JsonPropertyName("breakpoints")] SyncBreakpoint[] Breakpoints);

public sealed record SyncBreakpoint(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("condition")] string? Condition = null);

/// <summary>LLM command routed into the editor's debug session
/// (server→client request roslynSense/editorDebugCommand).</summary>
public sealed record EditorDebugCommandParams(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("expression")] string? Expression = null,
    [property: JsonPropertyName("file")] string? File = null,
    [property: JsonPropertyName("line")] int Line = 0,
    [property: JsonPropertyName("condition")] string? Condition = null);
