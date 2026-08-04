using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Lsp.Search;
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

    /// <summary>
    /// Outline entries for a node, with unnamed ones dropped.
    /// </summary>
    /// <remarks>
    /// A missing identifier token parses as an empty name, which happens in any file that does not
    /// fully parse — decompiled sources especially. The protocol requires a name, and the client
    /// throws "name must not be falsy" on the whole response, so one artifact would cost the
    /// entire outline. Dropping the artifact keeps the rest.
    /// </remarks>
    private static IEnumerable<DocumentSymbol> WalkNode(SyntaxNode node, TextLineCollection lines) =>
        WalkNodeCore(node, lines).Where(symbol => !string.IsNullOrWhiteSpace(symbol.Name));

    private static IEnumerable<DocumentSymbol> WalkNodeCore(SyntaxNode node, TextLineCollection lines)
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
                    en.Members
                        .Where(m => !string.IsNullOrWhiteSpace(m.Identifier.Text))
                        .Select(m => Make(m.Identifier.Text, null, LspSymbolKind.EnumMember,
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

    /// <summary>
    /// Name search across the solution, in C# and in the enabled packs' files.
    /// </summary>
    /// <remarks>
    /// The packs are not an either/or here the way a document request is: a query matches
    /// whatever it matches, and a control <c>ID</c> declared in an <c>.aspx</c> is as much a
    /// thing the user is looking for as a field declared in a <c>.cs</c>. Roslyn's declaration
    /// search only ever sees its own compilations, so without this the markup half of a WebForms
    /// solution is invisible to Ctrl+T.
    /// </remarks>
    public static async Task<SymbolInformation[]> WorkspaceSymbolsAsync(
        WorkspaceSymbolParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        if (string.IsNullOrWhiteSpace(p.Query))
            return Array.Empty<SymbolInformation>();

        var solution = WorkspaceService.TryGetMostRecentSolution();
        if (solution is null)
            return Array.Empty<SymbolInformation>();

        // Ranked, then capped — the other way round is how "StringBuilder" ends up unreachable
        // behind 200 alphabetically-earlier types. Files are excluded: the protocol has no kind
        // for them, and the extension's own Search Everywhere covers that.
        var hits = await SearchEverywhere.SearchAsync(
            solution, p.Query, maxResults: 200, ct, includeFiles: false);

        var results = hits
            .Select(hit => new SymbolInformation(
                hit.Name,
                hit.SymbolKind,
                new Protocol.Location(
                    LspConverters.PathToUri(hit.FilePath),
                    new Protocol.Range(
                        new Position(hit.Line, hit.Character),
                        new Position(hit.EndLine, hit.EndCharacter))),
                hit.Container))
            .ToList();

        foreach (var provider in LanguageScope.Of(languages).Contributors<ILanguageWorkspaceSymbolProvider>())
            results.AddRange(await provider.WorkspaceSymbolsAsync(p.Query, solution, ct));

        return results.ToArray();
    }
}
