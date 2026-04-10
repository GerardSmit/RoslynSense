using System.Text;

namespace RoslynMCP.Services;

/// <summary>
/// Formats tool output in a specific format (markdown, TOON, etc.).
/// Registered as a singleton in DI; tools receive it as a required parameter.
/// </summary>
public interface IOutputFormatter
{
    void AppendHeader(StringBuilder sb, string text, int level = 1);
    void AppendField(StringBuilder sb, string key, object? value);
    void AppendSeparator(StringBuilder sb);
    void AppendTable(StringBuilder sb, string name, string[] columns, List<string[]> rows, int? totalCount = null);
    void AppendHints(StringBuilder sb, params string[] hints);
    void AppendEmpty(StringBuilder sb, string message);
    void AppendTruncation(StringBuilder sb, int shown, int total, string paramName = "maxResults");
    string Escape(string text);
}
