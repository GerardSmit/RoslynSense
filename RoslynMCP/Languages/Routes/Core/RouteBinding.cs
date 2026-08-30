using System.Collections.Immutable;

namespace RoslynMCP.Languages.Routes.Core;

/// <summary>How an endpoint came to exist, which is also what clicking its row opens.</summary>
internal enum RouteSource
{
    /// <summary>An attribute on an action method, with the route prefix its type declares.</summary>
    Attribute,

    /// <summary>A registration call — <c>app.MapGet("/orders", …)</c> and its relatives.</summary>
    Registration,
}

/// <summary>What a registration call does to the route table.</summary>
internal enum RouteCallKind
{
    /// <summary>Declares an endpoint.</summary>
    Endpoint,

    /// <summary>
    /// Opens a group, which is a prefix every endpoint registered on the value it returns carries.
    /// Not an endpoint itself, and not a row.
    /// </summary>
    Group,
}

/// <summary>
/// One attribute whose string argument is a route template.
/// </summary>
/// <remarks>
/// <para>
/// Matched on the attribute's own name rather than on a type it must derive from, which is the
/// decision that makes the home-grown frameworks work at all. An in-house
/// <c>[Get("orders/{id}")]</c> derives from <see cref="Attribute"/> and from nothing else — there
/// is no base class to look for, and requiring one would answer "this solution serves nothing"
/// about a solution serving several hundred endpoints.
/// </para>
/// <para>
/// <see cref="ContainingType"/> is how a table stays inert where it should be. The shipped entries
/// all name the ASP.NET type, so a method of a solution's own called <c>HttpGet</c> is not claimed
/// as one; an entry a user adds usually omits it, because naming the attribute is the whole reason
/// they are adding a row.
/// </para>
/// </remarks>
internal sealed record RouteAttributeBinding
{
    /// <summary>
    /// The attribute as it is written, with or without the <c>Attribute</c> suffix.
    /// </summary>
    /// <remarks>
    /// Both spellings match. C# lets either be written and the two are the same attribute, so a
    /// table that distinguished them would claim <c>[HttpGet]</c> and miss <c>[HttpGetAttribute]</c>
    /// for no reason a reader could act on.
    /// </remarks>
    public required string AttributeName { get; init; }

    /// <summary>The attribute class's full name, or null to match the name wherever it is declared.</summary>
    public string? ContainingType { get; init; }

    /// <summary>
    /// Which constructor argument carries the template, counted from zero — or null for the first
    /// one that is a string.
    /// </summary>
    public int? PathIndex { get; init; }

    /// <summary>
    /// The HTTP method this attribute means, or null when it constrains none.
    /// </summary>
    /// <remarks>
    /// Null is <c>[Route]</c>, and it is not the same as "GET". An action reachable by any verb is
    /// a real and deliberate thing, and printing a verb it does not have would be a row a reader
    /// could act on wrongly.
    /// </remarks>
    public string? Verb { get; init; }
}

/// <summary>
/// One call whose string argument registers an endpoint.
/// </summary>
/// <remarks>
/// The same triple of member name, containing type and signature that
/// <see cref="Services.Symbols.MemberSignature"/> defines and that the cron and value-set bindings
/// are written with, so a reader who has configured one has configured this.
/// </remarks>
internal sealed record RouteMethodBinding
{
    /// <summary>The member's name.</summary>
    public required string MemberName { get; init; }

    /// <summary>The declaring type's full name, or null to match any type declaring the member.</summary>
    public string? ContainingType { get; init; }

    /// <summary>One type name per parameter, <c>*</c> for any, or null to match every overload.</summary>
    public ImmutableArray<string>? ParameterTypes { get; init; }

    /// <summary>Which parameter carries the template, counted from zero. Null means the first string.</summary>
    public int? PathIndex { get; init; }

    /// <summary>Which parameter carries what runs, when one does.</summary>
    public int? HandlerIndex { get; init; }

    /// <summary>The HTTP method, or null when the call constrains none.</summary>
    public string? Verb { get; init; }

    public RouteCallKind Kind { get; init; } = RouteCallKind.Endpoint;
}
