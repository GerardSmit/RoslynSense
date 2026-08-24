using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Language.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Proto.Core;

/// <summary>
/// One <c>.proto</c> together with everything the workspace knows about it: the parse, the
/// project or projects that compile it, and the bindings from its declarations to the C# protoc
/// generated.
/// </summary>
/// <param name="Projects">
/// Every project that compiles this file, nearest first. Plural because it routinely is: the
/// server and the client of a gRPC contract are separate assemblies that both
/// <c>&lt;Protobuf Include&gt;</c> the same <c>.proto</c>, and each gets its own copy of the
/// generated code. A find-usages that searched only one of them would miss every call site on
/// the other side of the wire.
/// </param>
/// <param name="Index">
/// The bindings from <see cref="Projects"/>'s first entry. One index rather than one per project
/// because the proto declarations are the same in all of them and the symbols they bind to are
/// duplicates of each other; the solution-wide reference search reaches the other copies through
/// their own project anyway.
/// </param>
internal sealed record ProtoProjectView(
    ProtoDocument Document,
    ImmutableArray<Project> Projects,
    ProtoGeneratedIndex Index)
{
    public ProtoFile Parse => Document.Parse;

    public string FilePath => Document.FilePath;

    public SourceText Text => Document.Text;

    /// <summary>The project every symbol in <see cref="Index"/> belongs to, and the one a
    /// solution-wide search is anchored on.</summary>
    public Project? Project => Projects.Length > 0 ? Projects[0] : Document.Project;

    /// <summary>The proto root Grpc.Tools gives files inside the project, which is what an
    /// <c>import</c> in this file resolves against.</summary>
    public string? ProjectDirectory =>
        Project?.FilePath is { } path ? Path.GetDirectoryName(path) : null;

    /// <summary>
    /// Builds the name-resolution scope for this file.
    /// </summary>
    /// <remarks>
    /// Not <see cref="ProtoDocument.CreateScope"/>: that one roots imports at the nearest
    /// <c>.csproj</c> on disk, while this one roots them at the project that was proved to compile
    /// the file. The two differ exactly when the <c>.proto</c> is linked in from outside the
    /// project directory, which is the case where getting the root wrong makes every import in the
    /// file unresolvable.
    /// </remarks>
    public ProtoScope CreateScope() => ProtoScope.Create(Parse, ProjectDirectory);
}

/// <summary>
/// Which <c>.proto</c> imports which, across one project.
/// </summary>
/// <remarks>
/// Both directions are kept. Forward is what a scope needs; backward is what any change to a
/// declaration needs, because renaming a message affects every file that can name it and those
/// files are exactly the ones reachable by walking imports in reverse.
/// </remarks>
internal sealed class ProtoImportGraph
{
    private readonly Dictionary<string, ImmutableArray<string>> _imports;
    private readonly Dictionary<string, ImmutableArray<string>> _importers;

    internal ProtoImportGraph(
        ImmutableArray<string> files,
        Dictionary<string, ImmutableArray<string>> imports,
        Dictionary<string, ImmutableArray<string>> importers)
    {
        Files = files;
        _imports = imports;
        _importers = importers;
    }

    /// <summary>Every file in the graph, absolute and normalised.</summary>
    public ImmutableArray<string> Files { get; }

    /// <summary>The files one file imports directly, resolved to absolute paths. An import whose
    /// target could not be found on disk is not in here — there is no path to give.</summary>
    public ImmutableArray<string> ImportsOf(string protoPath) =>
        _imports.TryGetValue(ProtoDocumentService.Normalize(protoPath), out var found) ? found : [];

    /// <summary>The files that import one file directly.</summary>
    public ImmutableArray<string> ImportersOf(string protoPath) =>
        _importers.TryGetValue(ProtoDocumentService.Normalize(protoPath), out var found) ? found : [];

