using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// textDocument/inlineValue — the variable values Visual Studio and Rider paint at the end of
/// each line while the debugger is stopped.
/// </summary>
/// <remarks>
/// <para>
/// The server never reads a value. It answers with *where* to look and *what* to ask for, and
/// the client resolves each one against whichever debug session is stopped. That is what makes
/// this work identically for a netcoredbg session, the Framework <c>--dap</c> server and the AI
/// mirror without any of them knowing about it — and it means no expression is evaluated in the
/// debuggee just because a file scrolled into view.
/// </para>
/// <para>
/// Two things are deliberately not reported: anything outside the stopped frame's own member,
/// and anything below the stopped line. Both would render values that are stale or not yet
/// assigned, which is worse than rendering nothing.
/// </para>
/// </remarks>
internal static class InlineValueHandler
{
    /// <summary>Keeps one very long method from producing a wall of annotations.</summary>
    private const int MaxValues = 200;

    public static async Task<object[]> InlineValuesAsync(InlineValueParams p, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document is null)
            return [];

        var root = await document.GetSyntaxRootAsync(ct);
        var model = await document.GetSemanticModelAsync(ct);
        var text = await document.GetTextAsync(ct);
        if (root is null || model is null)
            return [];

        var stopped = LspConverters.ToTextSpan(text, p.Context.StoppedLocation);
        var member = EnclosingMember(root, stopped.Start);
        if (member is null)
            return [];

        // Only the part of the viewport that belongs to the stopped frame's own member.
        var viewport = LspConverters.ToTextSpan(text, p.Range);
        var scope = viewport.Intersection(member.Span);
        if (scope is not { } window)
            return [];

        int stoppedLine = text.Lines.GetLineFromPosition(stopped.End).LineNumber;

        var values = new List<object>();
        var seen = new HashSet<(int Line, string Expression)>();

        foreach (var node in member.DescendantNodes(n => n.Span.IntersectsWith(window)))
        {
            ct.ThrowIfCancellationRequested();
            if (values.Count >= MaxValues)
                break;
            if (!node.Span.IntersectsWith(window))
                continue;

            var (span, expression, lookup) = Describe(node, model, ct);
            if (span is not { } identifierSpan || expression is null)
                continue;

            int line = text.Lines.GetLineFromPosition(identifierSpan.Start).LineNumber;

            // A local further down the method has not been assigned in this frame yet.
            if (line > stoppedLine)
                continue;
            if (!seen.Add((line, expression)))
                continue;

            var range = LspConverters.ToRange(text.Lines, identifierSpan);
            values.Add(lookup
                ? new InlineValueVariableLookup(range, expression)
                : new InlineValueEvaluatableExpression(range, expression));
        }

        return [.. values];
    }

    /// <summary>
    /// What to show for one node: the span to anchor on, the text to resolve, and whether a
    /// scope lookup by name is enough. Member access needs evaluation because
    /// <c>order.Total</c> is not a name any scope holds.
    /// </summary>
    private static (TextSpan? Span, string? Expression, bool Lookup) Describe(
        SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        switch (node)
        {
            case VariableDeclaratorSyntax declarator:
                return model.GetDeclaredSymbol(declarator, ct) is ILocalSymbol
                    ? (declarator.Identifier.Span, declarator.Identifier.ValueText, true)
                    : (null, null, false);

            case SingleVariableDesignationSyntax designation:
                return model.GetDeclaredSymbol(designation, ct) is ILocalSymbol
                    ? (designation.Identifier.Span, designation.Identifier.ValueText, true)
                    : (null, null, false);

            case IdentifierNameSyntax identifier:
            {
                // The `Name` half of `a.b` is handled when the member access itself is visited;
                // reporting it alone would ask the debugger for a name that is not in scope.
                if (identifier.Parent is MemberAccessExpressionSyntax parent
                    && parent.Name == identifier)
                {
                    return (null, null, false);
                }

                var symbol = model.GetSymbolInfo(identifier, ct).Symbol;
                return symbol switch
                {
                    ILocalSymbol or IParameterSymbol or IRangeVariableSymbol =>
                        (identifier.Span, identifier.Identifier.ValueText, true),
                    // A field or property read by bare name resolves through `this`, which the
                    // debugger can evaluate but cannot look up as a variable name.
                    IFieldSymbol or IPropertySymbol =>
                        (identifier.Span, identifier.Identifier.ValueText, false),
                    _ => (null, null, false),
                };
            }

            case MemberAccessExpressionSyntax access
                when access.IsKind(SyntaxKind.SimpleMemberAccessExpression):
            {
                if (model.GetSymbolInfo(access, ct).Symbol is not (IFieldSymbol or IPropertySymbol))
                    return (null, null, false);
                // Only a plain dotted chain — no calls, no indexers, nothing with a side effect
                // the debugger would run to render a hint.
                return IsSimpleChain(access)
                    ? (access.Span, access.ToString(), false)
                    : (null, null, false);
            }

            default:
                return (null, null, false);
        }
    }

    private static bool IsSimpleChain(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax => true,
        ThisExpressionSyntax => true,
        MemberAccessExpressionSyntax access
            when access.IsKind(SyntaxKind.SimpleMemberAccessExpression) =>
            IsSimpleChain(access.Expression),
        _ => false,
    };

    private static SyntaxNode? EnclosingMember(SyntaxNode root, int offset)
    {
        if (offset < 0 || offset > root.FullSpan.End)
            return null;

        return root.FindToken(offset).Parent?.AncestorsAndSelf().FirstOrDefault(node =>
            node is BaseMethodDeclarationSyntax
                or AccessorDeclarationSyntax
                or LocalFunctionStatementSyntax
                or AnonymousFunctionExpressionSyntax
                or PropertyDeclarationSyntax
                or IndexerDeclarationSyntax
                or CompilationUnitSyntax);
    }
}
