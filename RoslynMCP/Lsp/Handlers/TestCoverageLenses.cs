using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using RoslynMCP.Services.Testing;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// The per-method "N tests" lens: how many tests the coverage map says execute this member.
/// </summary>
/// <remarks>
/// Counted from the map rather than from a live coverage run, so the number is there without
/// anything running and survives a restart. It is as old as the map is — a member written since
/// the last build reads as zero, which is the honest answer to "which tests are known to
/// exercise this".
/// </remarks>
internal static class TestCoverageLenses
{
    /// <summary>
    /// The map's rows for one file, or an empty list when there is no map. Computed once per
    /// document so a file with two hundred members does not walk the map two hundred times.
    /// </summary>
    public static IReadOnlyList<(CoverageMapEntry Entry, CoveredFile File)> ForFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return [];

        var map = TestCoverageMapStore.LoadNearest(filePath);
        return map.IsEmpty ? [] : map.EntriesForFile(filePath);
    }

    /// <summary>
    /// How many distinct tests executed any line of the member — not how many entries matched.
    /// One test class covering a method is "6 tests" if it holds six of them, because six is
    /// what a click lists and what a run would run.
    /// </summary>
    public static int CountTests(
        IReadOnlyList<(CoverageMapEntry Entry, CoveredFile File)> rows, LineRange member)
    {
        if (rows.Count == 0)
            return 0;

        var tests = new HashSet<string>(StringComparer.Ordinal);
        LineRange[] range = [member];

        foreach (var (entry, file) in rows)
        {
            if (!file.IntersectsAny(range))
                continue;

            foreach (string test in entry.Tests)
                tests.Add(test);
        }

        return tests.Count;
    }

    /// <summary>The 1-based line span of the member a declaration occupies.</summary>
    public static LineRange LineRangeOf(SyntaxNode declaration)
    {
        var span = declaration.GetLocation().GetLineSpan();
        return new LineRange(span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
    }

    /// <summary>
    /// The member enclosing a 0-based LSP position, as 1-based lines. Used by the request behind
    /// the lens's click, which is given a position rather than a declaration.
    /// </summary>
    public static LineRange? MemberLineRange(SyntaxNode root, SourceText text, int line, int character)
    {
        if (line < 0 || line >= text.Lines.Count)
            return null;

        var textLine = text.Lines[line];
        int offset = Math.Min(textLine.Start + Math.Max(0, character), textLine.End);

        var node = root.FindToken(offset).Parent;
        var member = node?.AncestorsAndSelf()
            .FirstOrDefault(n => n is MemberDeclarationSyntax or LocalFunctionStatementSyntax);

        return member is null ? null : LineRangeOf(member);
    }
}
