using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// textDocument/linkedEditingRange — typing over one occurrence of a name rewrites the others
/// as you type, with no rename dialog.
/// </summary>
/// <remarks>
/// Deliberately limited to symbols that cannot be referenced from outside the file: locals,
/// parameters, range variables, labels, and a method's own type parameters. Linked editing has
/// no confirmation step and no preview, so it is only safe where the server can see every
/// reference. A field or a method would need a solution-wide search the client would then apply
/// blind to the part it happens to have open.
/// </remarks>
internal static class LinkedEditingHandler
{
    /// <summary>What the client accepts as still being the same identifier while typing.</summary>
    private const string IdentifierPattern = "[A-Za-z_@][A-Za-z0-9_]*";

    public static async Task<LinkedEditingRanges?> RangesAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document is null)
            return null;

        var root = await document.GetSyntaxRootAsync(ct);
        var model = await document.GetSemanticModelAsync(ct);
        var text = await document.GetTextAsync(ct);
        if (root is null || model is null)
            return null;

        var token = root.FindToken(LspConverters.ToOffset(text, p.Position));
        if (!token.IsKind(SyntaxKind.IdentifierToken) || token.Parent is null)
            return null;

        var symbol = model.GetDeclaredSymbol(token.Parent, ct)
            ?? model.GetSymbolInfo(token.Parent, ct).Symbol;
        if (symbol is null || !IsFileLocal(symbol))
            return null;

        var scope = ScopeOf(symbol, root) ?? root;
        string name = token.ValueText;

        var ranges = new List<Protocol.Range>();
        foreach (var candidate in scope.DescendantTokens())
        {
            ct.ThrowIfCancellationRequested();
            if (!candidate.IsKind(SyntaxKind.IdentifierToken)
                || !string.Equals(candidate.ValueText, name, StringComparison.Ordinal)
                || candidate.Parent is null)
            {
                continue;
            }

            var bound = model.GetDeclaredSymbol(candidate.Parent, ct)
                ?? model.GetSymbolInfo(candidate.Parent, ct).Symbol;
            if (SymbolEqualityComparer.Default.Equals(bound, symbol))
                ranges.Add(LspConverters.ToRange(text.Lines, candidate.Span));
        }

        // One range is the declaration on its own — nothing to keep in sync, and offering it
        // makes the client show linked-editing decoration for no reason.
        return ranges.Count > 1 ? new LinkedEditingRanges([.. ranges], IdentifierPattern) : null;
    }

    private static bool IsFileLocal(ISymbol symbol) => symbol switch
    {
        ILocalSymbol or IParameterSymbol or IRangeVariableSymbol or ILabelSymbol => true,
        ITypeParameterSymbol t => t.TypeParameterKind == TypeParameterKind.Method,
        _ => false,
    };

    /// <summary>
    /// The smallest node that contains every reference: the body the symbol was declared in.
    /// </summary>
    private static SyntaxNode? ScopeOf(ISymbol symbol, SyntaxNode root)
    {
        var declaration = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (declaration is null || !root.Span.Contains(declaration.Span))
            return null;

        return declaration.AncestorsAndSelf().FirstOrDefault(node =>
            node is BaseMethodDeclarationSyntax
                or AccessorDeclarationSyntax
                or LocalFunctionStatementSyntax
                or AnonymousFunctionExpressionSyntax
                or PropertyDeclarationSyntax
                or IndexerDeclarationSyntax
                or CompilationUnitSyntax);
    }
}
