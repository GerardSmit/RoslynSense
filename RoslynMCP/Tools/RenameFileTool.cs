using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class RenameFileTool
{
    /// <summary>
    /// Renames a file and the type it declares together. Renaming the file alone leaves the
    /// type name and file name disagreeing, which is exactly the state every C# style rule
    /// exists to prevent.
    /// </summary>
    [McpServerTool, Description(
        "Rename a C# file and the type it declares, updating every reference. Only renames a " +
        "type whose name matched the old file name; other files are left alone.")]
    public static async Task<string> RenameFile(
        [Description("Current path of the file.")] string oldPath,
        [Description("New path or file name.")] string newPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string from = PathHelper.NormalizePath(oldPath);
            if (!File.Exists(from))
                return $"Error: '{oldPath}' not found.";

            // A bare name means "rename in place", which is how a human would phrase it.
            string to = Path.IsPathRooted(newPath) || newPath.Contains(Path.DirectorySeparatorChar)
                ? PathHelper.NormalizePath(newPath)
                : Path.Combine(Path.GetDirectoryName(from)!, newPath);
            if (!to.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                to += ".cs";

            if (File.Exists(to))
                return $"Error: '{to}' already exists.";

            var edit = await FileOperationsHandler.WillRenameAsync(
                new RenameFilesParams([new FileRename(
                    LspConverters.PathToUri(from), LspConverters.PathToUri(to))]),
                cancellationToken);

            int edited = 0;
            if (edit is not null)
            {
                foreach (var (uri, edits) in edit.Changes)
                {
                    string path = LspConverters.UriToPath(uri);
                    // The file being renamed is still at its old path at this point.
                    string target = string.Equals(path, from, StringComparison.OrdinalIgnoreCase) ? from : path;
                    if (await ApplyAsync(target, edits, cancellationToken))
                        edited++;
                }
            }

            File.Move(from, to);
            await WorkspaceService.EvictAllAsync(cancellationToken);
            await LspSessionRegistry.RequestRefreshAsync(RefreshKind.All, cancellationToken);

            return edit is null
                ? $"Renamed '{Path.GetFileName(from)}' to '{Path.GetFileName(to)}'. " +
                  "No type matched the old file name, so no code changed."
                : $"Renamed '{Path.GetFileName(from)}' to '{Path.GetFileName(to)}' " +
                  $"and updated {edited} file(s).";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>Applies edits back-to-front so earlier offsets stay valid.</summary>
    private static async Task<bool> ApplyAsync(
        string path, TextEdit[] edits, CancellationToken ct)
    {
        if (!File.Exists(path) || edits.Length == 0)
            return false;

        var text = Microsoft.CodeAnalysis.Text.SourceText.From(
            await File.ReadAllTextAsync(path, ct));

        var ordered = edits
            .Select(edit => (Span: LspConverters.ToTextSpan(text, edit.Range), edit.NewText))
            .OrderByDescending(change => change.Span.Start)
            .ToList();

        foreach (var (span, newText) in ordered)
            text = text.Replace(span, newText);

        if (await LspSessionRegistry.TryApplyFullTextEditAsync(path, text.ToString(), "Rename file", ct))
            return true;

        await File.WriteAllTextAsync(path, text.ToString(), ct);
        return true;
    }
}
