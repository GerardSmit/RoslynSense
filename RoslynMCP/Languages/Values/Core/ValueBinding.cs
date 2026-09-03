using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.Values.Core;

/// <summary>
/// One place in C# whose string has to be a value from a set: a member by name on a type by name,
/// resolved the same way a configured resource lookup is.
/// </summary>
/// <remarks>
/// <para>
/// What the binding <i>means</i> is decided by the member it resolves to rather than by a field
/// saying so, because the member already says it and two ways of spelling the same thing is two
/// ways of spelling it wrong:
/// </para>
/// <list type="bullet">
/// <item>a method with a <see cref="ValueIndex"/> takes the value as that argument — the site is
/// every call;</item>
/// <item>a method without one <i>returns</i> a value — the site is every literal its result is
/// compared against;</item>
/// <item>a property or a field holds a value — the site is every literal it is compared or
/// assigned.</item>
/// </list>
/// <para>
/// The same triple of type, member and signature that <see cref="Services.Symbols.MemberSignature"/>
/// already defines, so the settings page's shape editor draws this without knowing what it is for.
/// </para>
/// </remarks>
internal sealed record ValueBinding
{
    /// <summary>The <see cref="ValueSetDefinition.Id"/> this binds.</summary>
    public required string SetId { get; init; }

    /// <summary>The member's name, or <c>Item</c> for an indexer.</summary>
    public required string MemberName { get; init; }

    /// <summary>The declaring type's full name, or null to match any type declaring the member.</summary>
    public string? ContainingType { get; init; }

    /// <summary>One type name per parameter, or null to match every overload.</summary>
    public ImmutableArray<string>? ParameterTypes { get; init; }

    /// <summary>Which parameter carries the value, or null for a member that holds or returns one.</summary>
    public int? ValueIndex { get; init; }
}

/// <summary>How a literal reached its set.</summary>
internal enum ValueSiteKind
{
    /// <summary>It is an argument of a configured call.</summary>
    Argument,

    /// <summary>It is compared or assigned to a configured member.</summary>
    Compared,
}

/// <summary>
/// One string literal that a binding claims: which set it has to belong to, and what to say about
/// it.
/// </summary>
/// <param name="Span">The literal without its quotes — what completion replaces and what a
/// diagnostic underlines.</param>
/// <param name="Written">The literal's value.</param>
/// <param name="Subject">The member, as it should be named to a person.</param>
internal readonly record struct ValueSite(
    ValueBinding Binding,
    ValueSetDefinition Set,
    ValueSiteKind Kind,
    TextSpan Span,
    string Written,
    string Subject);
