using System.Collections.Immutable;

namespace RoslynMCP.Languages.Routes.Core;

/// <summary>
/// The endpoint declarations this pack knows about with nothing configured.
/// </summary>
/// <remarks>
/// <para>
/// Three families, and they are not variations on each other. ASP.NET Core's attributes sit on a
/// controller action; its minimal APIs are calls on a builder; and ASP.NET Web API's attributes
/// are a third set with the same names in another namespace, which matters because a .NET
/// Framework service and a modern one turn up in the same solution more often than not.
/// </para>
/// <para>
/// Everything a solution invented for itself is configuration —
/// <see cref="RoutesSettings.Attributes"/> and <see cref="RoutesSettings.Methods"/> — because the
/// name of an in-house attribute is exactly the thing no shipped table could have guessed.
/// </para>
/// </remarks>
internal static class RoutePresets
{
    /// <summary>
    /// The types whose presence means the pack has something to do.
    /// </summary>
    /// <remarks>
    /// The MVC base classes rather than the attributes: an attribute type resolves in a project
    /// that merely references the package, while a controller base resolving means somebody wrote
    /// one.
    /// </remarks>
    public static ImmutableArray<string> WellKnownTypes { get; } =
    [
        "Microsoft.AspNetCore.Mvc.ControllerBase",
        "Microsoft.AspNetCore.Routing.IEndpointRouteBuilder",
        "System.Web.Http.ApiController",
        "System.Web.Mvc.Controller",
    ];

    /// <summary>
    /// The route attributes, ASP.NET Core's and Web API's alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RouteAttributeBinding.ContainingType"/> is left null throughout, which is the
    /// opposite of what the cron table does and is deliberate. There are four namespaces in play
    /// — <c>Microsoft.AspNetCore.Mvc</c>, <c>System.Web.Http</c>, <c>System.Web.Mvc</c> and
    /// whatever a solution's own routing layer is called — and every one of them spells the
    /// attribute <c>HttpGet</c>. Naming a type would mean four rows per verb and would still miss
    /// the fourth.
    /// </para>
    /// <para>
    /// The cost of matching on the name alone is a false positive: an attribute of the solution's
    /// own called <c>HttpGet</c> that has nothing to do with HTTP. Weighed against a section that
    /// silently omits a framework, and against what a wrong row costs here — one line in a list,
    /// linked to the code that produced it — that is the right way round.
    /// </para>
    /// </remarks>
    public static ImmutableArray<RouteAttributeBinding> Attributes { get; } =
    [
        // A template and no verb. On a type it is the prefix its actions hang off; on a method it
        // is an endpoint reachable by any verb.
        new RouteAttributeBinding { AttributeName = "Route" },

        new RouteAttributeBinding { AttributeName = "HttpGet", Verb = "GET" },
        new RouteAttributeBinding { AttributeName = "HttpPost", Verb = "POST" },
        new RouteAttributeBinding { AttributeName = "HttpPut", Verb = "PUT" },
        new RouteAttributeBinding { AttributeName = "HttpDelete", Verb = "DELETE" },
        new RouteAttributeBinding { AttributeName = "HttpPatch", Verb = "PATCH" },
        new RouteAttributeBinding { AttributeName = "HttpHead", Verb = "HEAD" },
        new RouteAttributeBinding { AttributeName = "HttpOptions", Verb = "OPTIONS" },

        // Web API 2's attribute routing, where the prefix is a separate attribute rather than a
        // Route on the type.
        new RouteAttributeBinding { AttributeName = "RoutePrefix" },
    ];

    /// <summary>
    /// The registration calls: minimal APIs, and the group that gives them a prefix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No positions are declared, which is what lets one entry cover a call written both ways.
    /// These are all extension methods, so the receiver is parameter zero of the declaration and is
    /// not written at the call — an index counted from the source would be one out for every one of
    /// them. The pattern is found as the first string parameter and the handler as the first
    /// callable one, both of which hold however the call is written.
    /// </para>
    /// <para>
    /// <c>MapMethods</c> is left out rather than mis-declared: it takes the verbs as a collection
    /// argument, and a row claiming the wrong verb is worse than a row that is missing.
    /// </para>
    /// <para>
    /// Bare <c>Map</c> is left out for a sharper version of the same reason. It is a real endpoint
    /// API — <c>app.Map(pattern, handler)</c> answers to every verb — but the name belongs to half
    /// the ecosystem: AutoMapper's and Mapster's <c>mapper.Map&lt;T&gt;(x)</c>, LINQ-ish helpers,
    /// and <c>IApplicationBuilder.Map</c>, which branches a pipeline rather than serving anything.
    /// Matching it on the name alone put a row in the list for every object mapping in the
    /// solution, and made the whole tree pay a bind for each one. A solution that really does use
    /// it names it under <c>routes.methods</c> with a <c>containingType</c>, which is precisely
    /// what configuration is for.
    /// </para>
    /// <para>
    /// <c>MapControllers()</c> is deliberately absent. It registers no endpoint of its own; it
    /// turns the attribute ones on. Listing it would put a row in the section for something that
    /// has no path, and leaving it out costs nothing — the attribute rows are found by reading the
    /// attributes, not by finding the call that activates them.
    /// </para>
    /// </remarks>
    public static ImmutableArray<RouteMethodBinding> Methods { get; } =
    [
        Map("MapGet", "GET"),
        Map("MapPost", "POST"),
        Map("MapPut", "PUT"),
        Map("MapDelete", "DELETE"),
        Map("MapPatch", "PATCH"),

        new RouteMethodBinding
        {
            MemberName = "MapGroup",
            Kind = RouteCallKind.Group,
        },
    ];

    private static RouteMethodBinding Map(string name, string? verb) => new()
    {
        MemberName = name,
        Verb = verb,
    };
}
