using System.Text;

namespace RoslynMCP.Services;

/// <summary>
/// Formats tool output as markdown tables and headers.
/// </summary>
public sealed class MarkdownFormatter : IOutputFormatter
{
    public void AppendHeader(StringBuilder sb, string text, int level = 1)
    {
        sb.Append('#', level);
        sb.Append(' ');
        sb.AppendLine(text);
        sb.AppendLine();
    }

    public void AppendField(StringBuilder sb, string key, object? value)
    {
        sb.AppendLine($"**{key}**: {value}");
    }

    public void AppendSeparator(StringBuilder sb)
    {
        sb.AppendLine();
    }

    public void AppendTable(StringBuilder sb, string name, string[] columns, List<string[]> rows, int? totalCount = null)
    {
        // Header row
        sb.Append('|');
        foreach (var col in columns)
        {
            sb.Append(' ');
            sb.Append(col);
            sb.Append(" |");
        }
        sb.AppendLine();

        // Separator row
        sb.Append('|');
        foreach (var _ in columns)
            sb.Append("------|");
        sb.AppendLine();

        // Data rows
        foreach (var row in rows)
        {
            sb.Append('|');
            for (int i = 0; i < row.Length; i++)
            {
                sb.Append(' ');
                sb.Append(EscapeTableCell(row[i]));
                sb.Append(" |");
            }
            sb.AppendLine();
        }
    }

    public void AppendHints(StringBuilder sb, params string[] hints)
    {
        if (hints.Length == 0) return;
        sb.AppendLine();
        foreach (var hint in hints)
            sb.AppendLine($"_{hint}_");
    }

    public void AppendEmpty(StringBuilder sb, string message)
    {
        sb.AppendLine(message);
    }

    public void AppendTruncation(StringBuilder sb, int shown, int total, string paramName = "maxResults")
    {
        if (shown >= total) return;
        sb.AppendLine($"_Showing first {shown} of {total}. Use `{paramName}` to see more._");
    }

    public string Escape(string text) => EscapeTableCell(text);

    internal static string EscapeTableCell(string text) =>
        text.Replace("|", "\\|")
            .Replace("`", "\\`")
            .Replace("\r\n", " ")
            .Replace("\r", " ")
            .Replace("\n", " ");
}
