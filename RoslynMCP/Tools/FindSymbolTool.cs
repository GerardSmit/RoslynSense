using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Searches for symbol declarations across a project by name pattern.
/// Supports exact, prefix, substring, and camelCase matching.
/// </summary>
[McpServerToolType]
public static class FindSymbolTool
{
    /// <summary>
    /// Finds source declarations matching a name pattern in the project that
    /// contains the given file.
    /// </summary>
    [McpServerTool, Description(
        "Search for symbol declarations (types, methods, properties, fields) in a project " +
        "by name. Supports exact, prefix, substring, and camelCase matching " +
        "(e.g. 'AC' matches 'AddCalculation'). Returns a table of matching symbols with locations.")]
    public static async Task<string> FindSymbol(
        [Description("Path to any file in the project to search.")] string filePath,
        [Description("Symbol name or pattern to search for.")] string symbolName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(symbolName))
                return "Error: symbolName cannot be empty.";

            var errors = new StringBuilder();
            var fileCtx = await ToolHelper.ResolveFileAsync(filePath, errors, cancellationToken);
            if (fileCtx is null)
                return errors.ToString();

            var symbols = (await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
                fileCtx.Project, symbolName, SymbolFilter.All, cancellationToken)).ToList();

            return FormatResults(symbols, symbolName, fileCtx.Project.Name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FindSymbol] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private static string FormatResults(
        List<ISymbol> symbols, string pattern, string projectName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Symbol Search: \"{pattern}\" in {projectName}");
        sb.AppendLine();

        if (symbols.Count == 0)
        {
            sb.AppendLine("No matching symbols found.");
            return sb.ToString();
        }

        sb.AppendLine($"Found **{symbols.Count}** symbol(s).");
        sb.AppendLine();
        sb.AppendLine("| # | Symbol | Kind | File | Line |");
        sb.AppendLine("|---|--------|------|------|------|");

        int index = 1;
        foreach (var symbol in symbols.OrderBy(s => s.Name))
        {
            var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            string file = "—";
            string line = "—";

            if (location is not null)
            {
                var lineSpan = location.GetLineSpan();
                file = Path.GetFileName(lineSpan.Path);
                line = (lineSpan.StartLinePosition.Line + 1).ToString();
            }

            sb.AppendLine(
                $"| {index} | {MarkdownHelper.EscapeTableCell(symbol.ToDisplayString())} | " +
                $"{symbol.Kind} | {file} | {line} |");
            index++;
        }

        return sb.ToString();
    }
}
