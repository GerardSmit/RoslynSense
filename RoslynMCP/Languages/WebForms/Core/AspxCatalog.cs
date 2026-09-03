using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using WebFormsCore;
using WebFormsCore.Models;
using WebFormsCore.Nodes;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>A control a tag prefix makes available.</summary>
/// <param name="Prefix">The tag prefix it is registered under.</param>
/// <param name="TagName">The tag name to write.</param>
/// <param name="Type">The control class.</param>
/// <param name="SourcePath">The <c>.ascx</c> it came from, for a user control.</param>
internal sealed record AspxControlEntry(
    string Prefix, string TagName, INamedTypeSymbol Type, string? SourcePath = null);

/// <summary>
/// What is in scope in a markup file: the controls its tag prefixes reach, and the properties
/// and events of a given control. Completion's source of truth.
/// </summary>
internal static class AspxCatalog
{
    /// <summary>Namespace contents are stable for the life of a compilation, and a single
    /// namespace like <c>System.Web.UI.WebControls</c> is enumerated on nearly every keystroke.</summary>
    private static readonly ConditionalWeakTable<Compilation, ConcurrentDictionary<string, ImmutableArray<INamedTypeSymbol>>>
        s_namespaceTypes = new();

    private const string ControlBaseName = "Control";

    /// <summary>Every control the file's registered prefixes make available.</summary>
    public static IReadOnlyList<AspxControlEntry> Controls(AspxDocument document)
    {
        if (document.Tree is not { } root)
            return [];

        var entries = new List<AspxControlEntry>();
        var seen = new HashSet<(string, string)>();

        foreach (var (prefix, namespaces) in root.TagPrefixes)
        {
            foreach (string ns in namespaces)
            {
                foreach (var type in ControlTypesIn(document.Compilation, ns))
                {
                    if (seen.Add((prefix, type.Name)))
                        entries.Add(new AspxControlEntry(prefix, type.Name, type));
                }
            }
        }

        foreach (var (key, registration) in root.RegisteredControls)
        {
            if (document.Compilation.GetType(registration.Type) is not { } type)
                continue;
            if (seen.Add((key.Namespace, key.Name)))
                entries.Add(new AspxControlEntry(key.Namespace, key.Name, type, registration.Path));
        }

        return entries;
    }

    /// <summary>The control class a written tag resolves to, for a tag the parser has not
    /// produced a node for yet (which is the normal state while typing).</summary>
    public static INamedTypeSymbol? ResolveTag(AspxDocument document, string? prefix, string tagName)
    {
        if (document.Tree is not { } root || string.IsNullOrEmpty(tagName))
            return null;

        if (prefix is { Length: > 0 })
        {
            if (root.RegisteredControls.TryGetValue(new ControlKey(prefix, tagName), out var registration))
                return document.Compilation.GetType(registration.Type);

            if (root.TagPrefixes.TryGetValue(prefix, out var namespaces))
            {
                foreach (string ns in namespaces)
                {
                    if (document.Compilation.GetType(ns, tagName) is { } type)
                        return type;
                }
            }

            return null;
        }

        return document.Compilation.GetType("System.Web.UI.HtmlControls", "Html" + tagName)
            ?? document.Compilation.GetType("WebFormsCore.UI.HtmlControls", "Html" + tagName);
    }

    private static ImmutableArray<INamedTypeSymbol> ControlTypesIn(Compilation compilation, string ns)
    {
        var cache = s_namespaceTypes.GetOrCreateValue(compilation);
        if (cache.TryGetValue(ns, out var cached))
            return cached;

        var symbol = ResolveNamespace(compilation, ns);
        var types = symbol is null
            ? []
            : symbol.GetTypeMembers()
                .Where(t => t is
                {
                    TypeKind: TypeKind.Class,
                    IsAbstract: false,
                    DeclaredAccessibility: Accessibility.Public,
                })
                .Where(t => t.IsAssignableTo(ControlBaseName))
                .ToImmutableArray();

        cache[ns] = types;
        return types;
    }

    private static INamespaceSymbol? ResolveNamespace(Compilation compilation, string ns)
    {
        var current = compilation.GlobalNamespace;
        foreach (string part in ns.Split('.'))
        {
            current = current.GetNamespaceMembers()
                .FirstOrDefault(n => n.Name.Equals(part, StringComparison.OrdinalIgnoreCase))!;
            if (current is null)
                return null;
        }
        return current;
    }

