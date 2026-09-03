using System.Text;
using System.Text.RegularExpressions;

namespace RoslynMCP.Services.Designers;

/// <summary>
/// The cosmetic conventions of a designer file that is already on disk, so regenerating it rewrites
/// only what actually changed.
/// </summary>
/// <remarks>
/// <para>
/// Designer files are generated, but they are also committed, reformatted and reviewed like any
/// other source. A file Visual Studio wrote years ago has usually been through the team's formatter
/// since: braces moved to their own line, trailing whitespace stripped from blank lines. Emitting
/// one canonical style over the top of that turns every regeneration into a whole-file diff, which
/// buries the one field that really changed — and makes the tool look like it rewrote code the user
/// never touched.
/// </para>
/// <para>
/// So the existing file decides how the new one is spelled. Only when there is no file yet does the
/// generator pick, and then it picks what Visual Studio would have written.
/// </para>
/// </remarks>
internal sealed record DesignerStyle
{
    /// <summary>What a file Visual Studio has not written yet gets: CodeDOM's output.</summary>
    public static DesignerStyle Default { get; } = new();

    /// <summary>Whether the file starts with a UTF-8 byte order mark, as Visual Studio writes.</summary>
    public bool ByteOrderMark { get; init; } = true;

    /// <summary>Whether an opening brace sits on its own line (Allman) rather than trailing its declaration.</summary>
    public bool BraceOnNewLine { get; init; }

    /// <summary>
    /// Whether separator lines carry the current indentation. CodeDOM indents them; most formatters
    /// strip them back to empty.
    /// </summary>
    public bool IndentBlankLines { get; init; } = true;

    /// <summary>
    /// The existing auto-generated banner, verbatim, including the blank line that ends it. Kept as
    /// it is because the versions differ in trailing whitespace that nothing else can recover.
    /// </summary>
    public string? Header { get; init; }

    /// <summary>
    /// The names of the fields the existing file declares, in declaration order, so the ones that
    /// survive keep their place instead of being resorted into markup order.
    /// </summary>
    public IReadOnlyList<string> FieldOrder { get; init; } = [];

    private static readonly Regex FieldPattern = new(
        @"^[ \t]*protected\s+(?:new\s+)?[^;=(){}]*?(?<name>[A-Za-z_@][A-Za-z0-9_]*)\s*;[ \t]*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the conventions off the file at <paramref name="designerPath"/>, falling back to
    /// <see cref="Default"/> when there is nothing there to read.
    /// </summary>
    public static DesignerStyle Detect(string designerPath)
    {
        string text;
        bool bom;

        try
        {
            if (!File.Exists(designerPath))
                return Default;

            var bytes = File.ReadAllBytes(designerPath);
            bom = bytes is [0xEF, 0xBB, 0xBF, ..];
            text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }
        catch (IOException)
        {
            return Default;
        }
        catch (UnauthorizedAccessException)
        {
            return Default;
        }

        // Normalized first: the line-anchored patterns below have no business knowing whether the
        // file on disk is CRLF, and the service re-applies the file's own endings on the way out.
        var normalized = text.ReplaceLineEndings("\n");
        var lines = normalized.Split('\n');

        return new DesignerStyle
        {
            ByteOrderMark = bom,
            BraceOnNewLine = DetectBraceOnNewLine(lines),
            IndentBlankLines = DetectIndentedBlankLines(lines),
            Header = ExtractHeader(lines),
            FieldOrder = [.. FieldPattern.Matches(normalized).Select(m => m.Groups["name"].Value)],
        };
    }

    /// <summary>
    /// A brace alone on its line, anywhere in the file, settles it: the two styles are never mixed
    /// within one generated file, and a same-line brace on the namespace is the other answer.
    /// </summary>
    private static bool DetectBraceOnNewLine(string[] lines) =>
        lines.Any(line => line.Trim() == "{");

    private static bool DetectIndentedBlankLines(string[] lines) =>
        lines.Any(line => line.Length > 0 && line.Trim().Length == 0);

    /// <summary>
    /// The leading comment banner plus the blank lines that follow it — everything before the first
    /// line of code. Returned only when it is recognisably the auto-generated header, so a file
    /// starting with something else is regenerated with the canonical one instead.
    /// </summary>
    private static string? ExtractHeader(string[] lines)
    {
        var end = 0;
        var sawComment = false;

        while (end < lines.Length)
        {
            var line = lines[end].Trim();

            if (line.StartsWith("//", StringComparison.Ordinal))
                sawComment = true;
            else if (line.Length > 0)
                break;

            end++;
        }

        if (!sawComment)
            return null;

        var header = string.Join("\n", lines[..end]) + "\n";
        return header.Contains("auto-generated", StringComparison.OrdinalIgnoreCase) ? header : null;
    }
}
