using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Languages.Proto;

/// <summary>
/// The <b>Proto</b> section of the Discovery view: every service the solution's schemas declare,
/// grouped by the package that declares it.
/// </summary>
/// <remarks>
/// <para>
/// Grouped by package rather than by project, which is the one decision in here worth arguing
/// about. A package is what a <c>.proto</c> declares itself to be in, and it is the name a client
/// in any language calls — <c>orders.v1.OrderService/GetOrder</c> is the wire path whoever is on
/// the other end wrote down. Which project happens to compile the schema is a build fact, and a
/// contracts file compiled by three of them would otherwise be listed three times.
/// </para>
/// <para>
/// Parse-only, start to finish. Nothing here loads a project, asks for a compilation or looks at
/// generated code, so the section is complete on a solution that has never been built — which is
/// the state a contracts repository is in when somebody clones it, and the state in which "what
/// does this service expose" is the first question asked. The C# side of the same rpc is reached
/// from the row's Implementation button, and that one does need a build; see
/// <see cref="SolutionNodeKind.SecondaryTargetSuffix"/> for why the two are resolved differently.
/// </para>
/// </remarks>
internal sealed partial class ProtoLanguage :
    ILanguageDiscoveryContributor, ILanguageDiscoveryImplementationResolver
{
    /// <summary>The section, and the prefix of everything under it.</summary>
    private const string Prefix = "proto:";

    /// <summary>One package.</summary>
    private const string PackagePrefix = Prefix + "pkg|";

    /// <summary>One service.</summary>
    private const string ServicePrefix = Prefix + "svc|";

    /// <summary>One rpc.</summary>
    private const string RpcPrefix = Prefix + "rpc|";

    /// <summary>
    /// What a file that declares no package is filed under.
    /// </summary>
    /// <remarks>
    /// Legal, and protoc puts such a file's declarations at the root — so the row cannot be
    /// omitted, and cannot be given a made-up name either. Marked rather than left blank, because
    /// a row with an empty label is indistinguishable from a rendering bug.
    /// </remarks>
    private const string NoPackage = "⟨no package⟩";

    public string NodeIdPrefix => Prefix;

    public Task<SolutionTreeNode?> SectionAsync(string solutionPath, CancellationToken ct)
    {
        // The manifests alone, which is the same gate the scheduled-jobs section applies: this
        // runs every time the view is drawn, and DeclaredProtoFiles is a cached read of the
        // project file.
        if (!AnySchemas(ct))
            return Task.FromResult<SolutionTreeNode?>(null);

        return Task.FromResult<SolutionTreeNode?>(new SolutionTreeNode(
            Id: Prefix + solutionPath,
            Kind: SolutionNodeKind.ProtoServices,
            Label: "Proto",
            Description: null,
            ResourceUri: null,
            HasChildren: true,
            ContextValue: SolutionNodeKind.ProtoServices));
    }

    public Task<SolutionTreeNode[]> ChildrenAsync(
        string nodeId, SolutionTreeParams p, CancellationToken ct)
    {
        // An rpc is a leaf, so a request for its children is a client that has lost its place
        // rather than a question. Falling through would answer with the package list, which the
        // tree would then draw underneath the rpc.
        if (nodeId.StartsWith(RpcPrefix, StringComparison.Ordinal))
            return Task.FromResult<SolutionTreeNode[]>([]);

        if (nodeId.StartsWith(ServicePrefix, StringComparison.Ordinal))
            return Task.FromResult(Rpcs(nodeId[ServicePrefix.Length..], ct));

        if (nodeId.StartsWith(PackagePrefix, StringComparison.Ordinal))
            return Task.FromResult(Services(nodeId[PackagePrefix.Length..], ct));

        return Task.FromResult(Packages(ct));
    }

    /// <summary>
    /// The deferred half of a row: what honours this rpc, asked for when the button is pressed.
    /// </summary>
    /// <remarks>
    /// Delegated rather than implemented here, because it is the same question
    /// <c>textDocument/implementation</c> already answers for a caret in the schema — only the
    /// empty case differs, and that difference is about what a button owes the person who pressed
    /// it rather than about proto.
    /// </remarks>
    public Task<DiscoveryImplementationsResult> DiscoveryImplementationsAsync(
        TextDocumentPositionParams p, CancellationToken ct) =>
        Lsp.ProtoNavigationHandler.DiscoveryImplementationsAsync(p, ct);

    /// <summary>Whether any project in the solution declares a <c>.proto</c> at all.</summary>
    private static bool AnySchemas(CancellationToken ct)
    {
        foreach (var project in SolutionProjectIndex.Projects())
        {
            ct.ThrowIfCancellationRequested();

            if (ProtoWorkspace.DeclaredProtoFiles(project.Path).Length > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Every <c>.proto</c> the solution compiles, each one once.
    /// </summary>
    /// <remarks>
    /// De-duplicated because a shared contracts file is normally compiled by more than one project
    /// — the service and its clients — and it declares the same services however many times it is
    /// listed. Read from the project files rather than by walking directories, which is what finds
    /// a schema linked in from outside the project that compiles it.
    /// </remarks>
    private static IEnumerable<ProtoFile> Schemas(CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in SolutionProjectIndex.Projects())
        {
            ct.ThrowIfCancellationRequested();

            foreach (string path in ProtoWorkspace.DeclaredProtoFiles(project.Path))
            {
                if (!seen.Add(ProtoDocumentService.Normalize(path)))
                    continue;

                // Null for a file the project names but disk does not have, which is ordinary
                // during a rename or on a branch that has not been restored.
                if (ProtoDocumentService.GetParse(path) is { } parse)
                    yield return parse;
            }
        }
    }

    /// <summary>The packages that declare a service, one row each.</summary>
    /// <remarks>
    /// Only those that declare one. A <c>.proto</c> holding nothing but messages is a real and
    /// common thing — the shared types every other schema imports — and a package row that expands
    /// to nothing says less than no row at all.
    /// </remarks>
    private static SolutionTreeNode[] Packages(CancellationToken ct)
    {
        // Ordinal, because a proto package name is case-sensitive: `widgets` and `Widgets` are
        // two packages, and folding them into one row would put a service under a heading it is
        // not in. File paths a line above are the opposite case and stay insensitive.
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var schema in Schemas(ct))
        {
            if (schema.Services.Length == 0)
                continue;

            string package = PackageOf(schema);
            counts[package] = counts.GetValueOrDefault(package) + schema.Services.Length;
        }

        return
        [
            .. counts.Select(entry => new SolutionTreeNode(
                Id: PackagePrefix + entry.Key,
                Kind: SolutionNodeKind.ProtoPackage,
                Label: entry.Key,
                Description: entry.Value == 1 ? "1 service" : $"{entry.Value} services",
                ResourceUri: null,
                HasChildren: true,
                ContextValue: SolutionNodeKind.ProtoPackage)),
        ];
    }

    /// <summary>The services one package declares, across every file that declares into it.</summary>
    private static SolutionTreeNode[] Services(string package, CancellationToken ct)
    {
        var rows = new List<(string Name, SolutionTreeNode Node)>();

        foreach (var schema in Schemas(ct))
        {
            if (!string.Equals(PackageOf(schema), package, StringComparison.Ordinal))
                continue;

            foreach (var service in schema.Services)
            {
                rows.Add((service.Name.Value, new SolutionTreeNode(
                    Id: ServicePrefix + schema.FilePath + "|" + service.FullName,
                    Kind: SolutionNodeKind.ProtoService,
                    Label: service.Name.Value,
                    Description: Describe(service, schema),
                    ResourceUri: LspConverters.PathToUri(schema.FilePath),
                    HasChildren: service.Rpcs.Length > 0,
                    ContextValue: SolutionNodeKind.ProtoService
                        + SolutionNodeKind.SecondaryTargetSuffix,
                    GoTo: Declaration(schema, service.Name.Span))));
            }
        }

        return
        [
            .. rows
                .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .Select(row => row.Node),
        ];
    }

    /// <summary>The rpcs of one service, in the order the schema declares them.</summary>
    /// <remarks>
    /// Declaration order, not alphabetical — unlike every other list in this section. Where an rpc
    /// sits in a service is written by a person and usually means something (the reads, then the
    /// writes), and a schema is short enough to read top to bottom. Services are sorted because
    /// their order across files is an accident of which project was listed first.
    /// </remarks>
    private static SolutionTreeNode[] Rpcs(string serviceId, CancellationToken ct)
    {
        if (Locate(serviceId, ct) is not var (schema, service))
            return [];

        return
        [
            .. service.Rpcs.Select(rpc => new SolutionTreeNode(
                Id: RpcPrefix + schema.FilePath + "|" + service.FullName + "|" + rpc.Name.Value,
                Kind: SolutionNodeKind.ProtoRpc,
                Label: rpc.Name.Value,
                Description: null,
                ResourceUri: LspConverters.PathToUri(schema.FilePath),
                HasChildren: false,
                ContextValue: SolutionNodeKind.ProtoRpc + SolutionNodeKind.SecondaryTargetSuffix,
                Tooltip: Signature(rpc),
                GoTo: Declaration(schema, rpc.Name.Span))),
        ];
    }

    /// <summary>
    /// The schema and service a service node id names, or null when neither is there any more.
    /// </summary>
    /// <remarks>
    /// The id carries the declaration's full name rather than its offset, and is resolved against
    /// a fresh parse every time. An offset would be stale the moment anything above the service
    /// was edited — the row would then expand to the wrong rpcs, or to none, with nothing on
    /// screen to say the tree was reading a file that had moved underneath it.
    /// </remarks>
    private static (ProtoFile Schema, ProtoService Service)? Locate(
        string serviceId, CancellationToken ct)
    {
        int split = serviceId.LastIndexOf('|');
        if (split < 0)
            return null;

        string path = serviceId[..split];
        string fullName = serviceId[(split + 1)..];

        ct.ThrowIfCancellationRequested();

        if (ProtoDocumentService.GetParse(path) is not { } schema)
            return null;

        foreach (var service in schema.Services)
        {
            if (string.Equals(service.FullName, fullName, StringComparison.Ordinal))
                return (schema, service);
        }

        return null;
    }

    /// <summary>
    /// Where clicking the row lands: the declaration's <em>name</em>, never the whole declaration.
    /// </summary>
    /// <remarks>
    /// Load-bearing beyond where the caret ends up. The Implementation button asks the server what
    /// implements this rpc by sending the position this range starts at, and
    /// <c>ProtoSymbolResolver</c> only recognises a service or an rpc from inside its name span.
    /// Point this at the whole declaration instead and the position lands on the <c>service</c>
    /// keyword, nothing resolves, and the button reports that the project has not been built — on
    /// a project that has been. A wrong answer with a plausible explanation, which is the worst
    /// kind, so it is pinned by a test.
    /// </remarks>
    private static SolutionTreeNavigation Declaration(ProtoFile schema, TextSpan name) =>
        new(LspConverters.PathToUri(schema.FilePath),
            LspConverters.ToRange(schema.Text.Lines, name));

    /// <summary>The package a file declares, or the mark for one that declares none.</summary>
    private static string PackageOf(ProtoFile schema) =>
        schema.Package is { Length: > 0 } package ? package : NoPackage;

    /// <summary>How many rpcs, and which file they are written in.</summary>
    /// <remarks>
    /// The file name earns its place because a package is not a file: two schemas can declare into
    /// one package, and then the only thing telling two service rows apart is where each is
    /// written.
    /// </remarks>
    private static string Describe(ProtoService service, ProtoFile schema)
    {
        string rpcs = service.Rpcs.Length == 1 ? "1 rpc" : $"{service.Rpcs.Length} rpcs";
        return $"{rpcs} · {Path.GetFileName(schema.FilePath)}";
    }

    /// <summary>
    /// The rpc as its schema writes it, streaming markers and all — on the hover, not on the row.
    /// </summary>
    /// <remarks>
    /// A message type is the least identifying thing about an rpc: <c>GetOrder</c> takes
    /// <c>GetOrderRequest</c> and returns <c>GetOrderResponse</c>, and a service's worth of rows
    /// then reads as the same sentence with one word changed — while the word that actually differs,
    /// the rpc's own name, is the one already in the label. Worse in the common case where the
    /// schema was generated from C#, since the types are then long CLR names and the row is mostly
    /// namespace. The signature still answers a real question, so it moves to the hover rather than
    /// going away.
    /// </remarks>
    private static string Signature(ProtoRpc rpc) =>
        $"{Side(rpc.RequestType, rpc.ClientStreaming)} → {Side(rpc.ResponseType, rpc.ServerStreaming)}";

    /// <summary>
    /// One side of an rpc. The leading dot a fully-qualified reference carries is dropped: it is
    /// meaningful to protoc's name resolution and noise in a list.
    /// </summary>
    private static string Side(ProtoTypeRef type, bool streaming)
    {
        string name = type.Text.TrimStart('.');
        return streaming ? $"stream {name}" : name;
    }
}