    /// <summary>Settable properties, derived declarations shadowing the ones they override.</summary>
    public static IReadOnlyList<IPropertySymbol> WritableProperties(INamedTypeSymbol type)
    {
        var results = new List<IPropertySymbol>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var current in Hierarchy(type))
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer
                    || property.DeclaredAccessibility != Accessibility.Public
                    || property.SetMethod is not { DeclaredAccessibility: Accessibility.Public })
                    continue;

                if (seen.Add(property.Name))
                    results.Add(property);
            }
        }

        return results;
    }

    /// <summary>Properties whose own type is a control sub-object, so that <c>Font-Bold</c>-style
    /// dashed attributes can be offered.</summary>
    public static IReadOnlyList<IPropertySymbol> ComplexProperties(INamedTypeSymbol type)
    {
        var results = new List<IPropertySymbol>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var current in Hierarchy(type))
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer
                    || property.DeclaredAccessibility != Accessibility.Public
                    || property.Type is not INamedTypeSymbol { TypeKind: TypeKind.Class } propertyType
                    || propertyType.SpecialType == SpecialType.System_String)
                    continue;

                if (seen.Add(property.Name))
                    results.Add(property);
            }
        }

        return results;
    }

    /// <summary>
    /// Properties a <c>ParseChildren</c> control accepts as nested elements: its templates
    /// (<c>&lt;ItemTemplate&gt;</c>) and its sub-object properties (<c>&lt;Columns&gt;</c>,
    /// <c>&lt;HeaderStyle&gt;</c>). Control-typed properties are excluded — <c>Page</c> and
    /// <c>Parent</c> are class-typed on every control, and neither is ever written as markup.
    /// </summary>
    public static IReadOnlyList<IPropertySymbol> ElementProperties(INamedTypeSymbol type)
    {
        var results = new List<IPropertySymbol>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var current in Hierarchy(type))
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer
                    || property.DeclaredAccessibility != Accessibility.Public
                    || property.GetMethod is not { DeclaredAccessibility: Accessibility.Public })
                    continue;

                bool nested = property.Type.IsTemplate()
                    || (property.Type is INamedTypeSymbol
                        {
                            TypeKind: TypeKind.Class,
                            SpecialType: SpecialType.None,
                        } propertyType
                        && !propertyType.IsAssignableTo(ControlBaseName));

                if (nested && seen.Add(property.Name))
                    results.Add(property);
            }
        }

        return results;
    }

    /// <summary>The element type a collection property accepts, read from its <c>Add</c>
    /// method — what ASP.NET's object parser itself calls with each child.</summary>
    public static INamedTypeSymbol? CollectionItemType(INamedTypeSymbol collectionType)
    {
        foreach (var current in Hierarchy(collectionType))
        {
            foreach (var method in current.GetMembers("Add").OfType<IMethodSymbol>())
            {
                if (method is { IsStatic: false, DeclaredAccessibility: Accessibility.Public, Parameters.Length: 1 }
                    && method.Parameters[0].Type is INamedTypeSymbol item)
                    return item;
            }
        }

        return null;
    }

    /// <summary>
    /// Every type the file's registered prefixes reach that fits in a collection of
    /// <paramref name="itemType"/> — the <c>asp:BoundField</c>s a <c>&lt;Columns&gt;</c> accepts.
    /// Not restricted to controls: most collection items (a grid column, a list item
    /// definition) are plain objects.
    /// </summary>
    public static IReadOnlyList<AspxControlEntry> CollectionItems(
        AspxDocument document, INamedTypeSymbol itemType)
    {
        if (document.Tree is not { } root)
            return [];

        var entries = new List<AspxControlEntry>();
        var seen = new HashSet<(string, string)>();

        foreach (var (prefix, namespaces) in root.TagPrefixes)
        {
            foreach (string ns in namespaces)
            {
                if (ResolveNamespace(document.Compilation, ns) is not { } symbol)
                    continue;

                foreach (var type in symbol.GetTypeMembers())
                {
                    if (type is not
                        {
                            TypeKind: TypeKind.Class,
                            IsAbstract: false,
                            DeclaredAccessibility: Accessibility.Public,
                        })
                        continue;

                    if (!IsAssignableTo(type, itemType))
                        continue;

                    if (seen.Add((prefix, type.Name)))
                        entries.Add(new AspxControlEntry(prefix, type.Name, type));
                }
            }
        }

        return entries;
    }

    private static bool IsAssignableTo(INamedTypeSymbol type, INamedTypeSymbol target)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, target))
                return true;
        }

        return type.AllInterfaces.Contains(target, SymbolEqualityComparer.Default);
    }

    public static IReadOnlyList<IEventSymbol> Events(INamedTypeSymbol type)
    {
        var results = new List<IEventSymbol>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var current in Hierarchy(type))
        {
            foreach (var @event in current.GetMembers().OfType<IEventSymbol>())
            {
                if (@event.IsStatic || @event.DeclaredAccessibility != Accessibility.Public)
                    continue;
                if (seen.Add(@event.Name))
                    results.Add(@event);
            }
        }

        return results;
    }

    /// <summary>The event <c>[DefaultEvent]</c> names — what a double-click in the designer
    /// would have wired up.</summary>
    public static IEventSymbol? DefaultEvent(INamedTypeSymbol type)
    {
        foreach (var current in Hierarchy(type))
        {
            var attribute = current.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "DefaultEventAttribute");

            if (attribute?.ConstructorArguments is [{ Value: string name }, ..])
                return type.GetDeep<IEventSymbol>(name);
        }

        return null;
    }

    private static IEnumerable<INamedTypeSymbol> Hierarchy(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.SpecialType == SpecialType.System_Object)
                yield break;
            yield return current;
        }
    }
}
