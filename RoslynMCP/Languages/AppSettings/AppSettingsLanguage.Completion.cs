using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.AppSettings.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.MetadataConfiguration;

namespace RoslynMCP.Languages.AppSettings;

internal sealed partial class AppSettingsLanguage : ILanguageCompletionProvider
{
    private const int KindProperty = 10;
    private const int KindValue = 12;
    private const int KindModule = 9;

    /// <summary>
    /// What can be typed where the caret is, learned from the C#: inside a section the code binds
    /// to an options type, the type's properties; at the top level, the sections the code asks
    /// for; after a colon, the values a bound property's type can hold.
    /// </summary>
    /// <remarks>
    /// Items are self-contained — no resolve round trip — per the documentless-request contract
    /// the other packs follow.
    /// </remarks>
    public async Task<CompletionList> CompletionAsync(
        CompletionParams p, LspResolveCache cache, CancellationToken ct)
    {
        if (await AppSettingsWorkspace.GetAsync(
                LspConverters.UriToPath(p.TextDocument.Uri), ct) is not { } view)
        {
            return new CompletionList(false, []);
        }

        int offset = LspConverters.ToOffset(view.Text, p.Position);
        string text = view.Text.ToString();

        if (Site(text, offset) is not { } site)
            return new CompletionList(false, []);

        string sectionPath = view.Document.EnclosingAt(offset)?.Path ?? "";

        var external = view.Project is { } project
            ? await MetadataConfigurationIndex.GetAsync(project, ct)
            : MetadataConfigurationIndex.Empty;

        var items = site.IsName
            ? NameItems(view, external, sectionPath)
            : ValueItems(view, text, offset, sectionPath);

        if (items.Count == 0)
            return new CompletionList(false, []);

        var range = LspConverters.ToRange(view.Text.Lines, site.ReplaceSpan);

        return new CompletionList(false,
        [
            .. items.Select((item, index) => new CompletionItem(
                item.Label, item.Kind, item.Detail,
                SortText: index.ToString("D3"),
                FilterText: item.Label,
                TextEdit: new TextEdit(range, item.Label))),
        ]);
    }

    public Task<CompletionItem> ResolveCompletionAsync(
        CompletionItem item, LspResolveCache cache, CancellationToken ct) =>
        Task.FromResult(item);

    private readonly record struct Item(string Label, int Kind, string? Detail);

