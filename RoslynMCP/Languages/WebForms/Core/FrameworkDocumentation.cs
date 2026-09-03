using System.Text;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>
/// What a framework control property or event is for, when the framework shipped no XML docs for
/// it. <c>System.Web</c> describes every one of them with <c>[WebSysDescription]</c> and files it
/// under a <c>[WebCategory]</c>, whose arguments are keys into the assembly's own string table
/// (<see cref="MetadataResources"/>) rather than the text — a shape Roslyn, which reads XML
/// documentation and nothing else, has no answer for, and the reason hovering
/// <c>&lt;asp:Button Text="…"&gt;</c> says nothing.
/// </summary>
/// <remarks>
/// Attributes are matched by shape rather than by name: the same convention is spelled
/// <c>SRDescription</c> in System.Design, <c>ResDescription</c> elsewhere, and whatever a control
/// vendor chose in their own library. What they share is one string argument that names a resource
/// of the assembly applying it, which is also where <see cref="MetadataAttributes"/> reads it from.
/// </remarks>
internal static class FrameworkDocumentation
{
    /// <summary>Both the name System.ComponentModel's own attribute goes by and the suffix every
    /// framework variant of it ends in.</summary>
    private const string DescriptionAttributeName = "DescriptionAttribute";

    private const string CategoryAttributeName = "CategoryAttribute";

    /// <summary>The two whose argument is the text itself rather than a key to it.</summary>
    private const string ComponentModelDescription = "System.ComponentModel." + DescriptionAttributeName;

    private const string ComponentModelCategory = "System.ComponentModel." + CategoryAttributeName;

    /// <summary>How <c>WebCategoryAttribute</c> spells its own argument when it looks it up.</summary>
    private const string CategoryKeyPrefix = "Category_";

    /// <summary>
    /// Markdown describing the property or event, or <c>null</c> when nothing in the metadata does.
    /// Walks the override chain, because a property that overrides a documented one carries the
    /// attribute only where the text was written.
    /// </summary>
    public static string? Describe(ISymbol symbol, Compilation compilation)
    {
        for (var current = symbol; current is not null; current = Overridden(current))
        {
            if (DescribeDeclaration(current, compilation) is { } markdown)
                return markdown;
        }

        return null;
    }

    private static ISymbol? Overridden(ISymbol symbol) => symbol switch
    {
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null,
    };

    private static string? DescribeDeclaration(ISymbol symbol, Compilation compilation)
    {
        if (symbol is not (IPropertySymbol or IEventSymbol)
            || symbol.ContainingType is not { } declaringType
            || AssemblyPath(symbol.ContainingAssembly, compilation) is not { } assemblyPath)
            return null;

        string? summary = null;
        string? category = null;

        foreach (var (attributeName, argument) in MetadataAttributes.StringArguments(
                     assemblyPath, MetadataName(declaringType), symbol.MetadataName,
                     symbol.Kind == SymbolKind.Event))
        {
            if (attributeName.EndsWith(DescriptionAttributeName, StringComparison.Ordinal))
                summary ??= Description(attributeName, argument, assemblyPath);
            else if (attributeName.EndsWith(CategoryAttributeName, StringComparison.Ordinal))
                category ??= Category(attributeName, argument, assemblyPath);
        }

        if (summary is null)
            return null;

        // A category on its own is a filing label rather than documentation, and earns a line only
        // beside the text it files.
        return category is null ? summary : $"{summary}\n\n**Category:** {category}";
    }

    /// <summary>
    /// An unresolved key is a key rather than a sentence, and <c>Button_Text</c> in a tooltip is
    /// worse than an empty one — so only the attribute that carries its own text falls back to it.
    /// </summary>
    private static string? Description(string attributeName, string argument, string assemblyPath) =>
        attributeName == ComponentModelDescription
            ? argument
            : MetadataResources.Lookup(assemblyPath, argument);

    /// <summary>
    /// <c>WebCategoryAttribute</c> looks its argument up as <c>Category_Appearance</c> and falls
    /// back to the argument when that misses, which is most of the time: System.Web translated
    /// only the categories it invented. Other libraries' variants pass a whole key.
    /// </summary>
    private static string Category(string attributeName, string argument, string assemblyPath) =>
        attributeName == ComponentModelCategory
            ? argument
            : MetadataResources.Lookup(assemblyPath, CategoryKeyPrefix + argument)
                ?? MetadataResources.Lookup(assemblyPath, argument)
                ?? argument;

    /// <summary>The name the type goes by in metadata — <c>Ns.Outer+Inner</c>, arity and all.</summary>
    private static string MetadataName(INamedTypeSymbol type)
    {
        var name = new StringBuilder(type.MetadataName);

        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            name.Insert(0, outer.MetadataName + "+");

        if (type.ContainingNamespace is { IsGlobalNamespace: false } ns)
            name.Insert(0, ns.ToDisplayString() + ".");

        return name.ToString();
    }

    private static string? AssemblyPath(IAssemblySymbol? assembly, Compilation compilation) =>
        assembly is not null
        && compilation.GetMetadataReference(assembly)
            is PortableExecutableReference { FilePath: { Length: > 0 } path }
            ? path
            : null;
}
