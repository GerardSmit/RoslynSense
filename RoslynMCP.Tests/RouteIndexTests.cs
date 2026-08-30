using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Routes;
using RoslynMCP.Languages.Routes.Core;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.Symbols;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Finding the HTTP endpoints in a project, and saying which parts of one a reader can actually
/// know.
/// </summary>
/// <remarks>
/// Most of what is checked here is the template arithmetic, because every rule in it is the
/// framework's rather than a choice: a prefix joins, a leading slash escapes, <c>[controller]</c> is
/// substituted, a group nests. Getting one wrong produces a row that is well-formed, plausible and
/// served by nobody — and a URL is the kind of thing a reader copies.
/// </remarks>
public class RouteIndexTests
{
    // ---- Attribute routing --------------------------------------------------------------------

    [Fact]
    public async Task AnActionJoinsItsControllersPrefixToItsOwnTemplate()
    {
        var route = await RouteAsync("GET /api/Orders/{id}");

        Assert.Equal("OrdersController.GetOrder", route.Handler.Text);
        Assert.False(route.IsDynamic);
    }

    /// <summary>
    /// <c>[controller]</c> is not a placeholder a request fills in — it is substituted at startup
    /// from the type's name, so leaving it in place would make every controller's rows read the
    /// same.
    /// </summary>
    [Fact]
    public async Task TheControllerTokenIsSubstitutedAndTheSuffixDropped()
    {
        var routes = await RoutesAsync();

        Assert.DoesNotContain(routes, route => route.Path.Text?.Contains("[controller]") == true);
        Assert.DoesNotContain(routes, route => route.Path.Text?.Contains("OrdersController") == true);
    }

    /// <summary>A verb attribute carrying no template is the controller's own path.</summary>
    [Fact]
    public async Task AVerbWithNoTemplateIsThePrefixOnItsOwn()
    {
        var route = await RouteAsync("GET /api/Orders");

        Assert.Equal("OrdersController.ListOrders", route.Handler.Text);
    }

    /// <summary>
    /// A leading slash is what an action uses to escape its controller's route, so the prefix is
    /// overridden rather than merely joined.
    /// </summary>
    [Fact]
    public async Task AnAbsoluteTemplateDiscardsThePrefix()
    {
        var route = await RouteAsync("GET /health");

        Assert.Equal("OrdersController.Health", route.Handler.Text);
    }

    /// <summary>
    /// The shape that would otherwise be counted twice: the verb attribute constrains the template
    /// written beside it rather than declaring an endpoint of its own.
    /// </summary>
    [Fact]
    public async Task AVerbBesideATemplateIsOneEndpointAndNotTwo()
    {
        var routes = await RoutesAsync();

        var cancel = Assert.Single(routes.Where(route => route.Handler.Text == "OrdersController.Cancel"));
        Assert.Equal("POST", cancel.Verb);
        Assert.Equal("/api/Orders/{id}/cancel", cancel.Path.Text);
    }

    /// <summary>
    /// Two verbs over one template are two endpoints, and the framework really does serve both.
    /// </summary>
    [Fact]
    public async Task TwoVerbsOverOneTemplateAreTwoEndpoints()
    {
        var routes = await RoutesAsync();

        var archive = routes.Where(route => route.Handler.Text == "OrdersController.Archive").ToList();

        Assert.Equal(2, archive.Count);
        Assert.Equal(["DELETE", "POST"], archive.Select(route => route.Verb).Order());
        Assert.All(archive, route => Assert.Equal("/api/Orders/{id}/archive", route.Path.Text));
    }

    /// <summary>
    /// No verb is not "GET". An action reachable by every method is a deliberate thing, and
    /// inventing one would be a row a reader could act on wrongly.
    /// </summary>
    [Fact]
    public async Task ARouteAttributeAloneConstrainsNoVerb()
    {
        var routes = await RoutesAsync();

        var search = Assert.Single(routes.Where(route => route.Handler.Text == "OrdersController.Search"));

        Assert.Null(search.Verb);
        Assert.Equal("/api/Orders/search", search.Path.Text);
    }

