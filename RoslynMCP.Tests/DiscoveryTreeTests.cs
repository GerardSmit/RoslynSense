using RoslynMCP.Languages;
using RoslynMCP.Languages.Cron;
using RoslynMCP.Languages.Cron.Core;
using RoslynMCP.Languages.Proto;
using RoslynMCP.Languages.Routes;
using RoslynMCP.Languages.Routes.Core;
using RoslynMCP.Languages.Templates;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The Discovery view: which sections appear, what hangs off them, and what drawing it is allowed
/// to cost.
/// </summary>
/// <remarks>
/// Half of what is checked here is the seam rather than any one section — that a contributor's ids
/// route back to it, that a pack which throws costs only its own row, and, the one that is
/// otherwise only a comment, that listing the roots evaluates no project.
/// </remarks>
[Collection(SharedState.Name)]
public class DiscoveryTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"discovery-tree-{Guid.NewGuid():N}");

    private readonly string _solution;
    private readonly string _scheduled;
    private readonly string _plain;

    public DiscoveryTreeTests()
    {
        _scheduled = Project("Acme.Worker", scheduler: true);
        _plain = Project("Acme.Domain", scheduler: false);
        _solution = Solution();
    }

    // ---- Where the sections live now ----------------------------------------------------------

    [Fact]
    public async Task TheSectionsAreTheRootsOfTheDiscoveryView()
    {
        var roots = await RootsAsync(Cron);

        var section = Assert.Single(roots.Where(n => n.Kind == SolutionNodeKind.CronJobs));
        Assert.Equal("Cron Jobs", section.Label);
        Assert.True(section.HasChildren);
    }

    /// <summary>
    /// And they are no longer in the Solution Explorer, which is the move this view exists for.
    /// A section is not a solution folder, and sitting in a list of them made the one row that
    /// could not be browsed by location look exactly like the rows that could.
    /// </summary>
    [Fact]
    public async Task TheSolutionExplorerNoLongerCarriesSections()
    {
        using var bound = WorkspaceService.BindSolutionForTesting(_solution);

        var nodes = await SolutionTreeHandler.ChildrenAsync(
            new SolutionTreeParams(NodeId: $"solution:{Path.GetFullPath(_solution)}"), default);

        Assert.Contains(nodes, n => n.Kind == SolutionNodeKind.Project);
        Assert.DoesNotContain(nodes, n => n.Kind == SolutionNodeKind.CronJobs);
    }

    // ---- When a section appears ---------------------------------------------------------------

    /// <summary>
    /// A solution that schedules nothing gets no section at all — the answer in most solutions,
    /// and the one that has to stay free.
    /// </summary>
    [Fact]
    public async Task ASolutionWithNoSchedulerHasNoCronSection()
    {
        File.WriteAllText(
            _scheduled,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + "<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var roots = await RootsAsync(Cron);

        Assert.DoesNotContain(roots, n => n.Kind == SolutionNodeKind.CronJobs);
    }

    [Fact]
    public async Task TurningThePackOffRemovesTheSection()
    {
        var roots = await RootsAsync(new LanguageSession([Pack(CronSettings.Disabled)]));

        Assert.DoesNotContain(roots, n => n.Kind == SolutionNodeKind.CronJobs);
    }

    /// <summary>
    /// A package probe alone would hide the section from exactly the person who asked for it: a
    /// configured binding names an in-house wrapper, and the project declaring one references no
    /// scheduling package — that is what made it a wrapper.
    /// </summary>
    [Fact]
    public async Task AConfiguredBindingShowsTheSectionForAProjectWithNoPackage()
    {
        var configured = new LanguageSession([Pack(Configured)]);

        var roots = await RootsAsync(configured);
        Assert.Single(roots.Where(n => n.Kind == SolutionNodeKind.CronJobs));

        var projects = await ChildrenAsync(CronSectionId, configured);

        Assert.Contains(projects, n => n.Label == "Acme.Domain");
    }

    [Fact]
    public async Task OnlyTheProjectsThatScheduleAnythingAreListed()
    {
        var projects = await ChildrenAsync(CronSectionId, Cron);

        var project = Assert.Single(projects);
        Assert.Equal("Acme.Worker", project.Label);
        Assert.Equal(SolutionNodeKind.CronProject, project.Kind);
    }

    // ---- Proto: package, service, rpc ----------------------------------------------------------

    [Fact]
    public async Task TheProtoSectionAppearsWhenAProjectDeclaresASchema()
    {
        Schema("Acme.Worker", "orders.proto", OrdersSchema);

        var roots = await RootsAsync(Proto);

        var section = Assert.Single(roots.Where(n => n.Kind == SolutionNodeKind.ProtoServices));
        Assert.Equal("Proto", section.Label);
    }

    [Fact]
    public async Task ASolutionWithNoSchemaHasNoProtoSection()
    {
        var roots = await RootsAsync(Proto);

        Assert.DoesNotContain(roots, n => n.Kind == SolutionNodeKind.ProtoServices);
    }

    [Fact]
    public async Task ServicesAreGroupedByThePackageThatDeclaresThem()
    {
        Schema("Acme.Worker", "orders.proto", OrdersSchema);

        var packages = await ChildrenAsync(ProtoSectionId, Proto);

        var package = Assert.Single(packages);
        Assert.Equal("orders.v1", package.Label);
        Assert.Equal("1 service", package.Description);

        var services = await ChildrenAsync(package.Id, Proto);

        var service = Assert.Single(services);
        Assert.Equal("OrderService", service.Label);
        Assert.Equal(SolutionNodeKind.ProtoService, service.Kind);
        Assert.StartsWith("2 rpcs", service.Description);
    }

    /// <summary>
    /// A file declaring no package is legal, and protoc puts its declarations at the root. The row
    /// cannot be dropped and cannot be given a made-up name either.
    /// </summary>
    [Fact]
    public async Task AFileWithNoPackageIsStillListed()
    {
        Schema("Acme.Worker", "loose.proto", """
            syntax = "proto3";
            service LooseService {
              rpc Ping (PingRequest) returns (PingReply);
            }
            message PingRequest {}
            message PingReply {}
            """);

        var packages = await ChildrenAsync(ProtoSectionId, Proto);

        var package = Assert.Single(packages);
        Assert.Contains("no package", package.Label, StringComparison.Ordinal);
    }

    /// <summary>
    /// A schema holding only messages is the shared types every other one imports. A package row
    /// that expands to nothing says less than no row at all.
    /// </summary>
    [Fact]
    public async Task ASchemaWithNoServicesIsNotAPackageRow()
    {
        Schema("Acme.Worker", "types.proto", """
            syntax = "proto3";
            package acme.types;
            message Money { int64 units = 1; }
            """);

        // The section itself still appears: whether it has anything to say is a parse away, and
        // the root listing is not allowed to pay for a parse of every schema in the solution.
        Assert.Empty(await ChildrenAsync(ProtoSectionId, Proto));
    }

    /// <summary>
    /// The row is the rpc's name and nothing else; the signature is on the hover.
    /// </summary>
    /// <remarks>
    /// A message type is the least identifying thing about an rpc — <c>GetOrder</c> takes
    /// <c>GetOrderRequest</c> and returns <c>GetOrderResponse</c> — so a service's worth of rows
    /// reads as one sentence with a word changed, and the word that differs is already the label.
    /// </remarks>
    [Fact]
    public async Task AnRpcRowIsItsNameAndTheHoverIsItsSignature()
    {
        Schema("Acme.Worker", "orders.proto", OrdersSchema);

        var rpcs = await RpcsAsync();

        Assert.Collection(
            rpcs,
            // Declaration order, not alphabetical: where an rpc sits in a service is written by a
            // person and usually means something.
            get =>
            {
                Assert.Equal("GetOrder", get.Label);
                Assert.Null(get.Description);
                Assert.Equal("GetOrderRequest → Order", get.Tooltip);
            },
            watch =>
            {
                Assert.Equal("WatchOrders", watch.Label);
                Assert.Null(watch.Description);
                Assert.Equal("WatchRequest → stream Order", watch.Tooltip);
            });
    }

    /// <summary>
    /// The trap this whole section is one mistake away from, so it is pinned rather than trusted.
    /// </summary>
    /// <remarks>
    /// The Implementation button asks the server what honours an rpc by sending the position this
    /// range starts at, and the proto resolver only recognises a service or an rpc from inside its
    /// <em>name</em>. Point the range at the whole declaration instead and the position lands on
    /// the <c>rpc</c> keyword, nothing resolves, and the button reports that the project has not
    /// been built — on a project that has been. Nothing else would catch that: the row still
    /// renders, the click still opens the right file, and only the button is quietly wrong.
    /// </remarks>
    [Fact]
    public async Task ARowPointsAtTheDeclarationsNameNotItsWholeDeclaration()
    {
        string path = Schema("Acme.Worker", "orders.proto", OrdersSchema);
        string text = File.ReadAllText(path);

        var rpc = (await RpcsAsync()).First(n => n.Label == "GetOrder");

        Assert.NotNull(rpc.GoTo);
        var lines = Microsoft.CodeAnalysis.Text.SourceText.From(text).Lines;
        int offset = lines[rpc.GoTo!.Range.Start.Line].Start + rpc.GoTo.Range.Start.Character;

        Assert.Equal("GetOrder", text.Substring(offset, "GetOrder".Length));
    }

    /// <summary>
    /// An rpc row says it leads somewhere without saying where, which is the deferred half of
    /// <see cref="SolutionNodeKind.SecondaryTargetSuffix"/>: resolving it is a solution-wide symbol
    /// search, far too much to run for every row the moment a service is expanded.
    /// </summary>
    [Fact]
    public async Task AnRpcOffersAnImplementationItDoesNotCarry()
    {
        Schema("Acme.Worker", "orders.proto", OrdersSchema);

        var rpc = (await RpcsAsync()).First();

        Assert.EndsWith(SolutionNodeKind.SecondaryTargetSuffix, rpc.ContextValue, StringComparison.Ordinal);
        Assert.Null(rpc.GoToSecondary);
    }

    /// <summary>
    /// A contracts schema is normally compiled by the service and by its clients both. It declares
    /// the same services however many projects list it.
    /// </summary>
    [Fact]
    public async Task ASchemaCompiledByTwoProjectsIsListedOnce()
    {
        string path = Schema("Acme.Worker", "orders.proto", OrdersSchema);
        Declare("Acme.Domain", path);

        var packages = await ChildrenAsync(ProtoSectionId, Proto);

        var package = Assert.Single(packages);
        Assert.Equal("1 service", package.Description);
        Assert.Single(await ChildrenAsync(package.Id, Proto));
    }

    // ---- Routes: which projects serve HTTP ----------------------------------------------------

    [Fact]
    public async Task TheRoutesSectionAppearsForAProjectThatCouldServeHttp()
    {
        Web("Acme.Api");

        var roots = await RootsAsync(Routes);

        var section = Assert.Single(roots.Where(n => n.Kind == SolutionNodeKind.Routes));
        Assert.Equal("Routes", section.Label);
        Assert.True(section.HasChildren);
    }

    /// <summary>
    /// The answer in a solution of libraries and workers, and the one that has to stay free — the
    /// probe reads manifests, never a compilation.
    /// </summary>
    [Fact]
    public async Task ASolutionThatServesNoHttpHasNoRoutesSection()
    {
        var roots = await RootsAsync(Routes);

        Assert.DoesNotContain(roots, n => n.Kind == SolutionNodeKind.Routes);
    }

    [Fact]
    public async Task TurningTheRoutesPackOffRemovesTheSection()
    {
        Web("Acme.Api");

        var roots = await RootsAsync(new LanguageSession([new RoutesLanguage(RoutesSettings.Disabled)]));

        Assert.DoesNotContain(roots, n => n.Kind == SolutionNodeKind.Routes);
    }

    /// <summary>
    /// The same widening the cron section does, for the same reason: a configured attribute names
    /// an in-house routing layer, and the project declaring one references no web framework — that
    /// is what made it in-house. A framework probe alone would hide the section from precisely the
    /// person who configured it.
    /// </summary>
    [Fact]
    public async Task AConfiguredAttributeShowsTheRoutesSectionForAProjectWithNoFramework()
    {
        var configured = new LanguageSession([new RoutesLanguage(ConfiguredRoutes)]);

        var roots = await RootsAsync(configured);
        Assert.Single(roots.Where(n => n.Kind == SolutionNodeKind.Routes));

        var projects = await ChildrenAsync(RoutesSectionId, configured);

        Assert.Contains(projects, n => n.Label == "Acme.Domain");
    }

    [Fact]
    public async Task OnlyTheProjectsThatCouldServeHttpAreListed()
    {
        Web("Acme.Api");

        var projects = await ChildrenAsync(RoutesSectionId, Routes);

        var project = Assert.Single(projects);
        Assert.Equal("Acme.Api", project.Label);
        Assert.Equal(SolutionNodeKind.RouteProject, project.Kind);
    }

    /// <summary>
    /// A project the workspace has not got to yet says so. Showing it with no rows would read as
    /// "this application serves nothing", which is the one thing the section must never say by
    /// accident — and it is the state every project is in while a large solution is still loading.
    /// </summary>
    [Fact]
    public async Task AProjectNotLoadedYetSaysSoRatherThanLookingEmpty()
    {
        Web("Acme.Api");

        var project = Assert.Single(await ChildrenAsync(RoutesSectionId, Routes));

        Assert.Equal("not loaded", project.Description);
        Assert.True(project.Dimmed);

        // Not expandable, on purpose. There is nothing under it yet, and a twistie that opens onto
        // an empty list says "this application serves nothing" — which is the one thing the row is
        // there to avoid saying.
        Assert.False(project.HasChildren);
    }

    /// <summary>
    /// A project that serves nothing is not listed at all.
    /// </summary>
    /// <remarks>
    /// The probe that put it in front of us reads manifests, so it says "could serve HTTP" rather
    /// than "does" — a project referencing the framework and declaring no endpoint is ordinary. A
    /// row reading <c>0 routes</c> is a question the reader then has to answer for themselves, and
    /// the answer is always "nothing to see".
    /// </remarks>
    [Fact]
    public void AProjectThatServesNothingIsNotListed()
    {
        Assert.Null(RoutesLanguage.ProjectNode("Acme.Api", _plain, count: 0));
    }

    /// <summary>
    /// Zero and "not counted yet" are different statements, and only one of them hides the row.
    /// Hiding an unloaded project would empty the section during a solution load and then fill it
    /// in, which reads as the section being wrong rather than as the load being unfinished.
    /// </summary>
    [Fact]
    public void NotCountedYetIsNotTheSameAnswerAsNone()
    {
        var pending = RoutesLanguage.ProjectNode("Acme.Api", _plain, count: null);

        Assert.NotNull(pending);
        Assert.Equal("not loaded", pending!.Description);
        Assert.True(pending.Dimmed);

        var counted = RoutesLanguage.ProjectNode("Acme.Api", _plain, count: 1);

        Assert.Equal("1 route", counted!.Description);
        Assert.True(counted.HasChildren);
        Assert.False(counted.Dimmed);

        Assert.Equal("7 routes", RoutesLanguage.ProjectNode("Acme.Api", _plain, 7)!.Description);
    }

    // ---- Templates ----------------------------------------------------------------------------

    /// <summary>
    /// The section costs a directory probe to decide against, which is what the root listing's
    /// promise allows: no project is evaluated and no file is read to find out that a solution
    /// declares no screens.
    /// </summary>
    [Fact]
    public async Task ASolutionWithNoTemplateFolderHasNoTemplatesSection()
    {
        Assert.DoesNotContain(
            await RootsAsync(Templates),
            node => node.Kind == SolutionNodeKind.Templates);
    }

    /// <summary>
    /// One application's worth of templates, so the section holds the tree itself — the row naming
    /// the application would be a level that never offers a choice.
    /// </summary>
    [Fact]
    public async Task TheSectionHoldsTheTreeTheFilesDescribe()
    {
        WriteTemplate("""
            tabs:
              Root:
                name:
                  nl-NL: Wortel
              Child:
                name:
                  nl-NL: Kind
                parent: Root
            """);

        var section = Assert.Single(
            (await RootsAsync(Templates)).Where(node => node.Kind == SolutionNodeKind.Templates));

        Assert.Equal("Templates", section.Label);

        var root = Assert.Single(await ChildrenAsync(section.Id, Templates));

        Assert.Equal("Wortel", root.Label);
        Assert.Equal("1 page", root.Description);
        Assert.True(root.HasChildren);

        var child = Assert.Single(await ChildrenAsync(root.Id, Templates));

        Assert.Equal("Kind", child.Label);
        Assert.False(child.HasChildren);

        // And the click lands on the line that declares it, in the file that declares it.
        Assert.NotNull(child.GoTo);
        Assert.EndsWith("1-first.yml", child.GoTo.Uri, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An endpoint is a leaf; asking for its children is a client that lost its place.</summary>
    [Fact]
    public async Task AnEndpointHasNothingUnderIt()
    {
        Web("Acme.Api");

        Assert.Empty(await ChildrenAsync($"routes:e|{_plain}|{_plain}|0|GET", Routes));
    }

    // ---- The seam ------------------------------------------------------------------------------

    [Fact]
    public async Task EveryIdInTheViewIsDistinct()
    {
        Schema("Acme.Worker", "orders.proto", OrdersSchema);
        var languages = new LanguageSession([Pack(CronSettings.Default), ProtoPack]);

        var ids = new List<string>();

        var roots = await RootsAsync(languages);
        ids.AddRange(roots.Select(n => n.Id));

        foreach (var section in roots)
        {
            var children = await ChildrenAsync(section.Id, languages);
            ids.AddRange(children.Select(n => n.Id));

            foreach (var child in children)
                ids.AddRange((await ChildrenAsync(child.Id, languages)).Select(n => n.Id));
        }

        // Two nodes sharing one id makes the second branch fail to render, so this is not a
        // tidiness point — it is whether the tree draws at all.
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// A contributor that throws costs its own section and nothing else. A pack must not be able
    /// to empty the view.
    /// </summary>
    [Fact]
    public async Task AContributorThatThrowsDoesNotCostTheView()
    {
        var roots = await RootsAsync(
            new LanguageSession([new ThrowingContributor(), Pack(CronSettings.Default)]));

        Assert.Contains(roots, n => n.Kind == SolutionNodeKind.CronJobs);
    }

    /// <summary>An id no pack claims is an empty node, not an exception.</summary>
    [Fact]
    public async Task AnIdNoPackClaimsIsEmpty()
    {
        Assert.Empty(await ChildrenAsync("nobody:whatever", Cron));
    }

    /// <summary>
    /// The invariant the two-method split exists for, and otherwise only a comment: listing the
    /// roots evaluates no project. An MSBuild evaluation is not fast and it queues behind a
    /// solution load, so a section that needed one would stall the view on a large repository.
    /// </summary>
    [Fact]
    public async Task DrawingTheRootsEvaluatesNoProject()
    {
        Schema("Acme.Worker", "orders.proto", OrdersSchema);
        ProjectEvaluationService.Clear();

        string web = Web("Acme.Api");
        ProjectEvaluationService.Clear();

        await RootsAsync(new LanguageSession(
            [Pack(CronSettings.Default), ProtoPack, new RoutesLanguage(RoutesSettings.Default)]));

        Assert.Null(ProjectEvaluationService.TryGetCached(_scheduled));
        Assert.Null(ProjectEvaluationService.TryGetCached(_plain));
        Assert.Null(ProjectEvaluationService.TryGetCached(web));
    }

    // ---- The fixture and the harness -----------------------------------------------------------

    private const string OrdersSchema = """
        syntax = "proto3";
        package orders.v1;

        service OrderService {
          rpc GetOrder (GetOrderRequest) returns (Order);
          rpc WatchOrders (WatchRequest) returns (stream Order);
        }

        message GetOrderRequest { string id = 1; }
        message WatchRequest {}
        message Order { string id = 1; }
        """;

    private string CronSectionId => $"cron:{Path.GetFullPath(_solution)}";

    private string ProtoSectionId => $"proto:{Path.GetFullPath(_solution)}";

    private string RoutesSectionId => $"routes:{Path.GetFullPath(_solution)}";

    private static LanguageSession Cron => new([Pack(CronSettings.Default)]);

    private static LanguageSession Proto => new([ProtoPack]);

    private static ProtoLanguage ProtoPack => new(new MarkdownFormatter());

    private static LanguageSession Routes => new([new RoutesLanguage(RoutesSettings.Default)]);

    /// <summary>
    /// A fresh pack per session, deliberately: the merged templates are cached on the pack, and a
    /// shared one would serve the first test's folder to the second.
    /// </summary>
    private static LanguageSession Templates =>
        new([new TemplatesLanguage(TemplatesSettings.Default)]);

    /// <summary>Settings carrying one attribute of the user's own, beyond the shipped table.</summary>
    private static RoutesSettings ConfiguredRoutes => RoutesSettings.Default with
    {
        Attributes =
        [
            .. RoutePresets.Attributes,
            new RouteAttributeBinding { AttributeName = "Endpoint" },
        ],
    };

    /// <summary>Settings carrying one binding of the user's own, beyond the shipped table.</summary>
    private static CronSettings Configured => CronSettings.Default with
    {
        Bindings =
        [
            .. CronPresets.Bindings,
            new CronBinding
            {
                ContainingType = "Acme.Domain.Scheduler",
                MemberName = "Enqueue",
                CronIndex = 1,
            },
        ],
    };

    private static CronLanguage Pack(CronSettings settings) => new(settings);

    private async Task<SolutionTreeNode[]> RootsAsync(LanguageSession languages)
    {
        using var bound = WorkspaceService.BindSolutionForTesting(_solution);

        return await DiscoveryTreeHandler.ChildrenAsync(
            new SolutionTreeParams(), default, languages);
    }

    private async Task<SolutionTreeNode[]> ChildrenAsync(string nodeId, LanguageSession languages)
    {
        using var bound = WorkspaceService.BindSolutionForTesting(_solution);

        return await DiscoveryTreeHandler.ChildrenAsync(
            new SolutionTreeParams(NodeId: nodeId), default, languages);
    }

    /// <summary>The rpcs of the one service in the orders schema, down the whole chain.</summary>
    private async Task<SolutionTreeNode[]> RpcsAsync()
    {
        var package = Assert.Single(await ChildrenAsync(ProtoSectionId, Proto));
        var service = Assert.Single(await ChildrenAsync(package.Id, Proto));
        return await ChildrenAsync(service.Id, Proto);
    }

    /// <summary>Writes one template file into the plain project, in the conventional folder.</summary>
    private void WriteTemplate(string yaml)
    {
        string folder = Path.Combine(
            Path.GetDirectoryName(_plain)!, "App_Data", "Templates");

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "1-first.yml"), yaml);
    }

    /// <summary>Writes a schema into a project and declares it there.</summary>
    private string Schema(string project, string name, string content)
    {
        string path = Path.Combine(_root, project, name);
        File.WriteAllText(path, content);
        Declare(project, path);
        return path;
    }

    /// <summary>Adds a <c>Protobuf</c> item to a project, however far away the file is.</summary>
    private void Declare(string project, string schemaPath)
    {
        string manifest = Path.Combine(_root, project, $"{project}.csproj");
        string include = Path.GetRelativePath(Path.Combine(_root, project), schemaPath);
        string text = File.ReadAllText(manifest);

        File.WriteAllText(manifest, text.Replace(
            "</Project>",
            $"  <ItemGroup><Protobuf Include=\"{include}\" /></ItemGroup>\n</Project>"));
    }

    /// <summary>Adds a project the probe will call a web application, and lists it in the solution.</summary>
    private string Web(string name)
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, $"{name}.csproj");
        File.WriteAllText(path, """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(directory, "Program.cs"), "class Program { }");

        File.AppendAllText(_solution,
            $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{name}\", "
            + $"\"{name}\\{name}.csproj\", \"{{{Guid.NewGuid()}}}\"\nEndProject\n");

        return path;
    }

    private string Project(string name, bool scheduler)
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);

        string reference = scheduler
            ? "<ItemGroup><PackageReference Include=\"Hangfire.Core\" Version=\"1.8.0\" /></ItemGroup>"
            : string.Empty;

        string path = Path.Combine(directory, $"{name}.csproj");
        File.WriteAllText(path, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              {reference}
            </Project>
            """);

        File.WriteAllText(Path.Combine(directory, "Program.cs"), "class Program { }");
        return path;
    }

    private string Solution()
    {
        string path = Path.Combine(_root, "Acme.sln");
        File.WriteAllText(path,
            "Microsoft Visual Studio Solution File, Format Version 12.00\n"
            + Entry("Acme.Worker")
            + Entry("Acme.Domain"));

        return path;

        static string Entry(string name) =>
            $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{name}\", "
            + $"\"{name}\\{name}.csproj\", \"{{{Guid.NewGuid()}}}\"\nEndProject\n";
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A contributor whose section throws, which must cost only its own row.</summary>
    private sealed class ThrowingContributor : ILanguagePack, ILanguageDiscoveryContributor
    {
        public string Id => "throwing";

        public string DisplayName => "Throwing";

        public System.Collections.Immutable.ImmutableArray<string> FileExtensions { get; } = [];

        public LanguageCapabilities Capabilities => LanguageCapabilities.None;

        public System.Collections.Immutable.ImmutableArray<string> WellKnownTypeNames { get; } = [];

        public System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.SymbolKind>
            InterestingSymbolKinds { get; } = [];

        public bool IsProjectionPath(string? filePath) => false;

        public string NodeIdPrefix => "throwing:";

        public Task<SolutionTreeNode?> SectionAsync(string solutionPath, CancellationToken ct) =>
            throw new InvalidOperationException("no");

        public Task<SolutionTreeNode[]> ChildrenAsync(
            string nodeId, SolutionTreeParams p, CancellationToken ct) =>
            Task.FromResult<SolutionTreeNode[]>([]);
    }
}
