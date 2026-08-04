using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.Proto;

/// <summary>
/// Call and type hierarchy rooted in a <c>.proto</c>.
/// </summary>
/// <remarks>
/// <para>
/// There is no projection to map through here. An <c>rpc</c> is already several real C# methods and
/// a <c>service</c> is already a real C# class, so both questions are Roslyn's own the moment the
/// caret has been turned into a symbol set — and every location that comes back is a document the
/// editor can open, which is what lets every call pass <c>mapper: null</c>.
/// </para>
/// <para>
/// Only the root item is the pack's. It is anchored on the <c>.proto</c> and titled the way the
/// <c>.proto</c> titles it, because that is the file the user is looking at and <c>GetWidget</c> is
/// what they wrote; the generated <c>GetWidgetAsync</c> beside it is protoc's spelling of the same
/// thing and belongs in the branches, not at the root. Nothing is stashed on the item to make that
/// work — a follow-up request re-resolves it from its URI and selection range, and that position is
/// the declaration's own name.
/// </para>
/// </remarks>
internal sealed partial class ProtoLanguage :
    ILanguageHierarchyProvider,
    ILanguageCallHierarchyContributor
{
    public async Task<HierarchyItem[]> PrepareCallHierarchyAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await HierarchyTargetAsync(p.TextDocument.Uri, p.Position, ct) is not var (view, hit))
            return [];

        // Only an rpc. A message is data and a field is a property on it; both are used rather than
        // called, and find-usages is the feature that answers about them.
        return hit.Declaration is ProtoRpc rpc ? [HierarchyItemFor(view, rpc, LspSymbolKind.Method)] : [];
    }

    /// <summary>
    /// The C# that calls the rpc: every call site of every method protoc generated for it.
    /// </summary>
    /// <remarks>
    /// The whole symbol set, not just the client's blocking overload — the same set find-usages
    /// searches, because the two features disagreeing about one rpc is the failure this pack exists
    /// to avoid. Roslyn's caller search takes one symbol at a time, so the sweep is a loop, and the
    /// results are merged before anyone sees them: the base method and its override are two symbols
    /// reached from one <c>rpc</c>, and a caller of both would otherwise appear twice under it.
    /// </remarks>
    public async Task<CallHierarchyIncomingCall[]> IncomingCallsAsync(
        CallHierarchyCallsParams p, CancellationToken ct)
    {
        if (await HierarchyTargetAsync(p.Item.Uri, p.Item.SelectionRange.Start, ct) is not var (view, hit))
            return [];

        if (hit.Declaration is not ProtoRpc || view.Project is not { } project)
            return [];

        var callers = new Dictionary<(string Uri, int Line, int Character), HierarchyItem>();
        var sites = new Dictionary<(string Uri, int Line, int Character), List<LspRange>>();

        foreach (var symbol in await ProtoReferenceService.SymbolSetForAsync(hit, view.Index, project, ct))
        {
            ct.ThrowIfCancellationRequested();

            foreach (var call in await CallHierarchyHandler.IncomingCallsAsync(
                symbol, project.Solution, mapper: null, ct))
            {
                var key = (call.From.Uri,
                    call.From.SelectionRange.Start.Line,
                    call.From.SelectionRange.Start.Character);

                if (!sites.TryGetValue(key, out var ranges))
                {
                    sites[key] = ranges = [];
                    callers[key] = call.From;
                }

                foreach (var range in call.FromRanges)
                {
                    if (!ranges.Contains(range))
                        ranges.Add(range);
                }
            }
        }

        return [.. callers.Select(entry =>
            new CallHierarchyIncomingCall(entry.Value, [.. sites[entry.Key]]))];
    }

    /// <summary>
    /// Nothing. An <c>rpc</c> declares a signature and has no body, so it calls nothing; the
    /// generated method that stands for it has a body made of protoc's marshalling, which is the
    /// runtime's business and not a call the contract makes.
    /// </summary>
    public Task<CallHierarchyOutgoingCall[]> OutgoingCallsAsync(
        CallHierarchyCallsParams p, CancellationToken ct) =>
        Task.FromResult<CallHierarchyOutgoingCall[]>([]);

    public async Task<HierarchyItem[]> PrepareTypeHierarchyAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await HierarchyTargetAsync(p.TextDocument.Uri, p.Position, ct) is not var (view, hit))
            return [];

        // Only a service. A message is a sealed generated class and an enum is a generated enum;
        // nothing derives from either, so rooting a hierarchy on one promises a tree that is always
        // a single node.
        return hit.Declaration is ProtoService service ? [HierarchyItemFor(view, service, LspSymbolKind.Class)] : [];
    }

    /// <summary>
    /// Empty in practice, and asked anyway. protoc's service base derives from nothing but
    /// <c>object</c> and implements no interface, so there is nothing above it — but that is a fact
    /// about the generated code rather than one this should assert, and reading it off the symbol
    /// keeps the answer right if a future plugin version changes it.
    /// </summary>
    public async Task<HierarchyItem[]> SupertypesAsync(TypeHierarchyItemParams p, CancellationToken ct)
    {
        if (await HierarchyTargetAsync(p.Item.Uri, p.Item.SelectionRange.Start, ct) is not var (view, hit))
            return [];

        return TypeHierarchyHandler.Supertypes(ServiceBaseFor(view, hit), mapper: null);
    }

    /// <summary>The hand-written server implementations: the classes deriving from the abstract
    /// base protoc generated for the service.</summary>
    public async Task<HierarchyItem[]> SubtypesAsync(TypeHierarchyItemParams p, CancellationToken ct)
    {
        if (await HierarchyTargetAsync(p.Item.Uri, p.Item.SelectionRange.Start, ct) is not var (view, hit))
            return [];

        if (view.Project is not { } project)
            return [];

        return await TypeHierarchyHandler.SubtypesAsync(
            ServiceBaseFor(view, hit), project.Solution, mapper: null, ct);
    }

    // ---- Called from C# ----------------------------------------------------------------------

    /// <summary>
    /// The <c>.proto</c> declaration a generated symbol came from, folded into the incoming calls of
    /// a hierarchy rooted on that symbol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a call in the sense an invocation is — the <c>rpc</c> does not call the method, it is
    /// where the method comes from. It is here because find-references already reports it: a caret
    /// on <c>GetWidget</c> in the generated base lists <c>rpc GetWidget</c> among its results, and a
    /// call hierarchy on the identical caret omitting it would be two features disagreeing about the
    /// same symbol, which is worse than either answer alone. Reported through the same
    /// <see cref="ProtoReferenceService.ProtoReferencesToAsync"/> so they cannot drift apart.
    /// </para>
    /// <para>
    /// The gate is a metadata lookup: a project that cannot resolve <c>IMessage</c> holds no
    /// generated code, and every solution without protobuf in it pays only that.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<CallHierarchyIncomingCall>> IncomingCallsAsync(
        ISymbol symbol, Project project, CancellationToken ct)
    {
        if (!await ProtoReferenceService.HostsProtobufAsync(project, ct))
            return [];

        var references = await ProtoReferenceService.ProtoReferencesToAsync(symbol, project, ct);
        if (references.IsDefaultOrEmpty)
            return [];

        var calls = new List<CallHierarchyIncomingCall>();

        foreach (var reference in references)
        {
            // The reference's own text, not a fresh read: it and the spans on it come from one
            // parse, so the range cannot fall outside the buffer it will be shown in.
            var lines = reference.Text.Lines;
            var declaration = reference.Declaration;
            var selection = LspConverters.ToRange(lines, declaration.Name.Span);

            calls.Add(new CallHierarchyIncomingCall(
                new HierarchyItem(
                    declaration.Name.Value,
                    HierarchyItemKind(declaration.Kind),
                    LspConverters.PathToUri(reference.FilePath),
                    LspConverters.ToRange(lines, declaration.Span),
                    selection,
                    Path.GetFileName(reference.FilePath)),
                [selection]));
        }

        return calls;
    }

    // ---- Positions ---------------------------------------------------------------------------

    /// <summary>
    /// The file and what the caret is on in it. One call serves a fresh request and a follow-up on
    /// an item alike, because an item's selection range is the position in the <c>.proto</c> that
    /// resolves back to the declaration it was built for.
    /// </summary>
    private static async Task<(ProtoProjectView View, ProtoHit Hit)?> HierarchyTargetAsync(
        string uri, Position position, CancellationToken ct)
    {
        if (await ProtoWorkspace.GetAsync(LspConverters.UriToPath(uri), ct) is not { } view)
            return null;

        int offset = LspConverters.ToOffset(view.Text, position);
        return ProtoSymbolResolver.ResolveAt(view, offset) is { } hit ? (view, hit) : null;
    }

    private static INamedTypeSymbol? ServiceBaseFor(ProtoProjectView view, ProtoHit hit) =>
        hit.Declaration is ProtoService service ? view.Index.ServiceBaseFor(service) : null;

    private static HierarchyItem HierarchyItemFor(ProtoProjectView view, ProtoDeclaration declaration, int kind) =>
        new(declaration.Name.Value,
            kind,
            LspConverters.PathToUri(view.FilePath),
            LspConverters.ToRange(view.Text.Lines, declaration.Span),
            LspConverters.ToRange(view.Text.Lines, declaration.Name.Span),
            declaration.Parent?.FullName is { Length: > 0 } owner
                ? owner
                : Path.GetFileName(view.FilePath));

    private static int HierarchyItemKind(ProtoDeclarationKind kind) => kind switch
    {
        ProtoDeclarationKind.Rpc => LspSymbolKind.Method,
        ProtoDeclarationKind.Field => LspSymbolKind.Field,
        ProtoDeclarationKind.EnumValue => LspSymbolKind.EnumMember,
        ProtoDeclarationKind.Enum => LspSymbolKind.Enum,
        ProtoDeclarationKind.Oneof => LspSymbolKind.Property,
        _ => LspSymbolKind.Class,
    };
}
