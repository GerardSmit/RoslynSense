using RoslynMCP.Languages.Routes.Core;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.Symbols;
using Xunit;
using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Tests;

/// <summary>
/// The shape of the Routes tree: which shared prefixes become a row, what a row underneath one
/// shows, and what is left where it is.
/// </summary>
/// <remarks>
/// Pure, so the whole tree shape is checkable without a workspace, a compilation or a fixture —
/// which is the reason <see cref="RouteGrouping"/> is a separate file from the rows it feeds.
/// </remarks>
public class RouteGroupingTests
{
    /// <summary>The example the section exists for: one heading, the verbs underneath it.</summary>
    [Fact]
    public void PathsThatShareAPrefixAreGroupedUnderIt()
    {
        var level = RouteGrouping.Level(
            [Route("GET", "/api/v1/users"), Route("POST", "/api/v1/users")], string.Empty);

        var branch = Assert.Single(level.Groups);
        Assert.Equal("/api/v1/users", branch.Prefix);
        Assert.Equal("/api/v1/users", branch.Label);
        Assert.Equal(2, branch.Count);
        Assert.Empty(level.Leaves);
    }

    /// <summary>
    /// A prefix nothing branches on is one row, not a chain of them. Otherwise a solution whose
    /// every path begins <c>/api/v1</c> has to be clicked through twice to see anything.
    /// </summary>
    [Fact]
    public void APrefixWithNothingToChooseBetweenIsOneRow()
    {
        var level = RouteGrouping.Level(
            [Route("GET", "/api/v1/users"), Route("GET", "/api/v1/orders")], string.Empty);

        var branch = Assert.Single(level.Groups);
        Assert.Equal("/api/v1", branch.Prefix);
        Assert.Equal("/api/v1", branch.Label);
    }

    /// <summary>A group holding one endpoint is a folder holding one thing.</summary>
    [Fact]
    public void APathNobodyElseSharesStaysWhereItIs()
    {
        var level = RouteGrouping.Level(
            [Route("GET", "/api/v1/users"), Route("GET", "/api/v1/orders"), Route("GET", "/health")],
            string.Empty);

        Assert.Single(level.Groups);

        var leaf = Assert.Single(level.Leaves);
        Assert.Equal("/health", leaf.Path.Text);
    }

    [Fact]
    public void ASingleRouteIsNeverGrouped()
    {
        var level = RouteGrouping.Level([Route("GET", "/api/v1/users")], string.Empty);

        Assert.Empty(level.Groups);
        Assert.Single(level.Leaves);
    }

    /// <summary>Under a branch, a row shows what it adds — the prefix is already on screen.</summary>
    [Fact]
    public void ARowUnderABranchShowsOnlyWhatItAdds()
    {
        var route = Route("POST", "/api/v1/users");

        Assert.Equal("/users", RouteGrouping.Remainder(route, "/api/v1"));
        Assert.Equal("/api/v1/users", RouteGrouping.Remainder(route, string.Empty));
    }

    /// <summary>
    /// A branch can be served itself — <c>/api/v1</c> answering while <c>/api/v1/users</c> exists
    /// beside it. It has no remainder, and what it serves is the branch.
    /// </summary>
    [Fact]
    public void AnEndpointServedAtTheBranchItselfIsALeafOfIt()
    {
        var level = RouteGrouping.Level(
            [Route("GET", "/api/v1"), Route("GET", "/api/v1/users"), Route("GET", "/api/v1/orders")],
            "/api/v1");

        Assert.Empty(level.Groups);
        Assert.Equal(
            ["/api/v1", "/api/v1/orders", "/api/v1/users"],
            level.Leaves.Select(endpoint => endpoint.Path.Text));

        Assert.Equal("/", RouteGrouping.Remainder(level.Leaves[0], "/api/v1"));
        Assert.Equal("/orders", RouteGrouping.Remainder(level.Leaves[1], "/api/v1"));
    }

    [Fact]
    public void BranchesNest()
    {
        var top = RouteGrouping.Level(
            [
                Route("GET", "/api/users"), Route("POST", "/api/users"),
                Route("GET", "/api/orders"), Route("POST", "/api/orders"),
            ],
            string.Empty);

        var api = Assert.Single(top.Groups);
        Assert.Equal("/api", api.Prefix);
        Assert.Equal(4, api.Count);
    }

    /// <summary>
    /// A path only knowable at run time has no prefix to sit under, and inventing one for it would
    /// be the guess the whole pack refuses to make. It belongs to the project.
    /// </summary>
    [Fact]
    public void APathNobodyCouldReadBelongsToTheProjectAndNoBranch()
    {
        var unreadable = new RouteEndpoint(
            Path: new RegistrationFacet(null, RegistrationOrigin.Expression, "configured"),
            Verb: "GET",
            Handler: RegistrationFacet.Absent,
            Source: RouteSource.Registration,
            ProjectPath: Project,
            FilePath: File,
            Offset: 0,
            Declaration: Zero);

        var roots = RouteGrouping.Level(
            [unreadable, Route("GET", "/api/v1/users"), Route("GET", "/api/v1/orders")],
            string.Empty);

        Assert.Single(roots.Groups);
        Assert.Single(roots.Leaves);
        Assert.Null(Assert.Single(roots.Leaves).Path.Text);

        // And it does not reappear underneath the branch it has no claim on.
        Assert.Empty(RouteGrouping.Level([unreadable], "/api/v1").Leaves);
    }

    // ---- The fixture ---------------------------------------------------------------------------

    private const string Project = @"C:\src\Application.csproj";

    private const string File = @"C:\src\Web.cs";

    private static readonly Range Zero = new(new Position(0, 0), new Position(0, 0));

    private static int s_offset;

    private static RouteEndpoint Route(string verb, string path) =>
        new(Path: new RegistrationFacet(path, RegistrationOrigin.Literal, null),
            Verb: verb,
            Handler: RegistrationFacet.Absent,
            Source: RouteSource.Registration,
            ProjectPath: Project,
            FilePath: File,
            Offset: s_offset++,
            Declaration: Zero);
}
