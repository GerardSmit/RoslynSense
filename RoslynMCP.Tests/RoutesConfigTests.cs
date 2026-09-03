using System.Text.Json;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages.Routes;
using RoslynMCP.Languages.Routes.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The configuration chain behind the routes pack: what an unconfigured solution gets, what a
/// configured one adds, and what a typo costs.
/// </summary>
/// <remarks>
/// The shipped tables cover ASP.NET Core's attributes and minimal APIs and Web API's attributes,
/// which is most of what exists. What is left over is the routing layer a solution wrote for
/// itself — the <c>[Endpoint("orders")]</c> that derives from nothing — so the tests here are
/// mostly about a bad entry costing the user one binding rather than the whole section.
/// </remarks>
public class RoutesConfigTests
{
    // ---- What an unconfigured solution gets ------------------------------------------------------

    /// <summary>
    /// No <c>routes</c> section at all is not a disabled pack: a solution that never mentions
    /// RoslynSense still gets its controllers and its minimal APIs listed.
    /// </summary>
    [Fact]
    public void NothingConfiguredIsTheShippedTables()
    {
        var routes = Resolve("{}").Routes;

        Assert.True(routes.Enabled);
        Assert.Equal(ShippedAttributes, routes.Attributes.ToArray());
        Assert.Equal(ShippedMethods, routes.Methods.ToArray());
        Assert.False(routes.IsConfigured);
    }

    /// <summary>
    /// The gate the syntax scan runs off. It has to hold the shipped names before anything is
    /// configured, or the pack reads nothing at all in the common case.
    /// </summary>
    [Fact]
    public void TheShippedNamesAreTheGateStraightAway()
    {
        var routes = Resolve("{}").Routes;

        Assert.Contains("HttpGet", (IReadOnlySet<string>)routes.AttributeNames);
        Assert.Contains("Route", (IReadOnlySet<string>)routes.AttributeNames);
        Assert.Contains("MapGet", (IReadOnlySet<string>)routes.MethodNames);
        Assert.Contains("MapGroup", (IReadOnlySet<string>)routes.MethodNames);
    }

    [Fact]
    public void TheToolsGateTurnsThePackOff()
    {
        Assert.False(Resolve("""{"tools":{"routes":false}}""").Routes.Enabled);
    }

    [Fact]
    public void TheFlagTurnsThePackOff()
    {
        Assert.False(EffectiveSettings.Resolve(["--no-routes"], null, out _).Routes.Enabled);
    }

    /// <summary>A disabled pack keeps none of its tables, so nothing downstream has to check both.</summary>
    [Fact]
    public void ADisabledPackCarriesNoBindings()
    {
        var routes = Resolve(
            """{"tools":{"routes":false},"routes":{"attributes":[{"attributeName":"Endpoint"}]}}""")
            .Routes;

        Assert.Empty(routes.Attributes);
        Assert.Empty(routes.Methods);
        Assert.Empty(routes.AttributeNames);
        Assert.Empty(routes.MethodNames);
    }

    [Fact]
    public void TurningThePackOffIsNamedInTheReloadDiff()
    {
        var changes = SettingsDiff.Describe(Resolve("{}"), Resolve("""{"tools":{"routes":false}}"""));

        Assert.Contains("routes: on → off", changes);
    }

    // ---- What configuration adds -----------------------------------------------------------------

    /// <summary>
    /// Appended rather than replacing. A solution with a routing layer of its own almost always has
    /// controllers beside it, and configuring the one must not cost it the other.
    /// </summary>
    [Fact]
    public void AConfiguredAttributeIsAddedToTheShippedOnes()
    {
        var routes = Resolve(Endpoint).Routes;

        Assert.Equal(ShippedAttributes.Length + 1, routes.Attributes.Length);
        Assert.Equal(ShippedAttributes, routes.Attributes[..ShippedAttributes.Length].ToArray());

        var added = routes.Attributes[^1];
        Assert.Equal("Endpoint", added.AttributeName);
        Assert.Equal("Application.Routing.EndpointAttribute", added.ContainingType);
        Assert.Equal("GET", added.Verb);
        Assert.True(routes.IsConfigured);
    }