    /// <summary>A prefix written as a constant is a prefix the reader could have read.</summary>
    [Fact]
    public async Task AConstantPrefixIsFolded()
    {
        var route = await RouteAsync("GET /api/reports");

        Assert.Equal(RegistrationOrigin.Constant, route.Path.Origin);
        Assert.False(route.IsDynamic);
    }

    /// <summary>
    /// A controller with no route attribute anywhere is routed by a convention this pack has not
    /// read. The action is real and worth a row; its path is not the pack's to state.
    /// </summary>
    [Fact]
    public async Task AControllerRoutedByConventionSaysSoRatherThanGuessing()
    {
        var routes = await RoutesAsync();

        var index = Assert.Single(routes.Where(route => route.Handler.Text == "HomeController.Index"));

        Assert.Null(index.Path.Text);
        Assert.True(index.IsDynamic);
        Assert.Equal("convention", index.Path.Detail);
    }

    /// <summary>An attribute that is not one of the tables is not a route.</summary>
    [Fact]
    public async Task AnAttributeNoTableClaimsIsNotARoute()
    {
        var routes = await RoutesAsync();

        Assert.DoesNotContain(routes, route => route.Handler.Text == "OrdersController.NotAnEndpoint");
    }

    // ---- Minimal APIs -------------------------------------------------------------------------

    [Fact]
    public async Task ARegistrationCallIsAnEndpointToo()
    {
        var route = await RouteAsync("GET /health/live");

        Assert.Equal(RouteSource.Registration, route.Source);
    }

    [Fact]
    public async Task AGroupPrefixesTheEndpointsRegisteredOnIt()
    {
        var route = await RouteAsync("GET /api/v1/orders");

        Assert.Equal(RouteSource.Registration, route.Source);
    }

    [Fact]
    public async Task GroupsNest()
    {
        await RouteAsync("GET /api/v1/admin/stats");
    }

    [Fact]
    public async Task AGroupOpenedInlineWorksTheSameWay()
    {
        await RouteAsync("GET /inline/ping");
    }

    /// <summary>
    /// A group registers no endpoint of its own. A row for it would be a path with nothing serving
    /// it.
    /// </summary>
    [Fact]
    public async Task AGroupIsNotAnEndpointOfItsOwn()
    {
        var routes = await RoutesAsync();

        Assert.DoesNotContain(routes, route => route.Path.Text == "/api/v1");
        Assert.DoesNotContain(routes, route => route.Path.Text == "/api/v1/admin");
    }