    /// <summary>
    /// The names that belong in a section: the bound type's settable properties when the section
    /// is bound, and otherwise — at the top level — every section the code actually asks for.
    /// Keys already present are left out; offering what is already there is how a list gets
    /// ignored.
    /// </summary>
    private static IReadOnlyList<Item> NameItems(
        AppSettingsView view, MetadataConfigurationIndex external, string sectionPath)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in view.Document.Keys)
        {
            if (Parent(key.Path).Equals(sectionPath, StringComparison.OrdinalIgnoreCase))
                existing.Add(key.Name);
        }

        if (view.Index.BoundType(sectionPath) is { } bound)
        {
            return
            [
                .. BindableProperties(bound)
                    .Where(property => !existing.Contains(property.Name))
                    .Select(property => new Item(
                        property.Name,
                        property.Type is INamedTypeSymbol { TypeKind: TypeKind.Class }
                            && property.Type.SpecialType == SpecialType.None
                            ? KindModule
                            : KindProperty,
                        property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))),
            ];
        }

        // Every path the application reads under this section, cut to the one segment that comes
        // next. At the top level that is the sections themselves; inside an unbound section it is
        // the keys of it — the same question asked one level down, and the level the answer used
        // to stop at.
        var segments = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Offer(string path, string detail)
        {
            if (NextSegment(path, sectionPath) is { Length: > 0 } segment
                && !existing.Contains(segment))
            {
                segments.TryAdd(segment, detail);
            }
        }

        foreach (var usage in view.Index.Usages)
            Offer(usage.Path, "read by this solution");

        foreach (var binding in view.Index.Bindings)
            Offer(binding.SectionPath, "read by this solution");

        // Sections a referenced package reads inside itself — Kestrel, Logging, Authentication.
        // The solution's own source never names them, so without this they are the sections you
        // have to already know about to write down.
        foreach (var read in external.ReadsOf(MetadataConfigurationKind.Path))
            Offer(read.Name, "read by " + read.AssemblyName);

        return [.. segments.Select(segment => new Item(segment.Key, KindModule, segment.Value))];
    }

    /// <summary>
    /// The one segment a path contributes directly under a section, or null when the path lies
    /// somewhere else entirely. The section itself is not a suggestion for its own inside.
    /// </summary>
    internal static string? NextSegment(string path, string sectionPath)
    {
        if (sectionPath.Length == 0)
            return FirstSegment(path);

        if (path.Length <= sectionPath.Length + 1
            || !path.StartsWith(sectionPath, StringComparison.OrdinalIgnoreCase)
            || path[sectionPath.Length] != ':')
        {
            return null;
        }

        return FirstSegment(path[(sectionPath.Length + 1)..]);
    }

    /// <summary>The values a bound property's type admits — booleans and enum members. Other
    /// types accept prose, and a list that cannot enumerate honestly should stay closed.</summary>
    private static IReadOnlyList<Item> ValueItems(
        AppSettingsView view, string text, int offset, string enclosingPath)
    {
        if (PropertyNameBefore(text, offset) is not { Length: > 0 } name)
            return [];

        string path = enclosingPath.Length == 0 ? name : enclosingPath + ":" + name;

        if (view.Index.BoundProperty(path) is not { } property)
            return [];

        if (property.Type.SpecialType == SpecialType.System_Boolean
            || property.Type is INamedTypeSymbol
            {
                Name: "Nullable", TypeArguments: [{ SpecialType: SpecialType.System_Boolean }],
            })
        {
            return
            [
                new Item("true", KindValue, null),
                new Item("false", KindValue, null),
            ];
        }

        var enumType = property.Type as INamedTypeSymbol;
        if (enumType is { Name: "Nullable", TypeArguments: [INamedTypeSymbol inner] })
            enumType = inner;

        if (enumType is not { TypeKind: TypeKind.Enum })
            return [];

        return
        [
            .. enumType.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(field => field.HasConstantValue)
                .Select(field => new Item(field.Name, KindValue, enumType.Name)),
        ];
    }

    /// <summary>Public settable instance properties, walking base types — what the binder sets.</summary>
    private static IEnumerable<IPropertySymbol> BindableProperties(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.SpecialType != SpecialType.None)
                yield break;

            foreach (var member in current.GetMembers())
            {
                if (member is IPropertySymbol
                    {
                        DeclaredAccessibility: Accessibility.Public,
                        IsStatic: false,
                        SetMethod: not null,
                    } property && seen.Add(property.Name))
                {
                    yield return property;
                }
            }
        }
    }

    private static string Parent(string path)
    {
        int colon = path.LastIndexOf(':');
        return colon < 0 ? "" : path[..colon];
    }

    private static string FirstSegment(string path)
    {
        int colon = path.IndexOf(':');
        return colon < 0 ? path : path[..colon];
    }

    private readonly record struct CompletionSite(bool IsName, TextSpan ReplaceSpan);

    /// <summary>
    /// Where the caret is, read straight from the characters: inside a string whose preceding
    /// significant character is <c>{</c> or <c>,</c> it is a property name; inside one preceded
    /// by <c>:</c> it is a value. Character-level rather than reader-level because the property
    /// being completed is exactly the one too half-typed for the reader to keep.
    /// </summary>
    private static CompletionSite? Site(string text, int offset)
    {
        if (offset > text.Length)
            return null;

        // The opening quote of the string the caret is in, if any: walk back to the line start
        // counting quotes — an odd count means the caret is inside the last one opened.
        int lineStart = offset;
        while (lineStart > 0 && text[lineStart - 1] is not ('\n' or '\r'))
            lineStart--;

        int open = -1;
        for (int i = lineStart; i < offset; i++)
        {
            if (text[i] == '"' && (i == 0 || text[i - 1] != '\\'))
                open = open < 0 ? i : -1;
        }

        if (open < 0)
        {
            // Not inside a string. A bare word after a colon is still a value being typed —
            // true, false and enum names are legal unquoted or about to be quoted.
            int wordStart = offset;
            while (wordStart > lineStart && char.IsLetter(text[wordStart - 1]))
                wordStart--;

            int significant = wordStart - 1;
            while (significant >= 0 && char.IsWhiteSpace(text[significant]))
                significant--;

            if (significant < 0 || text[significant] != ':')
                return null;

            int wordEnd = offset;
            while (wordEnd < text.Length && char.IsLetter(text[wordEnd]))
                wordEnd++;

            return new CompletionSite(IsName: false, TextSpan.FromBounds(wordStart, wordEnd));
        }

        // What the string is for is decided by the last significant character before it.
        int before = open - 1;
        while (before >= 0 && char.IsWhiteSpace(text[before]))
            before--;

        bool isName = before < 0 || text[before] is '{' or ',';
        bool isValue = before >= 0 && text[before] == ':';

        if (!isName && !isValue)
            return null;

        int end = offset;
        while (end < text.Length && text[end] is not ('"' or '\n' or '\r'))
            end++;

        return new CompletionSite(isName, TextSpan.FromBounds(open + 1, end));
    }

    /// <summary>The name of the property whose value the caret is in: the string before the
    /// nearest <c>:</c> at or before the offset.</summary>
    private static string? PropertyNameBefore(string text, int offset)
    {
        int colon = text.LastIndexOf(':', Math.Min(offset, text.Length) - 1);
        if (colon <= 0)
            return null;

        int close = colon - 1;
        while (close >= 0 && char.IsWhiteSpace(text[close]))
            close--;

        if (close < 0 || text[close] != '"')
            return null;

        int open = close - 1;
        while (open >= 0 && text[open] != '"')
            open--;

        return open >= 0 ? text[(open + 1)..close] : null;
    }
}
