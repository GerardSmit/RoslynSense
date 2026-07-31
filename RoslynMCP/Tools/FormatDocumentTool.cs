using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using ModelContextProtocol.Server;
using RoslynMCP.Lsp;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class FormatDocumentTool
{
    /// <summary>
    /// Formats a file with the project's own rules, so AI-written code arrives matching the
    /// codebase instead of making the next .editorconfig-driven diff noisy.
    /// </summary>
    [McpServerTool, Description(
        "Format a C# file using the project's formatting rules (.editorconfig included). " +
        "Writes the formatted text, or routes it through the editor when the file is open " +
        "with unsaved changes.")]
    public static async Task<string> FormatDocument(
        [Description("Path to the C# file.")] string filePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string path = PathHelper.NormalizePath(filePath);
            if (!File.Exists(path))
                return $"Error: '{filePath}' not found.";

            var document = await LspDocumentResolver.ResolveAsync(path, cancellationToken);
            if (document is null)
                return $"Error: '{filePath}' is not part of a loaded project.";

            var formatted = await Formatter.FormatAsync(
                document, (Microsoft.CodeAnalysis.Options.OptionSet?)null, cancellationToken);

            var before = await document.GetTextAsync(cancellationToken);
            var after = await formatted.GetTextAsync(cancellationToken);
            if (before.ContentEquals(after))
                return $"'{Path.GetFileName(path)}' is already formatted.";

            string text = after.ToString();

            // Writing disk under a dirty buffer would silently discard the user's unsaved work.
            if (await LspSessionRegistry.TryApplyFullTextEditAsync(
                    path, text, "Format document", cancellationToken))
                return $"Formatted '{Path.GetFileName(path)}' in the editor.";

            await File.WriteAllTextAsync(path, text, cancellationToken);
            return $"Formatted '{Path.GetFileName(path)}'.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
