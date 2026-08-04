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