    /// <summary>
    /// And the name gate widens with it, which is the part a lazily-filled cache would get wrong:
    /// the settings the pack runs on are a copy of the shipped ones, and a copy that carried the
    /// shipped gate would never look at the configured attribute at all.
    /// </summary>
    [Fact]
    public void TheGateWidensWithTheConfiguredEntry()
    {
        Assert.DoesNotContain("Endpoint", (IReadOnlySet<string>)Resolve("{}").Routes.AttributeNames);
        Assert.Contains("Endpoint", (IReadOnlySet<string>)Resolve(Endpoint).Routes.AttributeNames);
    }

    /// <summary>Both spellings mean the same attribute, and the gate holds the bare one.</summary>
    [Fact]
    public void TheAttributeSuffixIsOptional()
    {
        var routes = Resolve("""{"routes":{"attributes":[{"attributeName":"EndpointAttribute"}]}}""")
            .Routes;

        Assert.Contains("Endpoint", (IReadOnlySet<string>)routes.AttributeNames);
    }

    [Fact]
    public void AConfiguredMethodIsAddedToTheShippedOnes()
    {
        var routes = Resolve("""
            {"routes":{"methods":[{
                "memberName": "MapEndpoint",
                "pathIndex": 1,
                "handlerIndex": 2,
                "verb": "post",
                "kind": "endpoint"
            }]}}
            """).Routes;

        var added = routes.Methods[^1];
        Assert.Equal("MapEndpoint", added.MemberName);
        Assert.Equal(1, added.PathIndex);
        Assert.Equal(2, added.HandlerIndex);
        Assert.Equal("POST", added.Verb);
        Assert.Equal(RouteCallKind.Endpoint, added.Kind);
        Assert.Contains("MapEndpoint", (IReadOnlySet<string>)routes.MethodNames);
    }

    /// <summary>
    /// A group opens a prefix rather than serving anything, and saying so is the only way an
    /// in-house one can be told from an endpoint whose path happens to be short.
    /// </summary>
    [Fact]
    public void AGroupIsReadFromTheEntry()
    {
        var routes = Resolve(
            """{"routes":{"methods":[{"memberName":"MapArea","kind":"group"}]}}""").Routes;

        Assert.Equal(RouteCallKind.Group, routes.Methods[^1].Kind);
    }

    /// <summary>
    /// A verb nobody standardised is still a verb a service answers to, so it is upper-cased and
    /// kept rather than checked against a list.
    /// </summary>
    [Fact]
    public void AVerbIsUpperCasedAndNotSecondGuessed()
    {
        var routes = Resolve(
            """{"routes":{"attributes":[{"attributeName":"Propfind","verb":"propfind"}]}}""",
            out var warnings).Routes;

        Assert.Equal("PROPFIND", routes.Attributes[^1].Verb);
        Assert.Empty(warnings);
    }

    // ---- What a typo costs -----------------------------------------------------------------------

    [Fact]
    public void AnAttributeWithNoNameIsDroppedWithAWarning()
    {
        var routes = Resolve(
            """{"routes":{"attributes":[{"verb":"GET"}]}}""", out var warnings).Routes;

        Assert.Equal(ShippedAttributes, routes.Attributes.ToArray());
        Assert.Contains(warnings, w => w.Contains("no attributeName", StringComparison.Ordinal));
    }

    [Fact]
    public void AMethodWithNoNameIsDroppedWithAWarning()
    {
        var routes = Resolve(
            """{"routes":{"methods":[{"pathIndex":0}]}}""", out var warnings).Routes;

        Assert.Equal(ShippedMethods, routes.Methods.ToArray());
        Assert.Contains(warnings, w => w.Contains("no memberName", StringComparison.Ordinal));
    }

    /// <summary>
    /// A negative index is not an argument position, and the honest recovery is the rule that works
    /// without one — the first string — rather than a binding that silently reads argument zero.
    /// </summary>
    [Fact]
    public void AnIndexThatIsNotAnArgumentPositionWarnsAndFallsBackToTheType()
    {
        var routes = Resolve(
            """{"routes":{"attributes":[{"attributeName":"Endpoint","pathIndex":-1}]}}""",
            out var warnings).Routes;

        Assert.Null(routes.Attributes[^1].PathIndex);
        Assert.Contains(warnings, w => w.Contains("pathIndex -1", StringComparison.Ordinal));
    }

