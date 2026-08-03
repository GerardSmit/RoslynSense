using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record TestProjectInfo(
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("projectName")] string ProjectName);

public sealed record TestDiscoverParams(
    [property: JsonPropertyName("projectPath")] string? ProjectPath = null,
    [property: JsonPropertyName("uri")] string? Uri = null);

public sealed record TestInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("fullyQualifiedName")] string FullyQualifiedName,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("className")] string ClassName,
    [property: JsonPropertyName("namespace")] string? Namespace,
    [property: JsonPropertyName("framework")] string Framework,
    [property: JsonPropertyName("filePath")] string? FilePath,
    [property: JsonPropertyName("startLine")] int StartLine,
    [property: JsonPropertyName("endLine")] int EndLine,
    [property: JsonPropertyName("projectPath")] string ProjectPath);

public sealed record TestRunParams(
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("fullyQualifiedNames")] string[]? FullyQualifiedNames = null,
    [property: JsonPropertyName("collectCoverage")] bool CollectCoverage = false,
    /// <summary>Client-chosen id, used to route progress events back and to cancel the run.</summary>
    [property: JsonPropertyName("runId")] string? RunId = null);

public sealed record TestCancelParams(
    [property: JsonPropertyName("runId")] string RunId);

/// <summary>
/// Server-to-client notification while a run is going: one per finished test, plus the console
/// output as it arrives.
/// </summary>
public sealed record TestRunEvent(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("fullyQualifiedName")] string? FullyQualifiedName,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("durationMs")] double DurationMs);

public sealed record TestResultInfo(
    [property: JsonPropertyName("fullyQualifiedName")] string FullyQualifiedName,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("durationMs")] double DurationMs,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
    [property: JsonPropertyName("stackTrace")] string? StackTrace);

public sealed record TestDebugParams(
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("fullyQualifiedNames")] string[]? FullyQualifiedNames = null);

public sealed record TestDebugResult(
    [property: JsonPropertyName("processId")] int ProcessId,
    [property: JsonPropertyName("error")] string? Error);

public sealed record TestCoverageParams(
    [property: JsonPropertyName("projectPath")] string ProjectPath);

public sealed record FileCoverageInfo(
    [property: JsonPropertyName("filePath")] string FilePath,
    [property: JsonPropertyName("lines")] LineCoverageInfo[] Lines);

/// <summary>
/// One covered line. The branch counts are 0 for a line with no conditions in it, which is how
/// the client tells a plain statement from an <c>if</c> whose else-path never ran.
/// </summary>
public sealed record LineCoverageInfo(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("hits")] int Hits,
    [property: JsonPropertyName("coveredBranches")] int CoveredBranches = 0,
    [property: JsonPropertyName("totalBranches")] int TotalBranches = 0);
