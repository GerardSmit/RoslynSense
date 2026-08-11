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
/// What a run produced, and why it produced nothing when it did.
/// </summary>
/// <remarks>
/// The results alone cannot distinguish "this test did not run" from "nothing ran, because
/// MSBuild is missing / the build failed / the run timed out". Returning only the results meant a
/// .NET Framework project whose run never started reported every test as skipped, with the actual
/// reason going to the server's stderr where nobody sees it.
/// </remarks>
public sealed record TestRunResponse(
    [property: JsonPropertyName("results")] TestResultInfo[] Results,
    [property: JsonPropertyName("error")] string? Error = null);

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

/// <summary>Which tests execute the code at one position — what the per-method lens counts and
/// what its click lists.</summary>
public sealed record TestsCoveringParams(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character);

public sealed record CoveringTestInfo(
    [property: JsonPropertyName("fullyQualifiedName")] string FullyQualifiedName,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("className")] string ClassName,
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("filePath")] string? FilePath,
    [property: JsonPropertyName("line")] int Line);

/// <param name="Scope">uncommitted | branch | ref</param>
public sealed record ImpactedTestsParams(
    [property: JsonPropertyName("scope")] string Scope = "uncommitted",
    [property: JsonPropertyName("gitRef")] string? GitRef = null,
    /// <summary>Any path inside the repository; the workspace root when the client has one.</summary>
    [property: JsonPropertyName("anchorPath")] string? AnchorPath = null);

public sealed record ImpactedTestInfo(
    [property: JsonPropertyName("fullyQualifiedName")] string FullyQualifiedName,
    [property: JsonPropertyName("className")] string ClassName,
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("because")] string? Because);

public sealed record CoverageSnapshotParams(
    /// <summary>Any path in the solution; the workspace root when the client has one.</summary>
    [property: JsonPropertyName("anchorPath")] string? AnchorPath = null);

/// <summary>
/// The coverage view's data: every measured method, flat. The namespace/class nesting is the
/// client's to build — it is a rendering choice, not a fact about the measurement.
/// </summary>
public sealed record CoverageSnapshotResult(
    [property: JsonPropertyName("collectedAtUtc")] string? CollectedAtUtc,
    [property: JsonPropertyName("methods")] CoverageMethodInfo[] Methods,
    /// <summary>How many tests the per-test map knows about, so the view can say whether the
    /// "N tests" figures behind it exist at all.</summary>
    [property: JsonPropertyName("mappedTests")] int MappedTests);

public sealed record CoverageMethodInfo(
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("className")] string ClassName,
    [property: JsonPropertyName("methodName")] string MethodName,
    [property: JsonPropertyName("filePath")] string FilePath,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("coveredStatements")] int CoveredStatements,
    [property: JsonPropertyName("totalStatements")] int TotalStatements,
    [property: JsonPropertyName("coveredBranches")] int CoveredBranches,
    [property: JsonPropertyName("totalBranches")] int TotalBranches,
    /// <summary>Tests known to execute this method, from the per-test map. Zero when no map has
    /// been built — not the same as "nothing covers it", which is what 0 statements means.</summary>
    [property: JsonPropertyName("tests")] int Tests);

public sealed record BuildCoverageMapParams(
    /// <summary>The test project to map. Null maps every test project in the solution.</summary>
    [property: JsonPropertyName("projectPath")] string? ProjectPath = null,
    [property: JsonPropertyName("force")] bool Force = false);

public sealed record BuildCoverageMapResult(
    [property: JsonPropertyName("classesRun")] int ClassesRun,
    [property: JsonPropertyName("classesReused")] int ClassesReused,
    [property: JsonPropertyName("testsMapped")] int TestsMapped,
    [property: JsonPropertyName("failures")] string[] Failures,
    [property: JsonPropertyName("error")] string? Error);

public sealed record ImpactedTestsResult(
    [property: JsonPropertyName("tests")] ImpactedTestInfo[] Tests,
    [property: JsonPropertyName("changedFiles")] string[] ChangedFiles,
    [property: JsonPropertyName("uncoveredFiles")] string[] UncoveredFiles,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("mapWasEmpty")] bool MapWasEmpty,
    [property: JsonPropertyName("error")] string? Error);

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
