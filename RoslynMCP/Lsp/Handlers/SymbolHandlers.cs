using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>documentSymbol (hierarchical outline) and workspace/symbol (name search).</summary>
internal static class SymbolHandlers
{
    public static async Task<DocumentSymbol[]> DocumentSymbolsAsync(
        DocumentSymbolParams p, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document is null)
            return Array.Empty<DocumentSymbol>();

        var root = await document.GetSyntaxRootAsync(ct);
        var text = await document.GetTextAsync(ct);
        if (root is null)
            return Array.Empty<DocumentSymbol>();

        return root.ChildNodes().SelectMany(n => WalkNode(n, text.Lines)).ToArray();
    }

    private static IEnumerable<DocumentSymbol> WalkNode(SyntaxNode node, TextLineCollection lines)
    {
        switch (node)
        {
            case BaseNamespaceDeclarationSyntax ns:
                yield return Make(ns.Name.ToString(), null, LspSymbolKind.Namespace, ns.Span, ns.Name.Span,
                    ns.Members.SelectMany(m => WalkNode(m, lines)), lines);
                break;

            case TypeDeclarationSyntax type: // class / struct / interface / record
                int typeKind = type switch
                {
                    InterfaceDeclarationSyntax => LspSymbolKind.Interface,
                    StructDeclarationSyntax => LspSymbolKind.Struct,
                    _ => LspSymbolKind.Class,
                };
                yield return Make(type.Identifier.Text, TypeDetail(type), typeKind, type.Span, type.Identifier.Span,
                    type.Members.SelectMany(m => WalkNode(m, lines)), lines);
                break;

            case EnumDeclarationSyntax en:
                yield return Make(en.Identifier.Text, null, LspSymbolKind.Enum, en.Span, en.Identifier.Span,
                    en.Members.Select(m => Make(m.Identifier.Text, null, LspSymbolKind.EnumMember,
                        m.Span, m.Identifier.Span, Array.Empty<DocumentSymbol>(), lines)), lines);
                break;

            case DelegateDeclarationSyntax del:
                yield return Make(del.Identifier.Text, null, LspSymbolKind.Function, del.Span, del.Identifier.Span,
                    Array.Empty<DocumentSymbol>(), lines);
                break;

            case MethodDeclarationSyntax method:
                yield return Make(method.Identifier.Text,
                    $"({string.Join(", ", method.ParameterList.Parameters.Select(pa => pa.Type?.ToString()))})",
                    LspSymbolKind.Method, method.Span, method.Identifier.Span, Array.Empty<DocumentSymbol>(), lines);
                break;

            case ConstructorDeclarationSyntax ctor:
                yield return Make(ctor.Identifier.Text,
                    $"({string.Join(", ", ctor.ParameterList.Parameters.Select(pa => pa.Type?.ToString()))})",
                    LspSymbolKind.Constructor, ctor.Span, ctor.Identifier.Span, Array.Empty<DocumentSymbol>(), lines);
                break;

            case PropertyDeclarationSyntax prop:
                yield return Make(prop.Identifier.Text, prop.Type.ToString(), LspSymbolKind.Property,
                    prop.Span, prop.Identifier.Span, Array.Empty<DocumentSymbol>(), lines);
                break;

            case IndexerDeclarationSyntax indexer:
                yield return Make("this[]", indexer.Type.ToString(), LspSymbolKind.Property,
                    indexer.Span, indexer.ThisKeyword.Span, Array.Empty<DocumentSymbol>(), lines);
                break;

            case EventDeclarationSyntax ev:
                yield return Make(ev.Identifier.Text, ev.Type.ToString(), LspSymbolKind.Event,
                    ev.Span, ev.Identifier.Span, Array.Empty<DocumentSymbol>(), lines);
                break;

            case EventFieldDeclarationSyntax evField:
                foreach (var v in evField.Declaration.Variables)
                    yield return Make(v.Identifier.Text, evField.Declaration.Type.ToString(), LspSymbolKind.Event,
                        evField.Span, v.Identifier.Span, Array.Empty<DocumentSymbol>(), lines);
                break;

            case FieldDeclarationSyntax field:
                int fieldKind = field.Modifiers.Any(SyntaxKind.ConstKeyword)
                    ? LspSymbolKind.Constant : LspSymbolKind.Field;
                foreach (var v in field.Declaration.Variables)
                    yield return Make(v.Identifier.Text, field.Declaration.Type.ToString(), fieldKind,
                        field.Span, v.Identifier.Span, Array.Empty<DocumentSymbol>(), lines);
                break;

            case GlobalStatementSyntax:
                break; // top-level statements — no outline entries

            default:
                foreach (var child in node.ChildNodes())
                    foreach (var symbol in WalkNode(child, lines))
                        yield return symbol;
                break;
        }
    }

    private static string? TypeDetail(TypeDeclarationSyntax type) =>
        type.TypeParameterList is { Parameters.Count: > 0 } tp ? $"<{tp.Parameters}>" : null;

    private static DocumentSymbol Make(
        string name, string? detail, int kind, TextSpan fullSpan, TextSpan selectionSpan,
        IEnumerable<DocumentSymbol> children, TextLineCollection lines) =>
        new(name, detail, kind,
            LspConverters.ToRange(lines, fullSpan),
            LspConverters.ToRange(lines, selectionSpan),
            children.ToArray());

    public static async Task<SymbolInformation[]> WorkspaceSymbolsAsync(
        WorkspaceSymbolParams p, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.Query))
            return Array.Empty<SymbolInformation>();

        var solution = WorkspaceService.TryGetMostRecentSolution();
        if (solution is null)
            return Array.Empty<SymbolInformation>();

        var symbols = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
            solution, p.Query, SymbolFilter.TypeAndMember, ct);

        return symbols
            .Where(s => s.Locations.Any(l => l.IsInSource))
            .Take(200)
            .Select(s =>
            {
                var loc = LspConverters.ToLocation(s.Locations.First(l => l.IsInSource))!;
                return new SymbolInformation(
                    s.Name,
                    LspConverters.ToLspSymbolKind(s),
                    loc,
                    s.ContainingType?.ToDisplayString() ?? s.ContainingNamespace?.ToDisplayString());
            })
            .ToArray();
    }
}