    /// <summary>
    /// Every file that reaches one file through any chain of imports, itself excluded.
    /// </summary>
    /// <remarks>
    /// Transitive without regard for <c>import public</c>, which is wider than what protobuf lets a
    /// file <i>name</i>. That is the right width for the question it answers: a file that imports
    /// the importer is affected by a change here even when it cannot spell the declaration itself,
    /// because its own imports stop resolving when this one breaks.
    /// </remarks>
    public ImmutableArray<string> DependentsOf(string protoPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();
        pending.Enqueue(ProtoDocumentService.Normalize(protoPath));

        var results = ImmutableArray.CreateBuilder<string>();

        while (pending.Count > 0)
        {
            foreach (string importer in ImportersOf(pending.Dequeue()))
            {
                if (!seen.Add(importer))
                    continue;

                results.Add(importer);
                pending.Enqueue(importer);
            }
        }

        return results.ToImmutable();
    }
}

/// <summary>
/// Which <c>.proto</c> files belong to which project, and how they import each other.
/// </summary>
/// <remarks>
/// <para>
/// Ownership is asked of the build output first and of MSBuild second. Grpc.Tools stamps every
/// file it generates with a <c>// source:</c> header naming the <c>.proto</c> it came from, so a
/// project that has been built states its own proto set exactly — including files linked in from
/// outside its directory, and including the same file being compiled by two projects. That answer
/// costs nothing beyond the scan <see cref="ProtoGeneratedIndex"/> already runs.
/// </para>
/// <para>
/// The fallback exists for the one case the headers cannot cover: a project that has never been
/// built, where there is no generated code to read. Then the <c>.csproj</c> is read for its
/// <c>Protobuf</c> items, which is the only other place the association is written down. Reading
/// the XML rather than asking MSBuild is deliberate — the workspace does not carry non-Compile
/// items, and evaluating a project to recover them would cost more than every feature this
/// answers is worth.
/// </para>
/// </remarks>
internal static class ProtoWorkspace
{
    /// <summary>
    /// The full view of a <c>.proto</c>: parse, owners and bindings, or <c>null</c> when the path
    /// is not a <c>.proto</c> or cannot be read.
    /// </summary>
    /// <remarks>
    /// Never <c>null</c> merely because no project claims the file — the parse alone drives the
    /// outline, folding, diagnostics and name resolution, and a <c>.proto</c> opened outside a
    /// solution should still get all of them. In that case <see cref="ProtoProjectView.Projects"/>
    /// is empty and <see cref="ProtoProjectView.Index"/> is <see cref="ProtoGeneratedIndex.Empty"/>,
    /// which makes every symbol lookup answer <c>null</c> rather than throw.
    /// </remarks>
    public static async Task<ProtoProjectView?> GetAsync(string filePath, CancellationToken ct)
    {
        if (await ProtoDocumentService.GetAsync(filePath, ct) is not { } document)
            return null;

        var (projects, index) = await ResolveOwnersAsync(document.FilePath, document.Project, ct);
        return new ProtoProjectView(document, projects, index);
    }

    /// <summary>
    /// Every project that compiles <paramref name="protoPath"/>, nearest first.
    /// </summary>
    /// <param name="seed">The project the path sits under, when one is already known. It is tried
    /// first and breaks ties, so navigation from a file inside a project stays in that project.</param>
    public static async Task<ImmutableArray<Project>> ProjectsForAsync(
        string protoPath, Project? seed, CancellationToken ct)
    {
        var (projects, _) = await ResolveOwnersAsync(ProtoDocumentService.Normalize(protoPath), seed, ct);
        return projects;
    }

