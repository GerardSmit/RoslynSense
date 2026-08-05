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

    /// <summary>
    /// The Dependencies node of a .NET Framework project. Distinguished only so the menu can
    /// offer "Add Assembly Reference" where it means something — on modern .NET the framework
    /// arrives through the SDK and there is nothing to add, and offering an action whose only
    /// outcome is an explanation is worse than not offering it.
    /// </summary>
    public const string DependenciesNetFx = "dependenciesNetFx";
    public const string Imports = "imports";
    public const string Import = "import";
    public const string Framework = "framework";
    public const string Packages = "packages";
    public const string Package = "package";

    /// <summary>Packages restore pulled in that nothing in the project references directly.</summary>
    public const string Transitive = "transitive";
    public const string TransitivePackage = "transitivePackage";
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

    /// <summary>
    /// A project the user has unloaded. Distinguished only so the menu offers Reload where it
    /// offers Unload everywhere else; the node kind stays <c>project</c>.
    /// </summary>
    public const string UnloadedProject = "projectUnloaded";
}

public sealed record SolutionTreeParams(
    [property: JsonPropertyName("nodeId")] string? NodeId = null,
    [property: JsonPropertyName("showAllFiles")] bool ShowAllFiles = false,
    [property: JsonPropertyName("showIgnored")] bool ShowIgnored = false,
    [property: JsonPropertyName("filter")] string? Filter = null,
    [property: JsonPropertyName("fileNesting")] bool FileNesting = true,

    /// <summary>
    /// Projects the client is showing as unloaded. Held by the client because unloading is a
    /// per-window view choice, not a property of the solution — two editors on the same daemon
    /// can disagree about it, and nothing should be written to the solution file.
    /// </summary>
    [property: JsonPropertyName("unloadedProjects")] string[]? UnloadedProjects = null);

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

/// <summary>A project in the solution, for pickers that offer a choice of them.</summary>
public sealed record SolutionProjectInfo(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name);

/// <summary>What a new project can be made from on this machine.</summary>
public sealed record ProjectTemplateChoices(
    [property: JsonPropertyName("templates")] ProjectTemplateChoice[] Templates,
    [property: JsonPropertyName("targetFrameworks")] string[] TargetFrameworks);

public sealed record ProjectTemplateChoice(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("shortName")] string ShortName,
    [property: JsonPropertyName("tags")] string Tags);

public sealed record SolutionTreeSearchParams(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("limit")] int Limit = 50);

public sealed record SolutionTreeRevealParams(
    [property: JsonPropertyName("uri")] string Uri,
    // The chain runs through a file's nesting parent when nesting is on, so it has to be known
    // here as well as when the folder was listed.
    [property: JsonPropertyName("fileNesting")] bool FileNesting = true);

/// <summary>The chain of node ids from the root down to the target, so the client can expand
/// each ancestor before revealing.</summary>
public sealed record SolutionTreeRevealResult(
    [property: JsonPropertyName("path")] string[] Path);

/// <summary>An edit made from the tree: new file or folder, delete, rename, copy, or a
/// drag-and-drop move.</summary>
public sealed record SolutionTreeEditParams(
    // addFile | addFolder | delete | rename | move | copy | includeExistingFile | excludeFile |
    // addSolutionFolder | renameSolutionFolder | removeSolutionFolder |
    // addSolutionItem | removeSolutionItem | moveProject |
    // addProject | addExistingProject | removeProject |
    // addProjectReference | removeProjectReference | addAssemblyReference
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("targetUri")] string? TargetUri = null,
    [property: JsonPropertyName("projectPath")] string? ProjectPath = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("destinationUri")] string? DestinationUri = null,
    [property: JsonPropertyName("targetFramework")] string? TargetFramework = null);

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
