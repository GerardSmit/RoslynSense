using System.Text;

namespace RoslynMCP.Services;

/// <summary>
/// Formats tool output as TOON (Token-Optimized Object Notation)
/// for reduced token usage when consumed by LLMs.
/// </summary>
public sealed class ToonFormatter : IOutputFormatter
{
    // Headers are omitted in TOON — data is self-describing
    public void AppendHeader(StringBuilder sb, string text, int level = 1) { }

    public void AppendField(StringBuilder sb, string key, object? value)
    {
        sb.AppendLine($"{key}: {value}");
    }

    // Fields are already line-separated in TOON
    public void AppendSeparator(StringBuilder sb) { }

    public void AppendTable(StringBuilder sb, string name, string[] columns, List<string[]> rows, int? totalCount = null)
    {
        int count = totalCount ?? rows.Count;
        sb.AppendLine($"{name}[{count}]{{{string.Join(',', columns)}}}:");

        foreach (var row in rows)
        {
            sb.Append("  ");
            for (int i = 0; i < row.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Escape(row[i]));
            }
            sb.AppendLine();
        }
    }

    public void AppendHints(StringBuilder sb, params string[] hints)
    {
        if (hints.Length == 0) return;
        sb.AppendLine($"help[{hints.Length}]:");
        foreach (var hint in hints)
            sb.AppendLine($"  {hint}");
    }

    public void AppendEmpty(StringBuilder sb, string message)
    {
        sb.AppendLine(message);
    }

    public void AppendTruncation(StringBuilder sb, int shown, int total, string paramName = "maxResults")
    {
        if (shown >= total) return;
        sb.AppendLine($"truncated: showing {shown} of {total}");
    }

    public string Escape(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        bool needsQuoting = text.Contains(',') ||
                            text.Contains('\n') ||
                            text.Contains('\r') ||
                            text.Contains('"') ||
                            text[0] == ' ' ||
                            text[^1] == ' ';

        if (!needsQuoting)
            return text;

        return '"' + text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r") + '"';
    }
}
