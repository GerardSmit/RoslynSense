using RoslynMCP.Languages;
using RoslynMCP.Languages.Cron;
using RoslynMCP.Languages.Cron.Core;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The <b>Cron Jobs</b> section of the Solution Explorer: when it appears, what hangs off it, and
/// what drawing it is allowed to cost.
/// </summary>
/// <remarks>
/// The first pack to put anything in the tree, so half of what is checked here is the seam rather
/// than the section: that a contributor's ids route back to it, that its prefix cannot shadow the
/// tree's own, and — the one that is otherwise only a comment — that drawing the solution root
/// still evaluates no project.
/// </remarks>
[Collection(SharedState.Name)]
public class CronSolutionTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"cron-tree-{Guid.NewGuid():N}");

    private readonly string _solution;
    private readonly string _scheduled;
    private readonly string _plain;

    public CronSolutionTreeTests()
    {
        _scheduled = Project("Acme.Worker", scheduler: true);
        _plain = Project("Acme.Domain", scheduler: false);
        _solution = Solution();
    }

    // ---- When the section appears -----------------------------------------------------------------

    [Fact]
    public async Task TheSectionAppearsAfterTheProjects()
    {
        var nodes = await SolutionChildrenAsync(Enabled);

        var section = Assert.Single(nodes.Where(n => n.Kind == SolutionNodeKind.CronJobs));
        Assert.Equal("Cron Jobs", section.Label);
        Assert.True(section.HasChildren);

        // After the structure, not sorted in among it. A section is not another solution folder,
        // and putting it in the alphabetical run would read as one.
        Assert.Equal(nodes.Length - 1, Array.IndexOf(nodes, section));
        Assert.Contains(nodes, n => n.Kind == SolutionNodeKind.Project);
    }

    /// <summary>
    /// A solution that schedules nothing gets no section at all — the answer in most solutions,
    /// and the one that has to stay free.
    /// </summary>
    [Fact]
    public async Task ASolutionWithNoSchedulerHasNoSection()
    {
        File.Delete(_scheduled);
        File.WriteAllText(
            _scheduled,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + "<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var nodes = await SolutionChildrenAsync(Enabled);

        Assert.DoesNotContain(nodes, n => n.Kind == SolutionNodeKind.CronJobs);
    }

    [Fact]
    public async Task TurningThePackOffRemovesTheSection()
    {
        var nodes = await SolutionChildrenAsync(new LanguageSession([Pack(CronSettings.Disabled)]));

        Assert.DoesNotContain(nodes, n => n.Kind == SolutionNodeKind.CronJobs);
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

        var nodes = await SolutionChildrenAsync(configured);
        Assert.Single(nodes.Where(n => n.Kind == SolutionNodeKind.CronJobs));

        var projects = await ChildrenAsync(SectionId, configured);

        Assert.Contains(projects, n => n.Label == "Acme.Domain");
    }

    // ---- What hangs off it ------------------------------------------------------------------------

    [Fact]
    public async Task OnlyTheProjectsThatScheduleAnythingAreListed()
    {
        var projects = await ChildrenAsync(SectionId);

        var project = Assert.Single(projects);
        Assert.Equal("Acme.Worker", project.Label);
        Assert.Equal(SolutionNodeKind.CronProject, project.Kind);
        Assert.True(project.HasChildren);
    }

    /// <summary>
    /// Two nodes sharing one id makes the second branch fail to render, so distinctness is not a
    /// tidiness point — it is whether the tree draws.
    /// </summary>
    [Fact]
    public async Task EveryIdInTheSectionIsDistinct()
    {
        var ids = new List<string>();

        var nodes = await SolutionChildrenAsync(Enabled);
        ids.AddRange(nodes.Select(n => n.Id));

        var projects = await ChildrenAsync(SectionId);
        ids.AddRange(projects.Select(n => n.Id));

        foreach (var project in projects)
            ids.AddRange((await ChildrenAsync(project.Id)).Select(n => n.Id));

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // ---- The seam ---------------------------------------------------------------------------------

    /// <summary>
    /// The routing arm goes last, after every id shape the tree mints itself. Without that, a
    /// contributor could take over <c>project:</c> by choosing the wrong prefix.
    /// </summary>
    [Fact]
    public async Task APackCannotShadowTheTreesOwnNodes()
    {
        var greedy = new LanguageSession([new PrefixContributor("project:")]);

        var nodes = await SolutionTreeHandler.ChildrenAsync(
            new SolutionTreeParams(NodeId: $"project:{_scheduled}"), default, greedy);

        Assert.Contains(nodes, n => n.Kind == SolutionNodeKind.Dependencies);
    }

    /// <summary>
    /// A contributor that throws costs its own section and nothing else. A pack must not be able
    /// to empty the Explorer.
    /// </summary>
    [Fact]
    public async Task AContributorThatThrowsDoesNotCostTheTree()
    {
        var nodes = await SolutionChildrenAsync(new LanguageSession([new ThrowingContributor()]));

        Assert.Contains(nodes, n => n.Kind == SolutionNodeKind.Project);
    }

    /// <summary>
    /// The invariant the whole two-method split exists for, and otherwise only a comment: drawing
    /// the solution root evaluates no project. An MSBuild evaluation is not fast and it queues
    /// behind a solution load, so a section that needed one would stall the tree on a large repo.
    /// </summary>
    [Fact]
    public async Task DrawingTheRootEvaluatesNoProject()
    {
        ProjectEvaluationService.Clear();

        await SolutionChildrenAsync(Enabled);

        Assert.Null(ProjectEvaluationService.TryGetCached(_scheduled));
        Assert.Null(ProjectEvaluationService.TryGetCached(_plain));
    }

    // ---- The fixture and the harness --------------------------------------------------------------

    private string SectionId => $"cron:{Path.GetFullPath(_solution)}";

    private static LanguageSession Enabled => new([Pack(CronSettings.Default)]);

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

    private async Task<SolutionTreeNode[]> SolutionChildrenAsync(LanguageSession languages)
    {
        using var bound = WorkspaceService.BindSolutionForTesting(_solution);

        return await SolutionTreeHandler.ChildrenAsync(
            new SolutionTreeParams(NodeId: $"solution:{Path.GetFullPath(_solution)}"),
            default,
            languages);
    }

    private async Task<SolutionTreeNode[]> ChildrenAsync(
        string nodeId, LanguageSession? languages = null)
    {
        using var bound = WorkspaceService.BindSolutionForTesting(_solution);

        return await SolutionTreeHandler.ChildrenAsync(
            new SolutionTreeParams(NodeId: nodeId), default, languages ?? Enabled);
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

    /// <summary>A contributor claiming a prefix the tree already uses for its own nodes.</summary>
    private sealed class PrefixContributor(string prefix)
        : ILanguagePack, ILanguageSolutionTreeContributor
    {
        public string Id => "greedy";

        public string DisplayName => "Greedy";

        public System.Collections.Immutable.ImmutableArray<string> FileExtensions { get; } = [];

        public LanguageCapabilities Capabilities => LanguageCapabilities.None;

        public System.Collections.Immutable.ImmutableArray<string> WellKnownTypeNames { get; } = [];

        public System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.SymbolKind>
            InterestingSymbolKinds { get; } = [];

        public bool IsProjectionPath(string? filePath) => false;

        public string NodeIdPrefix => prefix;

        public Task<SolutionTreeNode?> SectionAsync(string solutionPath, CancellationToken ct) =>
            Task.FromResult<SolutionTreeNode?>(null);

        public Task<SolutionTreeNode[]> ChildrenAsync(
            string nodeId, SolutionTreeParams p, CancellationToken ct) =>
            Task.FromResult<SolutionTreeNode[]>(
            [
                new SolutionTreeNode(
                    "greedy:stolen", "file", "stolen", null, null, false, "file"),
            ]);
    }

    /// <summary>A contributor whose section throws, which must cost only its own row.</summary>
    private sealed class ThrowingContributor : ILanguagePack, ILanguageSolutionTreeContributor
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
