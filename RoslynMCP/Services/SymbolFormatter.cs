using System.Text;
using System.Text.RegularExpressions;
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
    /// Extracts a named section from XML documentation comments.
    /// Cleans up &lt;see cref="..."/&gt; and &lt;paramref name="..."/&gt; tags.
    /// </summary>
    internal static string? ExtractXmlDocSection(string xmlDoc, string sectionName)
    {
        var match = Regex.Match(
            xmlDoc, $@"<{sectionName}>(.*?)</{sectionName}>",
            RegexOptions.Singleline);
        if (!match.Success) return null;

        var text = match.Groups[1].Value;
        text = SeeCrefRegex().Replace(text, "$1");
        text = ParamrefRegex().Replace(text, "`$1`");
        text = XmlTagRegex().Replace(text, "");
        text = WhitespaceRunRegex().Replace(text, " ");
        return text.Trim();
    }

    /// <summary>
    /// Extracts parameter documentation from XML docs.
    /// </summary>
    internal static List<(string Name, string Description)> ExtractXmlDocParams(string xmlDoc)
    {
        var results = new List<(string, string)>();
        var matches = ParamDocRegex().Matches(xmlDoc);

        foreach (Match match in matches)
        {
            var name = match.Groups[1].Value;
            var desc = match.Groups[2].Value;
            desc = XmlTagRegex().Replace(desc, "");
            desc = WhitespaceRunRegex().Replace(desc, " ");
            results.Add((name, desc.Trim()));
        }

        return results;
    }

    [GeneratedRegex(@"<see\s+cref=""([^""]*)""\s*/>")]
    private static partial Regex SeeCrefRegex();

    [GeneratedRegex(@"<paramref\s+name=""([^""]*)""\s*/>")]
    private static partial Regex ParamrefRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex XmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunRegex();

    [GeneratedRegex(@"<param\s+name=""([^""]+)"">(.*?)</param>", RegexOptions.Singleline)]
    private static partial Regex ParamDocRegex();
}
