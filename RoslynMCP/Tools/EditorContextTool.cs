using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class EditorContextTool
{
    /// <summary>
    /// What the user currently has open and where their cursor is.
    /// </summary>
    [McpServerTool, Description(
        "Get what the user is looking at in their editor right now: the active file, cursor " +
        "position and enclosing symbol, current selection, open and unsaved files, and the " +
        "diagnostics visible in the active editor. Use this when the user refers to 'this' " +
        "method/file/error without naming it. Returns a notice when no editor is connected.")]
    public static string GetEditorContext(IOutputFormatter fmt)
    {
        var context = EditorContextStore.ReadNearest(Directory.GetCurrentDirectory());
        if (context is null)
            return "No editor is connected, or it has not reported any context yet.";

        // Stale context is worse than none: acting on where the cursor was an hour ago produces
        // confidently wrong answers.
        var age = DateTime.UtcNow - context.UpdatedAtUtc;
        if (age > TimeSpan.FromHours(4))
            return $"The editor last reported {age.TotalHours:F0} hours ago; treating that as stale.";

        var sb = new StringBuilder();
        fmt.AppendHeader(sb, "Editor context");

        if (context.ActiveFile is { Length: > 0 })
        {
            fmt.AppendField(sb, "Active file", context.ActiveFile);
            fmt.AppendField(sb, "Cursor", $"line {context.Line + 1}, column {context.Character + 1}");
        }
        if (context.EnclosingSymbol is { Length: > 0 })
            fmt.AppendField(sb, "Inside", context.EnclosingSymbol);

        if (context.SelectionText is { Length: > 0 })
        {
            fmt.AppendHeader(sb, "Selection", 2);
            sb.AppendLine("```csharp");
            sb.AppendLine(context.SelectionText.Length > 4000
                ? context.SelectionText[..4000] + "\n// …truncated"
                : context.SelectionText);
            sb.AppendLine("```");
        }

        if (context.Diagnostics.Count > 0)
        {
            fmt.AppendHeader(sb, "Visible diagnostics", 2);
            foreach (var diagnostic in context.Diagnostics.Take(20))
            {
                sb.AppendLine(
                    $"- line {diagnostic.Line + 1} {diagnostic.Severity} " +
                    $"{diagnostic.Code}: {diagnostic.Message}");
            }
        }

        if (context.DirtyFiles.Count > 0)
            fmt.AppendField(sb, "Unsaved", string.Join(", ", context.DirtyFiles.Select(Path.GetFileName)));
        if (context.OpenFiles.Count > 0)
            fmt.AppendField(sb, "Open", string.Join(", ", context.OpenFiles.Take(20).Select(Path.GetFileName)));

        return sb.ToString();
    }
}
