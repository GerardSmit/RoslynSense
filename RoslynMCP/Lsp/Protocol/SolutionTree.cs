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

    // ---- Discovery ------------------------------------------------------------------------
    //
    // The sections of the Discovery view, every one of them contributed by a language pack rather
    // than built by a handler. Declared here with the tree's own kinds because the client maps a
    // kind to an icon and a context menu, and it does that from one table for both views — so the
    // set is part of the protocol wherever the node is produced.

    /// <summary>The section listing what runs on a schedule.</summary>
    public const string CronJobs = "cronJobs";

    /// <summary>One project inside that section.</summary>
    public const string CronProject = "cronProject";

    /// <summary>One scheduled job.</summary>
    public const string CronJob = "cronJob";

    /// <summary>
    /// The context value of a job row, built from the base name and up to two suffixes:
    /// <see cref="CronJobDynamicSuffix"/> when something about the job is only knowable at run
    /// time, and <see cref="SecondaryTargetSuffix"/> when its own method was named and can be
    /// opened. So a fully static job with a resolved method is <c>cronJobTarget</c>, and a
    /// config-driven one with none is <c>cronJobDynamic</c>.
    /// </summary>
    /// <remarks>
    /// Composed rather than enumerated because the two facts are independent, and because a button
    /// that opens the job's method must not appear on a row that has no method to open — which the
    /// client can only decide from the context value it was given.
    /// </remarks>
    public const string CronJobDynamicSuffix = "Dynamic";

    /// <summary>The section listing what the solution's <c>.proto</c> files declare.</summary>
    public const string ProtoServices = "protoServices";

    /// <summary>One protobuf package, which is the namespace its services are declared in.</summary>
    public const string ProtoPackage = "protoPackage";

    /// <summary>One <c>service</c>.</summary>
    public const string ProtoService = "protoService";

    /// <summary>One <c>rpc</c> inside a service.</summary>
    public const string ProtoRpc = "protoRpc";

    /// <summary>
    /// This row has a second place worth going: the thing it names is implemented, handled or run
    /// somewhere else. Appended to the context value of a job, an endpoint or an rpc alike, which
    /// is what lets one <c>when</c> clause put the Implementation button on all three.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately says nothing about <em>where</em>, because the two ways of answering that are
    /// not alike. A cron job and a route carry their target in
    /// <see cref="SolutionTreeNode.GoToSecondary"/>, resolved when the row was built. An rpc does
    /// not: crossing from a <c>.proto</c> declaration to the C# overriding it runs a solution-wide
    /// symbol search, which is far too expensive to do for every row on expand, and there may be
    /// several answers where <c>GoToSecondary</c> holds one. So that one is resolved by the client
    /// when the button is pressed.
    /// </para>
    /// <para>
    /// The suffix marks both cases the same on purpose. Whether the answer was already known or
    /// has to be gone and fetched is the client's problem, not the menu's — what the menu needs to
    /// know is only whether the row leads anywhere at all.
    /// </para>
    /// </remarks>
    public const string SecondaryTargetSuffix = "Target";

    /// <summary>The section listing the HTTP endpoints the solution serves.</summary>
    public const string Routes = "routes";

    /// <summary>One project inside that section.</summary>
    public const string RouteProject = "routeProject";

    /// <summary>A path prefix more than one endpoint of a project shares.</summary>
    public const string RouteGroup = "routeGroup";

    /// <summary>One endpoint: a method, a path, and somewhere to go.</summary>
    public const string Route = "route";

    /// <summary>
    /// The context value of an endpoint row, the base name plus <c>Dynamic</c> when the path is
    /// only knowable at run time — a template built from a constant the pack could not fold, or a
    /// prefix composed by a route group.
    /// </summary>
    /// <remarks>
    /// Composed the same way <see cref="CronJob"/>'s is, and for the same reason: the menu item
    /// that copies a row's path must not appear on a row whose path is a guess, and the client can
    /// only decide that from the context value it was given.
    /// </remarks>
    public const string RouteDynamicSuffix = "Dynamic";

    /// <summary>The section listing the screens an application declares in template files.</summary>
    public const string Templates = "templates";

    /// <summary>
    /// One application's templates, listed only when a solution holds more than one set of them.
    /// </summary>
    public const string TemplateRoot = "templateRoot";

    /// <summary>One entry of that tree: a screen, or a heading holding screens.</summary>
    public const string TemplateEntry = "templateEntry";

    /// <summary>
    /// One module an entry hosts, listed only when it hosts more than one.
    /// </summary>
    /// <remarks>
    /// An entry hosting a single module carries that module's implementation on its own row
    /// instead, so this kind appears exactly where there is a choice to make.
    /// </remarks>
    public const string TemplateModule = "templateModule";
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
    [property: JsonPropertyName("highlights")] int[][]? Highlights = null,

    /// <summary>
    /// What the hover says, when the row itself cannot hold it.
    /// </summary>
    /// <remarks>
    /// The client shows the resource path when this is null, which is right for a file. A row
    /// standing for something written inside a file wants the line as well — and often something
    /// the row deliberately does not show, like the method serving a route, which is the same name
    /// on every row of a controller and belongs on the hover rather than in the column beside the
    /// path.
    /// </remarks>
    [property: JsonPropertyName("tooltip")] string? Tooltip = null,

    /// <summary>
    /// Where clicking this node should land, when that is somewhere other than the top of
    /// <see cref="ResourceUri"/>.
    /// </summary>
    /// <remarks>
    /// A file node opens its file and that is the whole story. A node standing for something
    /// written <i>inside</i> a file — a job registered by one call among twenty in a startup
    /// method — has to name the line, or clicking it lands at the top of a file and leaves the
    /// reader to find what they clicked on.
    /// <para>
    /// Deliberately narrower than letting a pack name a client command: a range in a document is
    /// a thing the client already knows how to open, and nothing a pack puts here can make the
    /// tree run something.
    /// </para>
    /// </remarks>
    [property: JsonPropertyName("goTo")] SolutionTreeNavigation? GoTo = null,

    /// <summary>
    /// A second place worth going, offered on the context menu rather than on click — the job's
    /// method, where the registration is what the click opens.
    /// </summary>
    [property: JsonPropertyName("goToSecondary")] SolutionTreeNavigation? GoToSecondary = null);

/// <summary>A place in a document a tree node can open.</summary>
public sealed record SolutionTreeNavigation(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("range")] Range Range);

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
