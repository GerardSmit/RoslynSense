using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMCP.Languages.Routes.Core;

/// <summary>Reading the name of an attribute as it is written, before anything is bound.</summary>
internal static class RouteNames
{
    private const string Suffix = "Attribute";

    /// <summary>
    /// An attribute name without the suffix C# lets a writer leave off.
    /// </summary>
    /// <remarks>
    /// <c>[HttpGet]</c> and <c>[HttpGetAttribute]</c> are the same attribute, so the table and the
    /// source are both reduced to the bare form before they are compared. Applied to the table as
    /// well as to the source, because a configured entry may be written either way and the person
    /// writing it should not have to know which.
    /// </remarks>
    public static string Bare(string name) =>
        name.EndsWith(Suffix, StringComparison.Ordinal) && name.Length > Suffix.Length
            ? name[..^Suffix.Length]
            : name;

    /// <summary>
    /// The simple name of an attribute as written, whatever it is qualified with.
    /// </summary>
    /// <remarks>
    /// <c>[Mvc.HttpGet]</c>, <c>[Microsoft.AspNetCore.Mvc.HttpGet]</c> and <c>[HttpGet]</c> are one
    /// attribute written three ways, and the rightmost identifier is what they have in common.
    /// </remarks>
    public static string Written(AttributeSyntax attribute) => attribute.Name switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax aliased => aliased.Name.Identifier.ValueText,
        _ => string.Empty,
    };
}