    /// <summary>
    /// Every <c>.proto</c> the project compiles, absolute and normalised.
    /// </summary>
    /// <remarks>
    /// The generated headers first, because they are protoc's own record of what it was asked to
    /// build and the only source that covers a file linked in from outside the project directory.
    /// The <c>Protobuf</c> items answer for a project that has never been built, which is when a
    /// user is most likely to be looking at the <c>.proto</c> in the first place. Only when neither
    /// says anything does this fall back to what was found lying under the project — a set that
    /// includes protos nothing compiles, and so is the last answer rather than the first.
    /// </remarks>
    public static async Task<ImmutableArray<string>> ProtoFilesAsync(Project project, CancellationToken ct)
    {
        var index = await ProtoGeneratedIndex.GetAsync(project, ct);

        if (!index.CompiledProtoFiles.IsDefaultOrEmpty)
            return index.CompiledProtoFiles;

        var declared = DeclaredProtoFiles(project.FilePath);
        return declared.IsDefaultOrEmpty ? index.ProtoFiles : declared;
    }

    /// <summary>
    /// Whether the project compiles this <c>.proto</c> at all.
    /// </summary>
    /// <remarks>
    /// The <c>.csproj</c> is still consulted when the generated output does not mention the file,
    /// not only when there is no output at all: a <c>.proto</c> added since the last build is
    /// declared but not yet generated, and that is exactly the moment someone is looking at it.
    /// </remarks>
    public static async Task<bool> CompilesAsync(Project project, string protoPath, CancellationToken ct)
    {
        string path = ProtoDocumentService.Normalize(protoPath);

        var index = await ProtoGeneratedIndex.GetAsync(project, ct);
        return !index.DocumentsFor(path).IsDefaultOrEmpty || Declares(project.FilePath, path);
    }

    // ---- Ownership --------------------------------------------------------------------------

    private static async Task<(ImmutableArray<Project> Projects, ProtoGeneratedIndex Index)>
        ResolveOwnersAsync(string protoPath, Project? seed, CancellationToken ct)
    {
        var solution = seed?.Solution ?? WorkspaceService.TryGetMostRecentSolution();

        if (solution is null)
        {
            return seed is null
                ? ([], ProtoGeneratedIndex.Empty)
                : ([seed], await ProtoGeneratedIndex.GetAsync(seed, ct));
        }

        var generated = ImmutableArray.CreateBuilder<Project>();
        ProtoGeneratedIndex? primary = null;

        foreach (var project in Candidates(solution, seed))
        {
            ct.ThrowIfCancellationRequested();

            // Ahead of the index, because building one walks the project's directory tree.
            if (!ReferencesProtobuf(project))
                continue;

            var index = await ProtoGeneratedIndex.GetAsync(project, ct);
            if (index.DocumentsFor(protoPath).IsDefaultOrEmpty)
                continue;

            generated.Add(project);
            primary ??= index;
        }

        if (primary is not null)
            return (generated.ToImmutable(), primary);

        var declaring = ImmutableArray.CreateBuilder<Project>();
        foreach (var project in Candidates(solution, seed))
        {
            if (Declares(project.FilePath, protoPath))
                declaring.Add(project);
        }

        // The nearest .csproj on disk, as a last resort. It is what NonCSharpProjectFinder already
        // settled on, and for a never-built project whose item list is a glob this parser did not
        // expand it is the only remaining answer that is right more often than not.
        if (declaring.Count == 0 && seed is not null)
            declaring.Add(seed);

        return declaring.Count == 0
            ? ([], ProtoGeneratedIndex.Empty)
            : (declaring.ToImmutable(), await ProtoGeneratedIndex.GetAsync(declaring[0], ct));
    }

    /// <summary>The C# projects worth asking, <paramref name="seed"/> first so the project the
    /// file sits in wins every tie.</summary>
    private static IEnumerable<Project> Candidates(Solution solution, Project? seed)
    {
        if (seed is not null && seed.Language == LanguageNames.CSharp)
            yield return seed;

        foreach (var project in solution.Projects)
        {
            if (project.Language == LanguageNames.CSharp && project.Id != seed?.Id)
                yield return project;
        }
    }

