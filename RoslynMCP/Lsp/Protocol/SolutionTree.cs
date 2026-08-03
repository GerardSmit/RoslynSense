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

    /// <summary>
    /// A project that can actually be started — a console app, a desktop app, or a web app.
    /// Only the context value differs from <see cref="Project"/>; the node kind stays
    /// <c>project</c> so icons and the rest of the menu are unaffected.
    /// </summary>
    public const string RunnableProject = "projectRunnable";
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
    [property: JsonPropertyName("filter")] string? Filter = null,
    [property: JsonPropertyName("fileNesting")] bool FileNesting = true);

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

/// <summary>An edit made from the tree: new file or folder, delete, rename, copy, or a
/// drag-and-drop move.</summary>
public sealed record SolutionTreeEditParams(
    [property: JsonPropertyName("action")] string Action,   // addFile | addFolder | delete | rename | move | copy
    [property: JsonPropertyName("targetUri")] string? TargetUri = null,
    [property: JsonPropertyName("projectPath")] string? ProjectPath = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("destinationUri")] string? DestinationUri = null);

/// <summary>
/// The outcome of a tree edit. <see cref="Edit"/> carries the namespace and type fixups a rename
/// implies; the client applies them rather than the server writing them, so a file open with
/// unsaved changes is edited in the buffer instead of being overwritten on disk.
/// </summary>
public sealed record SolutionTreeEditResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("uri")] string? Uri = null,
    [property: JsonPropertyName("edit")] WorkspaceEdit? Edit = null);
