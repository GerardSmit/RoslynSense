using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.WebForms;

/// <summary>
/// Call and type hierarchy from markup.
/// </summary>
/// <remarks>
/// <para>
/// A <c>&lt;script runat="server"&gt;</c> block is copied into the projection at class-member
/// level, inside the code-behind's own partial, so an <c>override</c> written in markup is a real
/// member of the merged type and binds as one. That is what makes hierarchy answerable here at
/// all: the questions are Roslyn's, asked of the projected compilation, and only the positions
/// have to travel.
/// </para>
/// <para>
/// They travel in both directions. A caret in the markup becomes a caret in the projection, and
/// every location Roslyn answers with is mapped back — a hierarchy item's URI is a document the
/// editor is told to open, and the projection is not one.
/// </para>
/// </remarks>
internal sealed partial class WebFormsLanguage :
    ILanguageHierarchyProvider,
    ILanguageCallHierarchyContributor
{
    public async Task<HierarchyItem[]> PrepareCallHierarchyAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument.Uri, p.Position, ct) is not var (document, symbol))
            return [];

        return CallHierarchyHandler.Prepare(symbol, MapperFor(document));
    }

    public async Task<CallHierarchyIncomingCall[]> IncomingCallsAsync(
        CallHierarchyCallsParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.Item.Uri, p.Item.SelectionRange.Start, ct) is not var (document, symbol))
            return [];

        if (await ProjectWideAsync(document, symbol, ct) is not var (projection, target, mapper))
            return [];

        return await CallHierarchyHandler.IncomingCallsAsync(
            target, projection.Solution, mapper, ct);
    }

    public async Task<CallHierarchyOutgoingCall[]> OutgoingCallsAsync(
        CallHierarchyCallsParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.Item.Uri, p.Item.SelectionRange.Start, ct) is not var (document, symbol))
            return [];

        if (await ProjectWideAsync(document, symbol, ct) is not var (projection, target, mapper))
            return [];

        return await CallHierarchyHandler.OutgoingCallsAsync(
            target, projection.Solution, p.Item.Uri, mapper, ct);
    }

    public async Task<HierarchyItem[]> PrepareTypeHierarchyAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument.Uri, p.Position, ct) is not var (document, symbol))
            return [];

        if (TypeAt(document, symbol) is not { } type)
            return [];

        // The projection reopens the page class as a partial in text it generated itself, so no
        // span in the markup maps to that declaration. The directive is where the file declares
        // which class it is, and resolving that position again returns the same symbol.
        if (IsPageClass(document, type) && PageDirectiveItem(document, type) is { } page)
            return [page];

        var item = HierarchyItemFactory.ToItem(type, MapperFor(document));
        return item is null ? [] : [item];
    }

    public async Task<HierarchyItem[]> SupertypesAsync(TypeHierarchyItemParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.Item.Uri, p.Item.SelectionRange.Start, ct) is not var (document, symbol))
            return [];

        // Base types and interfaces are C# declarations wherever the question was asked from, so
        // this needs no projection beyond the one the position already resolved through.
        return TypeHierarchyHandler.Supertypes(TypeAt(document, symbol), MapperFor(document));
    }

    public async Task<HierarchyItem[]> SubtypesAsync(TypeHierarchyItemParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.Item.Uri, p.Item.SelectionRange.Start, ct) is not var (document, symbol))
            return [];

        // Derived types are searched in the current solution: the search resolves the type's
        // originating project by compilation identity, which a cached snapshot's symbol fails.
        var (project, target) = await AspxDocumentService.AnchorAsync(document, symbol, ct);

        return await TypeHierarchyHandler.SubtypesAsync(
            TypeAt(document, target), project.Solution, MapperFor(document), ct);
    }

    // ---- Called from C# ------------------------------------------------------------------

    /// <summary>
    /// The markup call sites of a symbol whose hierarchy was rooted in C#.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hierarchy rooted on a <c>.cs</c> item is answered over the real workspace, and no markup
    /// call site is in it: the projection is a fork the workspace never holds, so
    /// <see cref="SymbolFinder.FindCallersAsync(ISymbol, Solution, CancellationToken)"/> cannot
    /// see one. The question is asked again over that fork, and only the markup half of the answer
    /// is kept — the fork still contains every real C# document, so returning its callers too
    /// would report each of them twice.
    /// </para>
    /// <para>
    /// The cost is one project-wide projection, the same one find-references builds and the same
    /// cache keeps warm. The gate is what confines that cost to solutions that have markup in
    /// them: a compilation with neither control base class hosts none, and this returns on that
    /// metadata lookup without walking a directory or projecting a line.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<CallHierarchyIncomingCall>> IncomingCallsAsync(
        ISymbol symbol, Project project, CancellationToken ct)
    {
        if (!await AspxReferenceService.HostsWebFormsAsync(project, ct))
            return [];

        if (await ProjectWideAsync(project, symbol, ct) is not var (projection, target, mapper))
            return [];

        var calls = await CallHierarchyHandler.IncomingCallsAsync(
            target, projection.Solution, mapper, ct);

        return [.. calls.Where(call =>
            AspxDocumentService.IsAspxFile(LspConverters.UriToPath(call.From.Uri)))];
    }

    // ---- Positions -----------------------------------------------------------------------

    /// <summary>
    /// The markup document and the symbol at a position in it — through the parse tree where the
    /// position is on markup, through the projection where it is on code. The same call serves a
    /// fresh request and a follow-up on an item, because an item's selection range is a position
    /// in the markup that resolves to the symbol it was built for.
    /// </summary>
    private static async Task<(AspxDocument Document, ISymbol Symbol)?> ResolveAsync(
        string uri, Position position, CancellationToken ct)
    {
        var document = await AspxDocumentService.GetAsync(LspConverters.UriToPath(uri), ct);
        if (document is null)
            return null;

        int offset = LspConverters.ToOffset(document.SourceText, position);
        var symbol = AspxSymbolResolver.ResolveAt(document, offset)?.Symbol
            ?? await ProjectedSymbolAsync(document, offset, ct);

        return symbol is null ? null : (document, symbol);
    }

    private static async Task<ISymbol?> ProjectedSymbolAsync(
        AspxDocument document, int offset, CancellationToken ct)
    {
        if (AspxProjectionService.Get(document) is not { } projection
            || projection.ToProjected(offset) is not { } projected)
            return null;

        return await SymbolFinder.FindSymbolAtPositionAsync(projection.Document, projected, ct);
    }

    /// <summary>
    /// The project's markup projected into one compilation, with the symbol re-resolved into it.
    /// </summary>
    /// <remarks>
    /// Calls cross files — a page calls a method a user control's script block declares — so the
    /// single-file projection the prepare step resolves through cannot answer them. Re-resolving
    /// is not optional: this is a different <see cref="Compilation"/>, so the symbol the position
    /// produced is not the same object as the one this compilation knows.
    /// </remarks>
    private static async Task<(AspxProjectProjection Projection, ISymbol Symbol, MarkupMapper Mapper)?>
        ProjectWideAsync(AspxDocument document, ISymbol symbol, CancellationToken ct)
    {
        // The current project, not the cached document's snapshot: the project-wide projection
        // is keyed on the current compilation, and handing it an older one would rebuild the
        // whole fork for every gesture — against text the user has since moved.
        var (project, target) = await AspxDocumentService.AnchorAsync(document, symbol, ct);
        return await ProjectWideAsync(project, target, ct);
    }

    /// <inheritdoc cref="ProjectWideAsync(AspxDocument, ISymbol, CancellationToken)"/>
    private static async Task<(AspxProjectProjection Projection, ISymbol Symbol, MarkupMapper Mapper)?>
        ProjectWideAsync(Project project, ISymbol symbol, CancellationToken ct)
    {
        if (await AspxProjectionService.GetProjectAsync(project, ct) is not { } projection)
            return null;

        var target = SymbolFinder
            .FindSimilarSymbols(symbol.OriginalDefinition, projection.Compilation, ct)
            .FirstOrDefault();

        return target is null
            ? null
            : (projection, target, new MarkupMapper(projection.Files.Values));
    }

    // ---- Types ---------------------------------------------------------------------------

    /// <summary>
    /// The type a position in markup asks about. A markup file declares no types of its own; the
    /// one it describes is its page class, so a caret on a member the markup itself declares — an
    /// <c>OnLoad</c> override in a script block — answers with that class. A caret on a member
    /// declared elsewhere answers the way C# does, with nothing.
    /// </summary>
    private static INamedTypeSymbol? TypeAt(AspxDocument document, ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol named => named,
        IMethodSymbol { MethodKind: MethodKind.Constructor } ctor => ctor.ContainingType,
        _ => DeclaredInMarkup(document, symbol) ? symbol.ContainingType : null,
    };

    /// <summary>Whether this markup file's own projection declares the symbol.</summary>
    private static bool DeclaredInMarkup(AspxDocument document, ISymbol symbol)
    {
        string projectionPath = document.FilePath + AspxProjectionService.ProjectionSuffix;

        return symbol.Locations.Any(l => l.IsInSource && string.Equals(
            l.SourceTree?.FilePath, projectionPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether this is the class the page is built on. Compared by name: a symbol that came back
    /// from the projection belongs to a forked compilation, so it is never the same object as the
    /// one the document's own parse resolved <c>Inherits</c> to.
    /// </summary>
    private static bool IsPageClass(AspxDocument document, INamedTypeSymbol type) =>
        document.CodeBehind is { } codeBehind
        && string.Equals(type.ToDisplayString(), codeBehind.ToDisplayString(), StringComparison.Ordinal);

    /// <summary>
    /// The page class as the markup declares it: the directive, selected on the <c>Inherits</c>
    /// value that names the class. Null for a page that names none — its class exists only inside
    /// the projection, and there is nothing in the file to anchor an item to.
    /// </summary>
    private static HierarchyItem? PageDirectiveItem(AspxDocument document, INamedTypeSymbol type)
    {
        if (document.Tree is not { } root)
            return null;

        foreach (var directive in root.Directives)
        {
            foreach (var (key, value) in directive.Attributes)
            {
                if (!key.Value.Equals("Inherits", StringComparison.OrdinalIgnoreCase))
                    continue;

                return HierarchyItemFactory.At(
                    type,
                    LspConverters.PathToUri(document.FilePath),
                    AspxLanguageHandler.ToRange(document, AspxSymbolResolver.Span(directive.Range)),
                    AspxLanguageHandler.ToRange(document, AspxSymbolResolver.Span(value.Range)));
            }
        }

        return null;
    }

    // ---- Mapping back --------------------------------------------------------------------

    private static MarkupMapper? MapperFor(AspxDocument document) =>
        AspxProjectionService.Get(document) is { } projection
            ? new MarkupMapper([
                new AspxProjectedFile(document.FilePath, document.SourceText, projection.Projected)])
            : null;

    /// <summary>
    /// Turns positions in the projected C# back into positions in the markup they were copied
    /// from. A span that landed in the projection's own scaffolding belongs to no markup and maps
    /// to nothing, which is what keeps a <c>.aspx-inline.g.cs</c> URI — a document no editor can
    /// open — out of every answer.
    /// </summary>
    private sealed class MarkupMapper : IHierarchySourceMapper
    {
        private readonly Dictionary<string, AspxProjectedFile> _files =
            new(StringComparer.OrdinalIgnoreCase);

        public MarkupMapper(IEnumerable<AspxProjectedFile> files)
        {
            foreach (var file in files)
                _files[file.MarkupPath + AspxProjectionService.ProjectionSuffix] = file;
        }

        public bool IsGenerated(string? filePath) =>
            AspxProjectionService.IsProjectionPath(filePath);

        public (string Uri, LspRange Range)? ToSource(string filePath, TextSpan span)
        {
            if (!_files.TryGetValue(filePath, out var file)
                || file.Projected.ToAspx(span) is not { } markup)
                return null;

            int length = file.MarkupText.Length;
            int start = Math.Clamp(markup.Start, 0, length);
            var clamped = TextSpan.FromBounds(start, Math.Clamp(markup.End, start, length));

            return (
                LspConverters.PathToUri(file.MarkupPath),
                LspConverters.ToRange(file.MarkupText.Lines, clamped));
        }
    }
}