    /// <summary>
    /// A verb with a space in it is a sentence rather than a method, and taking it at its word
    /// would put a row in the list that no request can ever match.
    /// </summary>
    [Fact]
    public void AVerbThatIsNotOneWarnsAndConstrainsNothing()
    {
        var routes = Resolve(
            """{"routes":{"attributes":[{"attributeName":"Endpoint","verb":"GET or POST"}]}}""",
            out var warnings).Routes;

        Assert.Null(routes.Attributes[^1].Verb);
        Assert.Contains(warnings, w => w.Contains("GET or POST", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownKindWarnsAndReadsAnEndpoint()
    {
        var routes = Resolve(
            """{"routes":{"methods":[{"memberName":"MapArea","kind":"prefix"}]}}""",
            out var warnings).Routes;

        Assert.Equal(RouteCallKind.Endpoint, routes.Methods[^1].Kind);
        Assert.Contains(warnings, w => w.Contains("kind 'prefix'", StringComparison.Ordinal));
    }

    // ---- All the way through to a row --------------------------------------------------------------

    /// <summary>
    /// The point of the whole chain: a line of JSON reaches an attribute of the solution's own,
    /// deriving from nothing and referencing no web framework at all.
    /// </summary>
    [Fact]
    public async Task AConfiguredAttributeReachesTheIndex()
    {
        Assert.Empty(await RoutesAsync(Resolve("{}").Routes));

        var found = Assert.Single(await RoutesAsync(Resolve(Endpoint).Routes));

        var row = RoutesLanguage.Node(found);

        Assert.Equal("/orders", row.Label);
        Assert.Equal("GET", row.Description);
        Assert.Equal("Orders.List", found.Handler.Text);
    }

    // ---- The fixture and the harness --------------------------------------------------------------

    /// <summary>
    /// The shipped tables as plain arrays. <c>ImmutableArray&lt;T&gt;</c> compares by the identity
    /// of the array underneath it, so two equal tables are unequal to an equality assertion — and
    /// the failure it prints is two identical-looking lists, which is a bad half hour.
    /// </summary>
    private static readonly RouteAttributeBinding[] ShippedAttributes = [.. RoutePresets.Attributes];

    private static readonly RouteMethodBinding[] ShippedMethods = [.. RoutePresets.Methods];

    private const string Endpoint = """
        {"routes":{"attributes":[{
            "attributeName": "Endpoint",
            "containingType": "Application.Routing.EndpointAttribute",
            "verb": "GET"
        }]}}
        """;

    /// <summary>
    /// A routing layer with no framework behind it and no name the pack could have guessed — the
    /// only shape configuration exists for.
    /// </summary>
    private const string Source = """
        using System;

        namespace Application.Routing
        {
            public sealed class EndpointAttribute : Attribute
            {
                public EndpointAttribute(string path) { }
            }
        }

        namespace Application
        {
            using Application.Routing;

            public sealed class Orders
            {
                [Endpoint("/orders")]
                public string List() => "";
            }
        }
        """;

    private static async Task<IReadOnlyList<RouteEndpoint>> RoutesAsync(RoutesSettings settings)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        const string projectPath = @"C:\src\Application.csproj";

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Default, "Application", "Application", LanguageNames.CSharp,
                filePath: projectPath,
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(
                DocumentId.CreateNewId(projectId), "Orders.cs", Source, filePath: @"C:\src\Orders.cs");

        var compilation = await solution.GetProject(projectId)!.GetCompilationAsync(default);

        return new RouteIndex(settings).Of(compilation!, projectPath, default);
    }

    private static EffectiveSettings Resolve(string json) => Resolve(json, out _);

    private static EffectiveSettings Resolve(string json, out List<string> warnings) =>
        EffectiveSettings.Resolve(
            [],
            JsonSerializer.Deserialize<RoslynSenseConfig>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                }),
            out warnings);
}
