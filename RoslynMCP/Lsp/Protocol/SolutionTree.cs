using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

/// <summary>What kind of thing a tree node is. The client maps these to icons and to the
/// context menu, so the set is part of the protocol rather than a display detail.</summary>
public static class SolutionNodeKind
{
    public const string Solution = "solution";
    public const string SolutionFolder = "solutionFolder";
    public const string SolutionItem = "solutionItem";
    public const string Project = "project";
    public const string Dependencies = "dependencies";
    public const string Imports = "imports";
    public const string Import = "import";
    public const string Framework = "framework";
    public const string Packages = "packages";
    public const string Package = "package";
    public const string Projects = "projects";
    public const string ProjectRef = "projectRef";
    public const string Assemblies = "assemblies";
    public const string Assembly = "assembly";
    public const string Analyzers = "analyzers";
    public const string Analyzer = "analyzer";
    public const string Generator = "generator";
    public const string GeneratedFile = "generatedFile";
    public const string Folder = "folder";
    public const string File = "file";
}

public sealed record SolutionTreeParams(
    [property: JsonPropertyName("nodeId")] string? NodeId = null,
    [property: JsonPropertyName("showAllFiles")] bool ShowAllFiles = false,
    [property: JsonPropertyName("showIgnored")] bool ShowIgnored = false,
    [property: JsonPropertyName("filter")] string? Filter = null);

/// <summary>
/// One node. <see cref="HasChildren"/> drives lazy expansion, which is what keeps a
/// 500-project solution from evaluating every project just to draw its root.
/// </summary>
public sealed record SolutionTreeNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("resourceUri")] string? ResourceUri,
    [property: JsonPropertyName("hasChildren")] bool HasChildren,
    [property: JsonPropertyName("contextValue")] string ContextValue,
    [property: JsonPropertyName("dimmed")] bool Dimmed = false,
    [property: JsonPropertyName("highlights")] int[][]? Highlights = null);

public sealed record SolutionTreeSearchParams(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("limit")] int Limit = 50);

public sealed record SolutionTreeRevealParams(
    [property: JsonPropertyName("uri")] string Uri);

/// <summary>The chain of node ids from the root down to the target, so the client can expand
/// each ancestor before revealing.</summary>
public sealed record SolutionTreeRevealResult(
    [property: JsonPropertyName("path")] string[] Path);