    /// <summary>
    /// Whether the project could hold generated protobuf code at all — an in-memory list scan, no
    /// I/O.
    /// </summary>
    /// <remarks>
    /// Generated code does not compile without the runtime, so a project without it generated
    /// nothing. It is a coarse gate and only a coarse one: <c>Google.Protobuf.dll</c> flows
    /// transitively, so the web project that merely <i>consumes</i> the contracts assembly passes
    /// it too. What excludes that project is owning no <c>.proto</c> of its own, which
    /// <see cref="ProtoGeneratedIndex.GetAsync"/> establishes before it touches a compilation. This
    /// gate's job is only to keep the projects with nothing to do with protobuf from paying for a
    /// directory walk.
    /// </remarks>
    private static bool ReferencesProtobuf(Project project) =>
        project.MetadataReferences.Any(reference =>
            reference.Display is { } display
            && display.EndsWith("Google.Protobuf.dll", StringComparison.OrdinalIgnoreCase));

    // ---- The .csproj side -------------------------------------------------------------------

    private sealed record ItemsEntry(DateTime WriteTimeUtc, ImmutableArray<string> Files);

    private static readonly ConcurrentDictionary<string, ItemsEntry> s_items =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the project file lists this <c>.proto</c> as a <c>Protobuf</c> item.</summary>
    private static bool Declares(string? projectPath, string protoPath) =>
        DeclaredProtoFiles(projectPath).Contains(protoPath, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>.proto</c> files a project file names, with globs expanded against disk.
    /// </summary>
    /// <remarks>
    /// Re-read only when the project file's timestamp moves. Every navigation in a never-built
    /// solution comes through here once per project, and the answer only changes when someone
    /// edits the <c>.csproj</c>. <see cref="ProtoGeneratedIndex"/> reads it too, for the one thing
    /// it states that nothing else does: a <c>.proto</c> linked in from outside the project
    /// directory, which no walk of that directory will ever find.
    /// </remarks>
    internal static ImmutableArray<string> DeclaredProtoFiles(string? projectPath)
    {
        if (projectPath is not { Length: > 0 })
            return [];

        DateTime writeTime;
        try
        {
            var info = new FileInfo(projectPath);
            if (!info.Exists)
                return [];

            writeTime = info.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        if (s_items.TryGetValue(projectPath, out var cached) && cached.WriteTimeUtc == writeTime)
            return cached.Files;

        var files = ReadProtobufItems(projectPath);
        s_items[projectPath] = new ItemsEntry(writeTime, files);
        return files;
    }

    /// <summary>
    /// Reads <c>&lt;Protobuf&gt;</c> items out of a project file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Element names are matched without their namespace, because a legacy project puts everything
    /// under the MSBuild namespace and an SDK project puts nothing under it — the same item written
    /// the same way would otherwise be found in one and missed in the other.
    /// </para>
    /// <para>
    /// <c>Link</c> and <c>ProtoRoot</c> are read but do not name a file: <c>Link</c> is where the
    /// file appears in the tree and <c>ProtoRoot</c> is what <c>import</c> paths are resolved
    /// against. Only <c>Include</c> and <c>Update</c> point at anything on disk, and
    /// <c>Remove</c> takes files back out.
    /// </para>
    /// </remarks>
    private static ImmutableArray<string> ReadProtobufItems(string projectPath)
    {
        XmlDocumentSyntax document;
        try
        {
            document = Parser.ParseText(File.ReadAllText(projectPath));
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        string directory = Path.GetDirectoryName(projectPath) ?? string.Empty;
        if (directory.Length == 0)
            return [];

        var included = new List<string>();
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in document.DescendantsByLocalName(
            "Protobuf", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string attribute in (ReadOnlySpan<string>)["Include", "Update"])
            {
                if (element.GetAttributeValue(attribute) is { Length: > 0 } value)
                    Expand(directory, value, included);
            }

            foreach (string attribute in (ReadOnlySpan<string>)["Remove", "Exclude"])
            {
                if (element.GetAttributeValue(attribute) is { Length: > 0 } value)
                {
                    var taken = new List<string>();
                    Expand(directory, value, taken);
                    removed.UnionWith(taken);
                }
            }
        }

        return
        [
            .. included
                .Where(path => !removed.Contains(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// Turns one item spec — which may be a semicolon-separated list, and may contain wildcards —
    /// into absolute paths.
    /// </summary>
    private static void Expand(string directory, string spec, List<string> results)
    {
        foreach (string part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // An unexpanded property means the real value lives somewhere this parser cannot see;
            // guessing a path from the literal text would name a file that does not exist.
            if (part.Contains("$(", StringComparison.Ordinal))
                continue;

            string relative = part.Replace('/', Path.DirectorySeparatorChar);

            if (relative.IndexOfAny(['*', '?']) < 0)
            {
                if (TryFullPath(directory, relative) is { } full)
                    results.Add(full);
                continue;
            }

            results.AddRange(Glob(directory, relative));
        }
    }

    private static string? TryFullPath(string directory, string relative)
    {
        try
        {
            return ProtoDocumentService.Normalize(Path.Combine(directory, relative));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every existing file matching an MSBuild wildcard spec.
    /// </summary>
    /// <remarks>
    /// Matched against a listing of the <c>.proto</c> files that are actually there rather than
    /// walked pattern-first, because MSBuild's <c>**</c> spans any number of directories and the
    /// set being filtered is small — a project has a handful of protos, not a tree of them.
    /// </remarks>
    private static IEnumerable<string> Glob(string directory, string spec)
    {
        var pattern = GlobRegex(spec);

        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(directory, "*.proto", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string file in candidates)
        {
            string relative = Path.GetRelativePath(directory, file);
            if (pattern.IsMatch(relative))
                yield return ProtoDocumentService.Normalize(file);
        }
    }

    private static readonly ConcurrentDictionary<string, Regex> s_globs = new(StringComparer.OrdinalIgnoreCase);

    private static Regex GlobRegex(string spec) => s_globs.GetOrAdd(spec, static value =>
    {
        var builder = new StringBuilder("^");

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];

            if (c == '*' && i + 1 < value.Length && value[i + 1] == '*')
            {
                builder.Append(".*");

                // `**/` and `**\` both mean "this directory and any below it", so the separator
                // that follows has to be optional or `**/x.proto` would never match `x.proto`.
                i++;
                if (i + 1 < value.Length && (value[i + 1] == '/' || value[i + 1] == '\\'))
                    i++;

                continue;
            }

            builder.Append(c switch
            {
                '*' => @"[^/\\]*",
                '?' => @"[^/\\]",
                '/' or '\\' => @"[/\\]",
                _ => Regex.Escape(c.ToString()),
            });
        }

        return new Regex(builder.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    });

    // ---- The import graph -------------------------------------------------------------------

    /// <summary>Builds the import graph over every <c>.proto</c> the project compiles.</summary>
    public static async Task<ProtoImportGraph> ImportGraphAsync(Project project, CancellationToken ct)
    {
        var files = await ProtoFilesAsync(project, ct);
        string? projectDirectory = project.FilePath is { } path ? Path.GetDirectoryName(path) : null;

        var imports = new Dictionary<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);
        var importers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (ProtoDocumentService.GetParse(file) is not { } parse)
                continue;

            var resolved = ImmutableArray.CreateBuilder<string>();

            foreach (var import in parse.Imports)
            {
                if (ProtoImportResolver.Resolve(import.Path, file, projectDirectory) is not { } target)
                    continue;

                string normalized = ProtoDocumentService.Normalize(target);
                resolved.Add(normalized);

                if (!importers.TryGetValue(normalized, out var list))
                    importers[normalized] = list = [];

                list.Add(file);
            }

            imports[file] = resolved.ToImmutable();
        }

        return new ProtoImportGraph(
            files,
            imports,
            importers.ToDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray(), StringComparer.OrdinalIgnoreCase));
    }
}
