using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services.Symbols;

/// <summary>
/// What a configured call shape means: a declaring type by name, and a positional signature that
/// tells one overload from another.
/// </summary>
/// <remarks>
/// <para>
/// Shared rather than owned by whoever needs it first, because two places have to agree exactly.
/// The resources pack decides at a call site whether a configured lookup binds; the settings page
/// shows the person writing that lookup which overloads it selects. If those two ever answered
/// differently, the page would be confidently wrong about the only thing it exists to say.
/// </para>
/// <para>
/// The rules themselves came from <c>Localization.GetString</c>, which has three two-argument
/// overloads — <c>(string, string)</c>, <c>(string, Control)</c> and
/// <c>(string, PortalSettings)</c> — of which only the first carries a root at index 1. Name and
/// arity bind all three and resolve garbage for two.
/// </para>
/// </remarks>
internal static class MemberSignature
{
    /// <summary>One parameter of any type.</summary>
    public const string Wildcard = "*";

    /// <summary>Fully qualified with the C# keyword for the built-ins, so a configured signature
    /// reads <c>string</c> rather than <c>System.String</c>.</summary>
    public static SymbolDisplayFormat TypeName { get; } = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>The same, with the built-ins under their framework names — <c>System.String</c>
    /// rather than <c>string</c>.</summary>
    public static SymbolDisplayFormat FrameworkTypeName { get; } = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    /// <summary>Fully qualified with the type arguments dropped, so a call through
    /// <c>IStringLocalizer&lt;Home&gt;</c> matches a shape written against
    /// <c>IStringLocalizer</c>.</summary>
    public static SymbolDisplayFormat DeclarationName { get; } = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    /// <summary>The parameters of whatever kind of member this is; empty for the kinds with none.</summary>
    public static ImmutableArray<IParameterSymbol> Parameters(ISymbol member) => member switch
    {
        IMethodSymbol method => method.Parameters,
        IPropertySymbol property => property.Parameters,
        _ => [],
    };

    /// <summary>
    /// Whether a parameter's type is the one a configured signature named, in either spelling.
    /// </summary>
    /// <remarks>
    /// <c>string</c> and <c>System.String</c> both, because the alternative is a shape that binds
    /// nothing and says nothing about why. Every other field is a name that either resolves or does
    /// not; the keyword-versus-framework spelling of a built-in is the one place where a
    /// correct-looking entry is silently inert, and which of the two a configuration reaches for is
    /// a house style rather than a statement about the code.
    /// </remarks>
    public static bool Named(ITypeSymbol type, string expected) =>
        type.ToDisplayString(TypeName).Equals(expected, StringComparison.Ordinal)
        || type.ToDisplayString(FrameworkTypeName).Equals(expected, StringComparison.Ordinal);

    /// <summary>Whether a member's parameters are positionally what the signature named.</summary>
    public static bool Matches(ISymbol member, ImmutableArray<string> expected)
    {
        var parameters = Parameters(member);

        if (parameters.Length != expected.Length)
            return false;

        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i].Equals(Wildcard, StringComparison.Ordinal))
                continue;

            if (!Named(parameters[i].Type, expected[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the type is the configured one, derives from it, or implements it.
    /// </summary>
    /// <remarks>
    /// The declaring type and not the receiver's: <c>this.LocalizeText(key)</c> in a module binds to
    /// <c>PortalModuleBase.LocalizeText</c>, which is the type the configuration names. Interfaces
    /// are walked too, since a call through <c>IStringLocalizer&lt;T&gt;</c> reaches a member
    /// declared on the non-generic one.
    /// </remarks>
    public static bool DeclaredBy(INamedTypeSymbol type, string name)
    {
        for (var candidate = type; candidate is not null; candidate = candidate.BaseType)
        {
            if (candidate.ToDisplayString(DeclarationName).Equals(name, StringComparison.Ordinal))
                return true;
        }

        foreach (var contract in type.AllInterfaces)
        {
            if (contract.ToDisplayString(DeclarationName).Equals(name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
