using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// The find-usages answer for a subject Roslyn binds to nothing: a resource key in a string
/// literal, a configuration name, a connection string. The LSP handler consults the packs ahead of
/// its symbol lookup; this is the same consultation for the MCP tool, so a caller that asks about
/// a key over the tool surface is not told "no symbol found" for something the editor can find.
/// </summary>
internal static class SymbolFreeUsages
{
    /// <summary>
    /// The report, or null when no pack owns <paramref name="offset"/> and the caller should carry
    /// on with whatever it does for an unresolved symbol.
    /// </summary>
    internal static async Task<string?> ReportAsync(
        string filePath, int offset, Project? project, string subject,
        IOutputFormatter fmt, int maxResults, CancellationToken ct)
    {
        foreach (var provider in LanguageScope.Process.Contributors<ISymbolFreeReferenceProvider>())
        {
            if (await provider.ReferencesAsync(filePath, offset, project, ct) is not { } found)
                continue;

            return Format(subject, found, filePath, project, fmt, maxResults);
        }

        return null;
    }

    private static string Format(
        string subject,
        IReadOnlyList<Lsp.Protocol.Location> locations,
        string filePath,
        Project? project,
        IOutputFormatter fmt,
        int maxResults)
    {
        var results = new StringBuilder();

        fmt.AppendHeader(results, "References");

        fmt.AppendHeader(results, "Search Information", level: 2);
        fmt.AppendField(results, "File", filePath);
        fmt.AppendField(results, "Target", $"`{subject}`");
        if (project?.FilePath is { } projectPath)
            fmt.AppendField(results, "Project", Path.GetFileName(projectPath));
        fmt.AppendField(results, "Note",
            "The target binds to no symbol — it is named in text, and the search is the owning " +
            "language pack's rather than Roslyn's.");
        fmt.AppendSeparator(results);

        fmt.AppendHeader(results, "Locations", level: 2);
        fmt.AppendField(results, "Found", locations.Count);
        fmt.AppendSeparator(results);

        if (locations.Count == 0)
            return results.ToString();

        // One read per file, not per location: a key with twenty sites in one resx would otherwise
        // read it twenty times.
        var texts = new Dictionary<string, SourceText?>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<string[]>();

        foreach (var location in locations.Take(maxResults))
        {
            string path = LspConverters.UriToPath(location.Uri);
            rows.Add([path, $"{location.Range.Start.Line + 1}", Snippet(texts, path, location)]);
        }

        fmt.AppendTable(results, "Sites", ["File", "Line", "Snippet"], rows);

        if (locations.Count > maxResults)
        {
            fmt.AppendField(
                results, "Truncated", $"{locations.Count - maxResults} further site(s) not listed");
        }

        fmt.AppendSeparator(results);
        return results.ToString();
    }

    /// <summary>The line the site sits on, trimmed and shortened to a table cell.</summary>
    private static string Snippet(
        Dictionary<string, SourceText?> texts, string path, Lsp.Protocol.Location location)
    {
        if (!texts.TryGetValue(path, out var text))
        {
            try
            {
                text = SourceText.From(File.ReadAllText(path));
            }
            catch (IOException)
            {
                text = null;
            }

            texts[path] = text;
        }

        if (text is null || location.Range.Start.Line >= text.Lines.Count)
            return "";

        string line = text.Lines[location.Range.Start.Line].ToString().Trim();
        return line.Length > 80 ? line[..77] + "..." : line;
    }
}