    /// <summary>
    /// The whole point of the facet. A pattern nobody read is not printed as a path, and the row
    /// says which half of it was unreadable.
    /// </summary>
    [Fact]
    public async Task APathNobodyCouldReadIsNotPrintedAsAPath()
    {
        var routes = await RoutesAsync();

        var dynamic = Assert.Single(routes.Where(route => route.IsDynamic && route.Source == RouteSource.Registration));

        Assert.Null(dynamic.Path.Text);
        Assert.Contains("path", dynamic.Path.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bug the bare <c>Map</c> preset caused, kept as a test because the entry is the obvious
    /// thing to add back: an object mapper spells its method exactly the way a routing one does, so
    /// matching on the name alone put a row in the list for every mapped object in the solution —
    /// and made the tree pay a bind for each.
    /// </summary>
    [Fact]
    public async Task AnObjectMapperIsNotARoutingTable()
    {
        var routes = await RoutesAsync();

        Assert.DoesNotContain(routes, route => route.Handler.Text?.Contains("Map<") == true);
        Assert.All(routes, route => Assert.NotEqual("Map", route.Verb));
    }

    /// <summary>
    /// A registration always writes its pattern, so a call of the right name that writes none is
    /// somebody else's overload rather than an endpoint with a missing half. An attribute may
    /// legitimately write neither — conventional routing — which is why the guard is on calls only.
    /// </summary>
    [Fact]
    public async Task ACallOfTheRightNameThatWritesNoPatternIsNotAnEndpoint()
    {
        var routes = await RoutesAsync();

        Assert.Equal(
            1,
            routes.Count(route => route.Source == RouteSource.Registration && route.Path.Text is null));
    }

    /// <summary>A method group is a second place worth going.</summary>
    [Fact]
    public async Task AMethodGroupHandlerIsSomewhereToGo()
    {
        var route = await RouteAsync("POST /orders");

        Assert.Equal("Endpoints.CreateOrder", route.Handler.Text);
        Assert.NotNull(route.Target);
        Assert.NotNull(route.TargetUri);
    }

    /// <summary>
    /// A lambda is the handler, written where the row already points, so there is no second place
    /// to go — and reading a method out of its body would name whatever it calls first.
    /// </summary>
    [Fact]
    public async Task ALambdaHandlerIsNotASecondPlaceToGo()
    {
        var route = await RouteAsync("GET /health/live");

        Assert.Null(route.Target);
        Assert.Null(route.Handler.Text);
    }

    // ---- The rows -----------------------------------------------------------------------------

    /// <summary>
    /// The path is the row, and the verb is the dimmed half to the right of it.
    /// </summary>
    /// <remarks>
    /// Leading with the verb would ragged-left the paths — the column a reader scans — behind three
    /// to six characters of <c>GET</c>, <c>POST</c> and <c>DELETE</c>, and would sort one
    /// resource's own rows away from each other.
    /// </remarks>
    [Fact]
    public async Task ThePathIsTheRowAndTheVerbSitsBesideIt()
    {
        var row = RoutesLanguage.Node(await RouteAsync("GET /api/Orders/{id}"));

        Assert.Equal("/api/Orders/{id}", row.Label);
        Assert.Equal("GET", row.Description);
        Assert.Equal(SolutionNodeKind.Route + SolutionNodeKind.SecondaryTargetSuffix, row.ContextValue);
    }

    /// <summary>
    /// The handler is not on the row, because every action of one controller carries the same type
    /// name and a column repeating it says nothing while crowding out the path. It is on the hover,
    /// with the line — which is the next question after "what does it serve".
    /// </summary>
    [Fact]
    public async Task TheHoverNamesTheHandlerAndTheLineTheRouteIsWrittenOn()
    {
        var route = await RouteAsync("GET /api/Orders/{id}");
        var row = RoutesLanguage.Node(route);

        Assert.NotNull(row.Tooltip);
        Assert.Contains("GET /api/Orders/{id}", row.Tooltip, StringComparison.Ordinal);
        Assert.Contains("OrdersController.GetOrder", row.Tooltip, StringComparison.Ordinal);

        // 1-based, the way the editor counts and the way a reader is about to type it.
        Assert.Contains(
            $"Web.cs:{route.Declaration.Start.Line + 1}", row.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARowWithNoVerbShowsThePathAlone()
    {
        var routes = await RoutesAsync();
        var row = RoutesLanguage.Node(
            routes.First(route => route.Handler.Text == "OrdersController.Search"));

        Assert.Equal("/api/Orders/search", row.Label);

        // Empty rather than "GET": an action reachable by every method is a real thing, and
        // inventing a verb for it would be a row a reader could act on wrongly.
        Assert.Null(row.Description);
    }

    /// <summary>
    /// A path nobody read is marked, and the mark is on the context value so the menu item that
    /// would copy it does not appear.
    /// </summary>
    [Fact]
    public async Task ADynamicRowIsMarkedAndSaysWhereThePathComesFrom()
    {
        var routes = await RoutesAsync();
        var row = RoutesLanguage.Node(
            routes.First(route => route.IsDynamic && route.Source == RouteSource.Registration));

        Assert.Equal("⟨path: configured⟩", row.Label);
        Assert.Equal("GET", row.Description);
        Assert.Contains(SolutionNodeKind.RouteDynamicSuffix, row.ContextValue, StringComparison.Ordinal);
    }

    /// <summary>
    /// Clicking a route opens where it is declared — where the path, the verb and the constraints
    /// are written. The handler is the other button.
    /// </summary>
    [Fact]
    public async Task ClickingARouteOpensItsDeclaration()
    {
        var route = await RouteAsync("POST /orders");
        var row = RoutesLanguage.Node(route);

        Assert.NotNull(row.GoTo);
        Assert.Equal(route.Declaration, row.GoTo.Range);
        Assert.NotNull(row.GoToSecondary);
        Assert.NotEqual(row.GoTo.Range, row.GoToSecondary.Range);
    }

    [Fact]
    public async Task ARouteWithNoHandlerToOpenIsNotOfferedTheButton()
    {
        var row = RoutesLanguage.Node(await RouteAsync("GET /health/live"));

        Assert.Null(row.GoToSecondary);
        Assert.DoesNotContain(
            SolutionNodeKind.SecondaryTargetSuffix, row.ContextValue, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two verbs over one attribute share an offset, so an id built from the offset alone makes the
    /// second row fail to render rather than merely look odd.
    /// </summary>
    [Fact]
    public async Task EveryRowHasItsOwnId()
    {
        var ids = (await RoutesAsync()).Select(route => RoutesLanguage.Node(route).Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // ---- The index ----------------------------------------------------------------------------

    [Fact]
    public async Task TheSameCompilationIsScannedOnce()
    {
        var (compilation, projectPath) = await CompilationAsync();
        var index = new RouteIndex(RoutesSettings.Default);

        Assert.Same(
            index.Of(compilation, projectPath, default),
            index.Of(compilation, projectPath, default));
    }

    /// <summary>
    /// A configured attribute is what makes an in-house routing layer visible, and it is the one
    /// thing no shipped table could have guessed.
    /// </summary>
    [Fact]
    public async Task AConfiguredAttributeIsRead()
    {
        var (compilation, projectPath) = await CompilationAsync();

        var configured = RoutesSettings.Default with
        {
            Attributes =
            [
                .. RoutePresets.Attributes,
                new RouteAttributeBinding { AttributeName = "Endpoint", Verb = "GET" },
            ],
        };

        var routes = new RouteIndex(configured).Of(compilation, projectPath, default);

        var own = Assert.Single(routes.Where(route => route.Handler.Text == "OrdersController.NotAnEndpoint"));
        Assert.Equal("GET", own.Verb);
        Assert.Equal("/api/Orders/house", own.Path.Text);
    }

    [Fact]
    public void ConfigurationWidensWhichProjectsAreLookedAt()
    {
        Assert.False(RoutesSettings.Default.IsConfigured);

        var configured = RoutesSettings.Default with
        {
            Methods =
            [
                .. RoutePresets.Methods,
                new RouteMethodBinding { MemberName = "MapEndpoint" },
            ],
        };

        Assert.True(configured.IsConfigured);
    }

    // ---- The fixture and the harness -----------------------------------------------------------

    /// <summary>
    /// The one route a row reads as, written the way somebody says it out loud.
    /// </summary>
    /// <remarks>
    /// The row itself puts the verb in its description, to the right of the path — but "GET
    /// /health" is how a test names the thing it is about, so the two halves are joined back
    /// together here rather than spelled apart at every call site.
    /// </remarks>
    private static async Task<RouteEndpoint> RouteAsync(string route)
    {
        var routes = await RoutesAsync();

        return Assert.Single(routes.Where(found => Reads(RoutesLanguage.Node(found)) == route));
    }

    private static string Reads(SolutionTreeNode row) =>
        row.Description is { Length: > 0 } verb ? $"{verb} {row.Label}" : row.Label;

    private static async Task<IReadOnlyList<RouteEndpoint>> RoutesAsync()
    {
        var (compilation, projectPath) = await CompilationAsync();

        return new RouteIndex(RoutesSettings.Default).Of(compilation, projectPath, default);
    }

    private static async Task<(Compilation Compilation, string ProjectPath)> CompilationAsync()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        const string projectPath = @"C:\src\Application.csproj";

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Default, "Application", "Application", LanguageNames.CSharp,
                filePath: projectPath,
                metadataReferences:
                [
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                ]))
            .AddDocument(
                DocumentId.CreateNewId(projectId), "Web.cs", Source, filePath: @"C:\src\Web.cs");

        var compilation = await solution.GetProject(projectId)!.GetCompilationAsync(default);

        return (compilation!, projectPath);
    }

    /// <summary>
    /// The frameworks stubbed rather than referenced, which is the point rather than a shortcut:
    /// the tables match an attribute's <em>name</em>, so a stub named the same way is claimed the
    /// same way — and that is exactly the property that makes an in-house routing layer work.
    /// </summary>
    private const string Source = """
        using System;

        namespace Microsoft.AspNetCore.Mvc
        {
            public class RouteAttribute : Attribute
            {
                public RouteAttribute(string template) { }
                public string Name { get; set; }
            }

            public class HttpGetAttribute : Attribute
            {
                public HttpGetAttribute() { }
                public HttpGetAttribute(string template) { }
                public int Order { get; set; }
            }

            public class HttpPostAttribute : Attribute
            {
                public HttpPostAttribute() { }
                public HttpPostAttribute(string template) { }
            }

            public class HttpDeleteAttribute : Attribute
            {
                public HttpDeleteAttribute() { }
                public HttpDeleteAttribute(string template) { }
            }

            public class ControllerBase { }
        }

        namespace Acme.Api
        {
            using Microsoft.AspNetCore.Mvc;

            public sealed class EndpointAttribute : Attribute
            {
                public EndpointAttribute(string template) { }
            }

            public static class Paths
            {
                public const string Reports = "api/reports";
            }

            [Route("api/[controller]")]
            public class OrdersController : ControllerBase
            {
                [HttpGet("{id}")]
                public string GetOrder(int id) => "";

                [HttpGet]
                public string ListOrders() => "";

                // A verb beside a template: one endpoint, not two.
                [HttpPost]
                [Route("{id}/cancel")]
                public void Cancel(int id) { }

                // Two verbs over one template: two endpoints sharing an offset.
                [Route("{id}/archive")]
                [HttpPost]
                [HttpDelete]
                public void Archive(int id) { }

                // Absolute, so the controller's prefix is overridden rather than joined.
                [HttpGet("~/health")]
                public string Health() => "";

                // A route with no verb: reachable by all of them.
                [Route("search")]
                public string Search() => "";

                // Claimed only once the attribute is configured.
                [Endpoint("house")]
                public string NotAnEndpoint() => "";
            }

            [Route(Paths.Reports)]
            public class ReportsController : ControllerBase
            {
                [HttpGet]
                public string Index() => "";
            }

            // No route attribute anywhere: routed by a convention this pack has not read.
            public class HomeController : ControllerBase
            {
                [HttpGet]
                public string Index() => "";
            }
        }

        namespace Microsoft.AspNetCore.Routing
        {
            public interface IEndpointRouteBuilder { }
        }

        namespace Acme.Mapping
        {
            // An object mapper, which has nothing to do with HTTP and says `Map` anyway.
            public interface IMapper
            {
                TDestination Map<TDestination>(object source);
            }
        }

        namespace Microsoft.AspNetCore.Builder
        {
            using Microsoft.AspNetCore.Routing;

            public static class EndpointRouteBuilderExtensions
            {
                public static IEndpointRouteBuilder MapGet(
                    this IEndpointRouteBuilder builder, string pattern, Delegate handler) => builder;

                public static IEndpointRouteBuilder MapPost(
                    this IEndpointRouteBuilder builder, string pattern, Delegate handler) => builder;

                public static IEndpointRouteBuilder MapGroup(
                    this IEndpointRouteBuilder builder, string prefix) => builder;

                // The overload that falls back to the pattern already in scope. Same name, no
                // pattern of its own — and nothing for a row to show.
                public static IEndpointRouteBuilder MapGet(
                    this IEndpointRouteBuilder builder, Delegate handler) => builder;
            }
        }

        namespace Acme.Api
        {
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;

            public static class Endpoints
            {
                public static void MapAll(
                    IEndpointRouteBuilder app, string configured, Acme.Mapping.IMapper mapper)
                {
                    app.MapGet("/health/live", () => "ok");
                    app.MapPost("/orders", CreateOrder);

                    var api = app.MapGroup("/api/v1");
                    api.MapGet("/orders", () => "");

                    var admin = api.MapGroup("/admin");
                    admin.MapGet("/stats", () => "");

                    app.MapGroup("/inline").MapGet("/ping", () => "");

                    // Nobody can read this one, and the row has to say so.
                    app.MapGet(configured, () => "");

                    // Neither of these serves anything.
                    var moved = mapper.Map<string>(configured);
                    app.MapGet(() => "");
                }

                public static string CreateOrder() => "";
            }
        }
        """;
}
