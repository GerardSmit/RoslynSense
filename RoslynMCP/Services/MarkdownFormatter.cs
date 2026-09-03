using System.Text;

namespace RoslynMCP.Services;

/// <summary>
/// Formats tool output as markdown tables and headers.
/// </summary>
public sealed class MarkdownFormatter : IOutputFormatter
{
    public void AppendHeader(StringBuilder sb, string text, int level = 1)
    {
        // The top level is bold rather than `#`: a tool result opens with its header, and clients
        // that preview the first line render an h1 at a size that swamps the rest of the chat.
        if (level <= 1)
        {
            sb.Append("**").Append(text).AppendLine("**");
        }
        else
        {
            sb.Append('#', level);
            sb.Append(' ');
            sb.AppendLine(text);
        }

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
        BeginTable(sb, name, columns, totalCount);
        foreach (var row in rows)
            AddRow(sb, row);
        EndTable(sb);
    }

    public void BeginTable(StringBuilder sb, string name, string[] columns, int? totalCount = null)
    {
        sb.Append('|');
        foreach (var col in columns)
        {
            sb.Append(' ');
            sb.Append(col);
            sb.Append(" |");
        }
        sb.AppendLine();

        sb.Append('|');
        foreach (var _ in columns)
            sb.Append("------|");
        sb.AppendLine();
    }

    public void AddRow(StringBuilder sb, params ReadOnlySpan<string> values)
    {
        BeginRow(sb);
        foreach (var val in values)
            WriteCell(sb, val);
        EndRow(sb);
    }

    public void BeginRow(StringBuilder sb) => sb.Append('|');

    public void WriteCell(StringBuilder sb, string value)
    {
        sb.Append(' ');
        sb.Append(EscapeTableCell(value));
        sb.Append(" |");
    }

    public void WriteCell(StringBuilder sb, int value)
    {
        sb.Append(' ');
        sb.Append(value);
        sb.Append(" |");
    }

    public void EndRow(StringBuilder sb) => sb.AppendLine();

    public void EndTable(StringBuilder sb) { }

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
