using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record HotReloadParams(
    [property: JsonPropertyName("projectPath")] string ProjectPath);

public sealed record HotReloadDiagnosticDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("filePath")] string FilePath,
    [property: JsonPropertyName("line")] int Line);

/// <summary>The result of one apply, detailed enough for the editor to show what happened without
/// a second round trip.</summary>
public sealed record HotReloadResultDto(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("diagnostics")] HotReloadDiagnosticDto[] Diagnostics,
    [property: JsonPropertyName("appliedTo")] string[] AppliedTo,
    [property: JsonPropertyName("errors")] string[] Errors);

/// <summary>What a hot reload apply would currently reach.</summary>
public sealed record HotReloadStatusDto(
    [property: JsonPropertyName("sessions")] string[] Sessions,
    [property: JsonPropertyName("targets")] HotReloadTargetDto[] Targets);

/// <summary>
/// The environment a process must be started with for hot reload to work in it.
/// </summary>
/// <remarks>
/// Returned rather than applied because the editor owns the launch — a task, an F5 session, or a
/// terminal — and every one of these settings is read only at process start.
/// </remarks>
public sealed record HotReloadEnvironmentDto(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("variables")] Dictionary<string, string> Variables,
    [property: JsonPropertyName("message")] string Message);

public sealed record HotReloadTargetDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("processId")] int ProcessId,
    [property: JsonPropertyName("runtime")] string Runtime);
