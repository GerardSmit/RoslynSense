using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;
using LspFoldingRange = RoslynMCP.Lsp.Protocol.FoldingRange;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>textDocument/foldingRange: brace blocks of declarations and statements,
/// #region/#endregion pairs, using-directive runs, and multi-line comment runs.</summary>
internal static class FoldingRangeHandler
{
    public static async Task<LspFoldingRange[]> FoldingRangesAsync(
        FoldingRangeParams p, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document is null)
            return Array.Empty<LspFoldingRange>();

        var root = await document.GetSyntaxRootAsync(ct);
        var text = await document.GetTextAsync(ct);
        if (root is null)
            return Array.Empty<LspFoldingRange>();

        var lines = text.Lines;
        var ranges = new List<LspFoldingRange>();

        foreach (var node in root.DescendantNodes(descendIntoTrivia: false))
        {
            var span = BraceSpan(node) ?? node switch
            {
                SwitchStatementSyntax sw => TokenSpan(sw.OpenBraceToken, sw.CloseBraceToken),
                InitializerExpressionSyntax init => TokenSpan(init.OpenBraceToken, init.CloseBraceToken),
                AnonymousObjectCreationExpressionSyntax anon => TokenSpan(anon.OpenBraceToken, anon.CloseBraceToken),
                _ => null,
            };
            if (span is not { } s)
                continue;

            int startLine = lines.GetLinePosition(s.Start).Line;
            int endLine = lines.GetLinePosition(s.End).Line;
            if (endLine > startLine)
                ranges.Add(new LspFoldingRange(startLine, endLine, Kind: null));
        }

        AddUsingRuns(root, lines, ranges);
        AddRegions(root, lines, ranges);
        AddCommentRuns(root, lines, ranges);

        return ranges
            .DistinctBy(r => (r.StartLine, r.EndLine))
            .OrderBy(r => r.StartLine)
            .ToArray();
    }

    /// <summary>Span from opening to closing brace for brace-delimited declarations and blocks.
    /// Starting at the open brace keeps the signature/header line visible when folded.</summary>
    private static TextSpan? BraceSpan(SyntaxNode node) => node switch
    {
        NamespaceDeclarationSyntax ns => TokenSpan(ns.OpenBraceToken, ns.CloseBraceToken),
        BaseTypeDeclarationSyntax type => TokenSpan(type.OpenBraceToken, type.CloseBraceToken),
        AccessorListSyntax accessors => TokenSpan(accessors.OpenBraceToken, accessors.CloseBraceToken),
        BlockSyntax block => TokenSpan(block.OpenBraceToken, block.CloseBraceToken),
        _ => null,
    };

    private static TextSpan? TokenSpan(SyntaxToken open, SyntaxToken close) =>
        open.IsMissing || close.IsMissing ? null : TextSpan.FromBounds(open.SpanStart, close.Span.End);

    private static void AddUsingRuns(SyntaxNode root, TextLineCollection lines, List<LspFoldingRange> ranges)
    {
        // The compilation unit is the root; walking the whole file to find it was two of the five
        // traversals this handler makes over a document it is asked about on every open and every
        // structural edit. `as` rather than a cast: the resolver hands back any Roslyn document and
        // nothing above here checks the language, so a non-C# root must degrade to no usings
        // instead of throwing.
        //
        // The namespace walk descends only through compilation units and namespaces, which is
        // exhaustive — a namespace can nest inside nothing else — and stops at the first type
        // declaration rather than visiting every node in every method body.
        var usingLists = (root as CompilationUnitSyntax)?.Usings is { } fileUsings
            ? [fileUsings]
            : Enumerable.Empty<SyntaxList<UsingDirectiveSyntax>>();

        usingLists = usingLists.Concat(
            root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                .OfType<BaseNamespaceDeclarationSyntax>()
                .Select(n => n.Usings));

        foreach (var usings in usingLists)
        {
            if (usings.Count < 2)
                continue;
            int start = lines.GetLinePosition(usings[0].SpanStart).Line;
            int end = lines.GetLinePosition(usings[^1].Span.End).Line;
            if (end > start)
                ranges.Add(new LspFoldingRange(start, end, FoldingRangeKind.Imports));
        }
    }

    private static void AddRegions(SyntaxNode root, TextLineCollection lines, List<LspFoldingRange> ranges)
    {
        var stack = new Stack<int>();
        foreach (var directive in root.DescendantTrivia(descendIntoTrivia: true)
                     .Where(t => t.IsDirective)
                     .Select(t => t.GetStructure())
                     .OfType<DirectiveTriviaSyntax>())
        {
            switch (directive)
            {
                case RegionDirectiveTriviaSyntax region:
                    stack.Push(lines.GetLinePosition(region.SpanStart).Line);
                    break;
                case EndRegionDirectiveTriviaSyntax end when stack.Count > 0:
                    int start = stack.Pop();
                    int endLine = lines.GetLinePosition(end.SpanStart).Line;
                    if (endLine > start)
                        ranges.Add(new LspFoldingRange(start, endLine, FoldingRangeKind.Region));
                    break;
            }
        }
    }

    private static void AddCommentRuns(SyntaxNode root, TextLineCollection lines, List<LspFoldingRange> ranges)
    {
        int runStart = -1, runEnd = -1;

        void Flush()
        {
            if (runStart >= 0 && runEnd > runStart)
                ranges.Add(new LspFoldingRange(runStart, runEnd, FoldingRangeKind.Comment));
            runStart = runEnd = -1;
        }

        foreach (var trivia in root.DescendantTrivia())
        {
            bool isComment = trivia.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiLineCommentTrivia)
                || trivia.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiLineDocumentationCommentTrivia);
            if (!isComment)
                continue;

            int start = lines.GetLinePosition(trivia.SpanStart).Line;
            int end = lines.GetLinePosition(trivia.Span.End).Line;
            // Doc comment trivia includes the trailing newline — pull the end back onto content.
            if (end > start && trivia.Span.End <= lines[end].Start)
                end--;

            if (runStart >= 0 && start <= runEnd + 1)
            {
                runEnd = Math.Max(runEnd, end);
            }
            else
            {
                Flush();
                runStart = start;
                runEnd = end;
            }
        }
        Flush();
    }
}
