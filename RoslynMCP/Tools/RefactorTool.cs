using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Services.Refactoring;

namespace RoslynMCP.Tools;

/// <summary>
/// Refactorings that rewrite more than the file in front of you.
/// </summary>
/// <remarks>
/// These are the ones worth a tool rather than a text edit: both reach call sites, overrides and
/// implementations across the solution, and doing either by editing text means finding all of
/// those by hand and getting every one right.
/// </remarks>
[McpServerToolType]
public static class RefactorTool
{
    [McpServerTool, Description(
        "Reorder or remove a method's parameters and update every call site, override, " +
        "implementation and XML doc comment across the solution. Give the original parameter " +
        "indices in their new order: '1,0' swaps the first two, '0,2' drops the middle one of " +
        "three. Editing the declaration as text instead leaves every caller broken.")]
    public static async Task<string> ChangeSignature(
        [Description("Path to the file containing the method.")]
        string filePath,
        [Description("Line of the method's name (1-based).")]
        int line,
        [Description("Original parameter indices in their new order, comma-separated and " +
                     "0-based. Omit an index to remove that parameter.")]
        string newOrder,
        IOutputFormatter fmt,
        [Description("Column of the method's name (1-based). Defaults to the first non-space.")]
        int column = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var order = new List<int>();
            foreach (var part in newOrder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, out int index))
                    return $"Error: '{part}' is not a parameter index.";
                order.Add(index);
            }

            var located = await LocateAsync(filePath, line, column, cancellationToken);
            if (located.Error is { } error)
                return error;

            var result = await RefactoringService.ChangeSignatureAsync(
                located.Document!, located.Position, order, cancellationToken);

            return Describe(result, fmt);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "Move the type at a position into its own file, named after the type, keeping the " +
        "namespace and the usings it needs. Use this to split a file that has grown several " +
        "types rather than copying text between files by hand.")]
    public static async Task<string> MoveTypeToFile(
        [Description("Path to the file containing the type.")]
        string filePath,
        [Description("Line of the type's name (1-based).")]
        int line,
        IOutputFormatter fmt,
        [Description("Column of the type's name (1-based). Defaults to the first non-space.")]
        int column = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var located = await LocateAsync(filePath, line, column, cancellationToken);
            if (located.Error is { } error)
                return error;

            var result = await RefactoringService.MoveTypeToFileAsync(
                located.Document!, located.Position, cancellationToken);

            return Describe(result, fmt);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>Turns a file and a 1-based line/column into a document and an absolute offset.</summary>
    private static async Task<(Microsoft.CodeAnalysis.Document? Document, int Position, string? Error)>
        LocateAsync(string filePath, int line, int column, CancellationToken cancellationToken)
    {
        var errors = new StringBuilder();
        var context = await ToolHelper.ResolveFileAsync(filePath, errors, cancellationToken);
        if (context?.Document is null)
            return (null, 0, errors.Length > 0 ? errors.ToString() : "Error: File not found in project.");

        var text = await context.Document.GetTextAsync(cancellationToken);
        if (line < 1 || line > text.Lines.Count)
            return (null, 0, $"Error: line {line} is outside {Path.GetFileName(filePath)}.");

        var textLine = text.Lines[line - 1];

        // Column 0 means "wherever the code starts": the caller knows the line, and making them
        // count leading whitespace to hit a name is a good way to miss.
        int offset = column > 0
            ? Math.Min(column - 1, textLine.Span.Length)
            : textLine.ToString().TakeWhile(char.IsWhiteSpace).Count();

        return (context.Document, textLine.Start + offset, null);
    }

    private static string Describe(RefactoringResult result, IOutputFormatter fmt)
    {
        if (!result.Ok)
            return $"Error: {result.Message}";

        var sb = new StringBuilder(result.Message);
        sb.AppendLine();

        if (result.ChangedFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**{result.ChangedFiles.Count} file(s) changed:**");
            foreach (string file in result.ChangedFiles)
                sb.AppendLine($"- {file}");
        }

        fmt.AppendHints(sb,
            "Use BuildProject to confirm nothing was left behind",
            "Use GetRoslynDiagnostics on a changed file to see errors in place");

        return sb.ToString();
    }
}
