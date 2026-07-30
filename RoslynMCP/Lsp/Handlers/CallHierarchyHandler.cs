using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>textDocument/prepareCallHierarchy + callHierarchy/incomingCalls/outgoingCalls.
/// Incoming uses <see cref="SymbolFinder.FindCallersAsync(ISymbol, Solution, CancellationToken)"/>;
/// outgoing walks invocations in the symbol's declaration bodies.</summary>
internal static class CallHierarchyHandler
{
    public static async Task<HierarchyItem[]> PrepareAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        var resolved = await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct);
        if (resolved is not var (document, _, offset))
            return Array.Empty<HierarchyItem>();

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is not (IMethodSymbol or IPropertySymbol or IEventSymbol or IFieldSymbol))
            return Array.Empty<HierarchyItem>();

        var item = HierarchyItemFactory.ToItem(symbol);
        return item is null ? Array.Empty<HierarchyItem>() : [item];
    }

    public static async Task<CallHierarchyIncomingCall[]> IncomingCallsAsync(
        CallHierarchyCallsParams p, CancellationToken ct)
    {
        var (symbol, document) = await HierarchyItemFactory.ResolveSymbolAsync(p.Item, ct);
        if (symbol is null || document is null)
            return Array.Empty<CallHierarchyIncomingCall>();

        var callers = await SymbolFinder.FindCallersAsync(symbol, document.Project.Solution, ct);
        var calls = new List<CallHierarchyIncomingCall>();

        foreach (var caller in callers.Where(c => c.IsDirect))
        {
            var from = HierarchyItemFactory.ToItem(caller.CallingSymbol);
            if (from is null)
                continue;

            string fromPath = LspConverters.UriToPath(from.Uri);
            var fromRanges = caller.Locations
                .Where(l => l.IsInSource && string.Equals(
                    Services.PathHelper.NormalizePath(l.SourceTree!.FilePath), fromPath,
                    StringComparison.OrdinalIgnoreCase))
                .Select(l => LspConverters.ToRange(l.GetLineSpan().Span))
                .ToArray();
            if (fromRanges.Length == 0)
                continue;

            calls.Add(new CallHierarchyIncomingCall(from, fromRanges));
        }
        return calls.ToArray();
    }

    public static async Task<CallHierarchyOutgoingCall[]> OutgoingCallsAsync(
        CallHierarchyCallsParams p, CancellationToken ct)
    {
        var (symbol, document) = await HierarchyItemFactory.ResolveSymbolAsync(p.Item, ct);
        if (symbol is null || document is null)
            return Array.Empty<CallHierarchyOutgoingCall>();

        var solution = document.Project.Solution;
        var byTarget = new Dictionary<ISymbol, List<Protocol.Range>>(SymbolEqualityComparer.Default);

        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var declaration = await syntaxRef.GetSyntaxAsync(ct);
            var declDocument = solution.GetDocument(syntaxRef.SyntaxTree);
            var model = declDocument is null ? null : await declDocument.GetSemanticModelAsync(ct);
            if (model is null)
                continue;

            foreach (var node in declaration.DescendantNodes())
            {
                if (node is not (InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax))
                    continue;

                if (model.GetSymbolInfo(node, ct).Symbol is not IMethodSymbol target
                    || !target.Locations.Any(l => l.IsInSource))
                    continue;

                // Attribute the call to the user-facing symbol (property for accessors, etc.).
                ISymbol display = target.AssociatedSymbol ?? target;
                if (!byTarget.TryGetValue(display, out var ranges))
                    byTarget[display] = ranges = new List<Protocol.Range>();
                ranges.Add(LspConverters.ToRange(node.GetLocation().GetLineSpan().Span));
            }
        }

        return byTarget
            .Select(kv => (Item: HierarchyItemFactory.ToItem(kv.Key), Ranges: kv.Value))
            .Where(x => x.Item is not null)
            .Select(x => new CallHierarchyOutgoingCall(x.Item!, x.Ranges.ToArray()))
            .ToArray();
    }
}
