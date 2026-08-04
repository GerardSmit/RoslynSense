using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Proto.Tools;

/// <summary>
/// Turns a caller's <c>[| |]</c> snippet into a caret offset in a <c>.proto</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of the difference between the two front-ends. The editor hands the LSP layer a
/// caret; an AI session has none and describes the target by quoting the line it is on, so every
/// tool here has to recover the offset before it can ask <see cref="Core.ProtoSymbolResolver"/>
/// anything. Everything downstream of this method is the same engine the editor drives.
/// </para>
/// <para>
/// Kept in one place because both proto tools need it and a caller commonly runs
/// <c>go_to_definition</c> and then <c>find_usages</c> on the same snippet: two copies of the
/// match-picking rule would eventually answer about two different occurrences of the same word,
/// which reads as the two tools disagreeing about the file.
/// </para>
/// </remarks>
internal static class ProtoMarkup
{
    /// <summary>
    /// The file offsets the marked span maps to, or <c>null</c> when the snippet appears nowhere in
    /// the file.
    /// </summary>
    /// <param name="hintLine">The 1-based line the caller believes the snippet is near, used to
    /// choose between repeated matches. A <c>.proto</c> writes the same word over and over —
    /// <c>Widget</c> is a message name, a field type and half the rpc signatures in the file — so
    /// without a hint the first occurrence wins, which is the rule the ASPX tools use.</param>
    public static (int Start, int End)? FindMarkedSpan(
        SourceText text, MarkupString markup, int? hintLine = null)
    {
        string fileText = text.ToString();

        var matches = MarkupSymbolResolver.FindAllOccurrences(fileText, markup.PlainText);
        if (matches.Count == 0)
            return null;

        var match = PickBestMatch(text, matches, hintLine);

        return (
            MarkupSymbolResolver.MapSnippetOffsetToFile(fileText, match, markup.PlainText, markup.SpanStart),
            MarkupSymbolResolver.MapSnippetOffsetToFile(
                fileText, match, markup.PlainText, markup.SpanStart + markup.SpanLength));
    }

    /// <summary>The match on the line nearest <paramref name="hintLine"/>, or the first one when
    /// there is nothing to choose between.</summary>
    private static MarkupSymbolResolver.SnippetMatch PickBestMatch(
        SourceText text, List<MarkupSymbolResolver.SnippetMatch> matches, int? hintLine)
    {
        if (matches.Count == 1 || hintLine is null)
            return matches[0];

        return matches.MinBy(match => Math.Abs(LineOf(text, match.FileOffset) - hintLine.Value));
    }

    /// <summary>The 1-based line an offset sits on, clamped so an offset past the end of a file
    /// that shrank under us still answers instead of throwing.</summary>
    public static int LineOf(SourceText text, int offset) =>
        text.Lines.GetLinePosition(Math.Clamp(offset, 0, text.Length)).Line + 1;
}
