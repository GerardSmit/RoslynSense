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
    [property: JsonPropertyName("collectCoverage")] bool CollectCoverage = false);

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

public sealed record LineCoverageInfo(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("hits")] int Hits);
