using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services;

/// <summary>
/// Shared symbol formatting utilities used across multiple tools.
/// Consolidates XML doc extraction and symbol metadata formatting.
/// </summary>
internal static partial class SymbolFormatter
{
    /// <summary>
    /// Appends standard symbol metadata lines to a StringBuilder.
    /// </summary>
    public static void AppendSymbolInfo(StringBuilder sb, ISymbol symbol)
    {
        sb.AppendLine($"- **Symbol**: {symbol.ToDisplayString()}");
        sb.AppendLine($"- **Kind**: {symbol.Kind}");

        if (symbol.ContainingType is not null)
            sb.AppendLine($"- **Containing Type**: {symbol.ContainingType.ToDisplayString()}");

        if (symbol.ContainingNamespace is { IsGlobalNamespace: false })
            sb.AppendLine($"- **Namespace**: {symbol.ContainingNamespace.ToDisplayString()}");
    }

    /// <summary>
    /// Appends XML documentation (summary, returns, remarks, parameters) to a StringBuilder.
    /// </summary>
    public static void AppendXmlDocs(StringBuilder sb, ISymbol symbol)
    {
        var xmlDoc = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xmlDoc))
            return;

        var summary = ExtractXmlDocSection(xmlDoc, "summary");
        if (!string.IsNullOrWhiteSpace(summary))
            sb.AppendLine($"- **Summary**: {summary.Trim()}");

        var returns = ExtractXmlDocSection(xmlDoc, "returns");
        if (!string.IsNullOrWhiteSpace(returns))
            sb.AppendLine($"- **Returns**: {returns.Trim()}");

        var remarks = ExtractXmlDocSection(xmlDoc, "remarks");
        if (!string.IsNullOrWhiteSpace(remarks))
            sb.AppendLine($"- **Remarks**: {remarks.Trim()}");

        if (symbol is IMethodSymbol method && method.Parameters.Length > 0)
        {
            var paramDocs = ExtractXmlDocParams(xmlDoc);
            if (paramDocs.Count > 0)
            {
                sb.AppendLine("- **Parameters**:");
                foreach (var (name, desc) in paramDocs)
                    sb.AppendLine($"  - `{name}`: {desc.Trim()}");
            }
        }
    }

    /// <summary>
    /// Extracts a named section from XML documentation comments, rendered as markdown prose:
    /// &lt;see cref="..."/&gt; and the other reference tags become code spans naming the symbol
    /// the way it is written in C#.
    /// </summary>
    /// <param name="compilation">
    /// The compilation the crefs are resolved against. Without one a cref can only be cleaned up
    /// textually, which gets the name right but not its type parameters.
    /// </param>
    internal static string? ExtractXmlDocSection(
        string xmlDoc, string sectionName, Compilation? compilation = null)
    {
        var match = Regex.Match(
            xmlDoc, $@"<{sectionName}>(.*?)</{sectionName}>",
            RegexOptions.Singleline);
        if (!match.Success) return null;

        return RenderDocFragment(match.Groups[1].Value, compilation);
    }

    /// <summary>
    /// Extracts parameter documentation from XML docs.
    /// </summary>
    internal static List<(string Name, string Description)> ExtractXmlDocParams(
        string xmlDoc, Compilation? compilation = null)
    {
        var results = new List<(string, string)>();
        var matches = ParamDocRegex().Matches(xmlDoc);

        foreach (Match match in matches)
            results.Add((match.Groups[1].Value, RenderDocFragment(match.Groups[2].Value, compilation)));

        return results;
    }

    /// <summary>
    /// The body of one documentation section as a single markdown paragraph.
    /// </summary>
    /// <remarks>
    /// Parsed rather than pattern-matched, because the tags nest and their order matters: a cref
    /// renders to <c>Dictionary&lt;TKey, TValue&gt;</c>, and stripping "the remaining tags" after
    /// that would eat the type argument list. The doc comment Roslyn hands back is well-formed
    /// XML; anything else — a hand-written comment the compiler already warned about — falls back
    /// to the textual clean-up, which cannot go wrong on malformed input because it never parses.
    /// </remarks>
    private static string RenderDocFragment(string inner, Compilation? compilation)
    {
        try
        {
            var root = XElement.Parse($"<doc>{inner}</doc>", LoadOptions.PreserveWhitespace);
            var sb = new StringBuilder();
            RenderDocNodes(root, sb, compilation);
            return WhitespaceRunRegex().Replace(sb.ToString(), " ").Trim();
        }
        catch (XmlException)
        {
            var text = SeeCrefRegex().Replace(inner, m => CodeSpan(CrefText(m.Groups[1].Value, compilation)));
            text = ParamrefRegex().Replace(text, "`$1`");
            text = XmlTagRegex().Replace(text, "");
            return WhitespaceRunRegex().Replace(text, " ").Trim();
        }
    }

    private static void RenderDocNodes(XElement parent, StringBuilder sb, Compilation? compilation)
    {
        foreach (var node in parent.Nodes())
        {
            switch (node)
            {
                case XText text:
                    sb.Append(text.Value);
                    break;

                case XElement element:
                    RenderDocElement(element, sb, compilation);
                    break;
            }
        }
    }

    private static void RenderDocElement(XElement element, StringBuilder sb, Compilation? compilation)
    {
        switch (element.Name.LocalName)
        {
            case "see" or "seealso":
                RenderReference(element, sb, compilation);
                break;

            // A name, not prose: the reader is looking for it in the signature above.
            case "paramref" or "typeparamref":
                if (element.Attribute("name")?.Value is { Length: > 0 } name)
                    sb.Append(CodeSpan(name));
                break;

            case "c" or "code":
                if (element.Value is { Length: > 0 } code)
                    sb.Append(CodeSpan(code));
                break;

            // Structure the paragraph does not have room for; keep the words, drop the layout.
            case "para" or "list" or "item" or "description" or "term":
                RenderDocNodes(element, sb, compilation);
                sb.Append(' ');
                break;

            default:
                RenderDocNodes(element, sb, compilation);
                break;
        }
    }

    /// <summary>
    /// A <c>&lt;see&gt;</c> or <c>&lt;seealso&gt;</c>: what it points at, named the way the author
    /// asked for.
    /// </summary>
    /// <remarks>
    /// Three targets, and each says where the words come from. <c>href</c> is a URL, so it becomes
    /// a link rather than a code span. <c>langword</c> is a keyword — <c>null</c>, <c>true</c> —
    /// and always reads as code. <c>cref</c> names a symbol, but text written between the tags is
    /// the author's own wording for it and wins over the name we would derive: an
    /// <c>&lt;see cref="T:System.String"&gt;the name&lt;/see&gt;</c> means to say "the name", not
    /// "<c>string</c>".
    /// </remarks>
    private static void RenderReference(XElement element, StringBuilder sb, Compilation? compilation)
    {
        string label = WhitespaceRunRegex().Replace(element.Value, " ").Trim();

        if (element.Attribute("href")?.Value is { Length: > 0 } href)
        {
            sb.Append(label.Length > 0 ? $"[{label}]({href})" : href);
            return;
        }

        if (element.Attribute("cref")?.Value is { Length: > 0 } cref)
        {
            sb.Append(label.Length > 0 ? label : CodeSpan(CrefText(cref, compilation)));
            return;
        }

        if (element.Attribute("langword")?.Value is { Length: > 0 } langword)
        {
            sb.Append(CodeSpan(label.Length > 0 ? label : langword));
            return;
        }

        // No attribute at all is not valid documentation, but the words inside it are still words.
        sb.Append(label);
    }

    /// <summary>How a cref is named in the middle of a sentence: short, with its type parameters,
    /// the way it would be written in code.</summary>
    private static readonly SymbolDisplayFormat s_crefFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>
    /// A documentation comment id written as the symbol it points at.
    /// </summary>
    /// <remarks>
    /// The id is metadata spelling — <c>T:System.Collections.Generic.Dictionary`2</c> — and
    /// printing it verbatim is what put a bare <c>T:</c> and a stray backtick in the tooltip.
    /// Resolving it gives the declared type parameter names back; when it does not resolve (an
    /// unresolved <c>!:</c> cref, or a symbol from another compilation) the id is trimmed down to
    /// the same shape by hand.
    /// </remarks>
    private static string CrefText(string cref, Compilation? compilation)
    {
        if (compilation is not null
            && DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, compilation) is { } symbol)
            return symbol.ToDisplayString(s_crefFormat);

        return ShortenCrefId(cref);
    }

    private static string ShortenCrefId(string cref)
    {
        // "T:", "M:", "!:" — the kind prefix says nothing a reader of prose wants.
        string id = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        char kind = cref.Length > 2 && cref[1] == ':' ? cref[0] : 'T';

        // Arity markers are metadata bookkeeping, and without a symbol there are no names to
        // put in their place.
        id = GenericArityRegex().Replace(id, "");

        // A namespace is its full name or it is not that namespace; a type needs only its own
        // name; everything else is a member, which keeps the type it is declared on.
        if (kind == 'N')
            return id;

        int cut = id.IndexOf('(');
        string qualified = cut < 0 ? id : id[..cut];
        var segments = qualified.Split('.');
        int keep = kind == 'T' ? 1 : 2;
        return string.Join('.', segments.Skip(Math.Max(0, segments.Length - keep)));
    }

    /// <summary>A code span whose content cannot break out of it, however many backticks the
    /// documentation put inside.</summary>
    private static string CodeSpan(string text)
    {
        string content = WhitespaceRunRegex().Replace(text, " ").Trim();
        if (content.Length == 0)
            return "";

        // One more backtick than the longest run inside, per the markdown rules for code spans.
        int longest = 0, run = 0;
        foreach (char c in content)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        string fence = new('`', longest + 1);
        return longest == 0 ? $"{fence}{content}{fence}" : $"{fence} {content} {fence}";
    }

    [GeneratedRegex(@"<see\s+cref=""([^""]*)""\s*/>")]
    private static partial Regex SeeCrefRegex();

    [GeneratedRegex(@"<paramref\s+name=""([^""]*)""\s*/>")]
    private static partial Regex ParamrefRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex XmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunRegex();

    [GeneratedRegex(@"`\d+")]
    private static partial Regex GenericArityRegex();

    [GeneratedRegex(@"<param\s+name=""([^""]+)"">(.*?)</param>", RegexOptions.Singleline)]
    private static partial Regex ParamDocRegex();

    /// <summary>
    /// Determines whether a type member should be displayed in outlines and member tables.
    /// Filters out compiler-generated, backing fields, accessor methods, nested types, etc.
    /// </summary>
    public static bool ShouldDisplayMember(ISymbol member)
    {
        if (member.IsImplicitlyDeclared)
            return false;

        if (member is IFieldSymbol { AssociatedSymbol: not null })
            return false;

        if (member is IMethodSymbol method)
        {
            if (method.AssociatedSymbol is not null)
                return false;
            if (method.MethodKind is MethodKind.StaticConstructor or MethodKind.Destructor)
                return false;
        }

        if (member is INamedTypeSymbol)
            return false;

        return member is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol;
    }

    /// <summary>
    /// Returns a sort order value for grouping members by kind (ctors first, then fields, properties, events, methods).
    /// </summary>
    public static int MemberSortOrder(ISymbol member) => member switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor } => 0,
        IFieldSymbol => 1,
        IPropertySymbol => 2,
        IEventSymbol => 3,
        IMethodSymbol => 4,
        _ => 5
    };
}
