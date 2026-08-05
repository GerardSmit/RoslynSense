using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Languages.WebForms.Core;
using WebFormsCore;
using WebFormsCore.Models;
using WebFormsCore.Nodes;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;
using LspRange = RoslynMCP.Lsp.Protocol.Range;
using Protocol = RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;

namespace RoslynMCP.Languages.WebForms.Lsp;

/// <summary>
/// Language features for ASPX-family markup. The LSP dispatch in <see cref="LspServer"/> routes
/// a request here when the document is <c>.aspx</c>, <c>.ascx</c>, <c>.master</c> or one of
/// their siblings; everything else still goes to the C# handlers.
/// </summary>
/// <remarks>
/// Two sources answer a request. Markup positions — tag names, attributes, IDs, handler names —
/// resolve through <see cref="AspxSymbolResolver"/> against the parse tree. Positions inside
/// <c>&lt;% %&gt;</c> code resolve through <see cref="AspxProjectionService"/>, which lifts the
/// code into a synthetic partial of the code-behind and hands the question to Roslyn, then maps
/// the answer back.
/// </remarks>
internal static class AspxLanguageHandler
{
    /// <summary>
    /// Whether this document is markup rather than C#. Takes a URI or a plain path — the
    /// extension is the whole test, and percent-encoding never reaches the end of one.
    /// </summary>
    public static bool Handles(string uriOrPath) =>
        !uriOrPath.StartsWith(VirtualDocumentHandler.GeneratedScheme + ":", StringComparison.Ordinal)
        && !uriOrPath.StartsWith(VirtualDocumentHandler.MetadataScheme + ":", StringComparison.Ordinal)
        && AspxDocumentService.IsAspxFile(uriOrPath);

    // ---- Navigation ------------------------------------------------------------------------

    public static async Task<LspLocation[]> DefinitionAsync(
        TextDocumentPositionParams p, bool typeDefinition, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (document, offset))
            return [];

        var hit = AspxSymbolResolver.ResolveAt(document, offset);

        if (hit is { Kind: AspxHitKind.FileReference, TargetPath: { Length: > 0 } target })
            return [FileStart(target)];

        // A resource key binds to no symbol and reaches no projection, so it has to be answered
        // before either fallback rather than after them.
        if (hit is not null && AspxResourceHandler.Handles(hit.Kind))
            return await AspxResourceHandler.DefinitionAsync(document, hit, ct);

        // The caret is already on the declaration, so there is no definition to go to — the
        // question a user asks here is the other one. See ControlIdUsagesAsync.
        if (hit is { Kind: AspxHitKind.ControlId, Symbol: { } declared } && !typeDefinition)
            return await ControlIdUsagesAsync(document, hit, declared, ct);

        if (hit is { Symbol: { } symbol })
            return await NavigationHandlers.DefinitionLocationsAsync(symbol, document.Project, typeDefinition, ct);

        if (await ProjectedSymbolAsync(document, offset, ct) is { } projected)
        {
            // A local or label declared inside a code block lives in the projection, which is
            // not a file anyone can open. Its declaration is really in the markup.
            if (InProjection(document, projected) is { Length: > 0 } inMarkup)
                return inMarkup;

            return await NavigationHandlers.DefinitionLocationsAsync(
                projected, document.Project, typeDefinition, ct);
        }

        return [];
    }

    /// <summary>
    /// Where a control is used, for a caret on the <c>ID</c> that declares it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>ID</c> attribute <em>is</em> the declaration — it is what makes the code-behind field
    /// exist, and this pack already says so everywhere else: the reference search reports it the way
    /// Roslyn reports a declaration, and the code lens counts from it. Go-to-definition on a
    /// declaration therefore has nowhere to go, and answering it with the designer field sends the
    /// reader to a transcription of the line their caret is already on — the same dead end
    /// <see cref="WebFormsLanguage.Supersedes"/> removes when the gesture starts from C#. Doing one
    /// there and the other here would be the two halves of one relationship disagreeing.
    /// </para>
    /// <para>
    /// Usages instead, which is what Visual Studio does for the identical caret in C#. Several
    /// locations make the editor open its peek list rather than jump, and that is the right shape
    /// for a question with more than one answer.
    /// </para>
    /// <para>
    /// The <c>ID</c> itself is filtered out for the reason the lens filters it: it is in the results
    /// because it is the declaration, and offering the caret its own position is an invitation to
    /// go nowhere.
    /// </para>
    /// </remarks>
    private static async Task<LspLocation[]> ControlIdUsagesAsync(
        AspxDocument document, AspxHit hit, ISymbol declared, CancellationToken ct)
    {
        var range = ToRange(document, hit.Span);

        return
        [
            .. (await AllReferencesAsync(declared, document.Project, includeDeclaration: false, ct))
                .Where(location => !IsSelf(location, document.FilePath, range)),
        ];
    }

    /// <summary>Whether a result is the <c>ID</c> the request started on.</summary>
    private static bool IsSelf(LspLocation location, string filePath, LspRange range) =>
        location.Range.Start.Line == range.Start.Line
        && location.Range.Start.Character == range.Start.Character
        && string.Equals(
            LspConverters.UriToPath(location.Uri), filePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A symbol's declarations, for the ones declared in the projected C# rather than in a real
    /// file: each is mapped back to the markup it was copied from. Empty when the symbol is
    /// declared somewhere real, which is the common case.
    /// </summary>
    private static LspLocation[] InProjection(AspxDocument document, ISymbol symbol)
    {
        if (AspxProjectionService.Get(document) is not { } projection)
            return [];

        var results = new List<LspLocation>();

        foreach (var location in symbol.Locations)
        {
            if (!location.IsInSource
                || !AspxProjectionService.IsProjectionPath(location.SourceTree?.FilePath))
                continue;

            if (projection.ToAspx(location.SourceSpan) is { } mapped)
                results.Add(new LspLocation(
                    LspConverters.PathToUri(document.FilePath), ToRange(document, mapped)));
        }

        return results.ToArray();
    }

    public static async Task<LspLocation[]> ImplementationAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (document, offset))
            return [];

        var symbol = AspxSymbolResolver.ResolveAt(document, offset)?.Symbol
            ?? await ProjectedSymbolAsync(document, offset, ct);
        if (symbol is null)
            return [];

        var solution = document.Project.Solution;
        var results = new List<ISymbol>();

        switch (symbol)
        {
            case INamedTypeSymbol { TypeKind: TypeKind.Interface } iface:
                results.AddRange(await SymbolFinder.FindImplementationsAsync(iface, solution, cancellationToken: ct));
                break;
            case INamedTypeSymbol type:
                results.AddRange(await SymbolFinder.FindDerivedClassesAsync(type, solution, cancellationToken: ct));
                break;
            default:
                results.AddRange(await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct));
                results.AddRange(await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: ct));
                break;
        }

        if (results.Count == 0)
            results.Add(symbol);

        return await HandlerHelpers.ToLocationsAsync(
            results.SelectMany(s => s.Locations).Where(l => l.IsInSource), document.Project, ct);
    }

    public static async Task<LspLocation[]> ReferencesAsync(ReferenceParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (document, offset))
            return [];

        // The markup counterpart of the pre-pass in NavigationHandlers: a `<%$ Resources: %>`
        // argument resolves to no symbol, so a search started on one has to reach the pack that
        // knows what a resource key is before the resolve declines.
        foreach (var provider in
                 LanguageScope.Process.Contributors<ISymbolFreeReferenceProvider>())
        {
            if (await provider.ReferencesAsync(document.FilePath, offset, document.Project, ct)
                is { } found)
            {
                return [.. found];
            }
        }

        var symbol = AspxSymbolResolver.ResolveAt(document, offset)?.Symbol
            ?? await ProjectedSymbolAsync(document, offset, ct);
        if (symbol is null)
            return [];

        return await AllReferencesAsync(symbol, document.Project, p.Context.IncludeDeclaration, ct);
    }

    /// <summary>
    /// A symbol's references in code and in markup. Shared with the C# handler, so a
    /// find-references started in the code-behind also lists the <c>OnClick=</c> that names it.
    /// </summary>
    public static async Task<LspLocation[]> AllReferencesAsync(
        ISymbol symbol, Project project, bool includeDeclaration, CancellationToken ct)
    {
        var locations = new List<Microsoft.CodeAnalysis.Location>();

        foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, project.Solution, ct))
        {
            if (includeDeclaration)
                locations.AddRange(referenced.Definition.Locations.Where(l => l.IsInSource));
            locations.AddRange(referenced.Locations.Select(r => r.Location));
        }

        var results = new List<LspLocation>(await HandlerHelpers.ToLocationsAsync(locations, project, ct));

        foreach (var markup in await AspxReferenceService.FindAsync(symbol, project, ct))
            results.Add(ToLocation(markup.FilePath, markup.Text, markup.Span));

        return results.Distinct().ToArray();
    }

    public static async Task<DocumentHighlight[]> DocumentHighlightAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (document, offset))
            return [];

        var symbol = AspxSymbolResolver.ResolveAt(document, offset)?.Symbol
            ?? await ProjectedSymbolAsync(document, offset, ct);
        if (symbol is null)
            return [];

        // Same-file scope. Not a project-wide search filtered down: this runs on cursor moves,
        // and building the project's projection to answer a question about one file would make
        // the cheapest request in the server the most expensive one.
        return (await AspxReferenceService.FindInDocumentAsync(document, symbol, ct))
            .Select(r => new DocumentHighlight(ToRange(document, r.Span), 1))
            .DistinctBy(h => h.Range)
            .ToArray();
    }

    // ---- Hover -----------------------------------------------------------------------------

    public static async Task<Hover?> HoverAsync(TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (document, offset))
            return null;

        var hit = AspxSymbolResolver.ResolveAt(document, offset);

        if (hit is { Kind: AspxHitKind.FileReference } file)
        {
            string body = file.TargetPath is { Length: > 0 } path
                ? $"`{path}`"
                : $"`{file.Name}` — not found";
            return new Hover(new MarkupContent("markdown", body), ToRange(document, hit.Span));
        }

        // Ahead of the symbol branch: an expression builder's two halves and an
        // implicit-localization key all bind to nothing, so what there is to say about them comes
        // from the resource catalog rather than from the compilation.
        if (hit is not null && AspxResourceHandler.Handles(hit.Kind))
            return await AspxResourceHandler.HoverAsync(document, hit, ct);

        if (hit is { Symbol: { } symbol })
        {
            string markdown = HoverHandler.Describe(symbol, ct);

            // System.Web keeps what a control's property or event does in a resource key its
            // metadata points at rather than in XML documentation, so where Roslyn found nothing
            // there is still something to say.
            if (symbol is IPropertySymbol or IEventSymbol
                && string.IsNullOrWhiteSpace(symbol.GetDocumentationCommentXml(cancellationToken: ct))
                && FrameworkDocumentation.Describe(symbol, document.Compilation) is { } framework)
            {
                markdown += "\n\n" + framework;
            }

            // An event attribute whose handler does not exist yet is the single most common
            // thing to hover in a WebForms file; say so instead of showing nothing.
            if (hit.Kind == AspxHitKind.EventHandler && hit.Event is not null)
                markdown += $"\n\nHandles `{hit.Event.Name}`.";

            return new Hover(new MarkupContent("markdown", markdown), ToRange(document, hit.Span));
        }

        if (hit is { Kind: AspxHitKind.EventHandler, Event: { } unbound })
        {
            return new Hover(
                new MarkupContent("markdown",
                    $"`{unbound.Name}` has no handler named `{hit.Name}` on "
                    + $"`{document.CodeBehind?.ToDisplayString() ?? "the code-behind"}`."),
                ToRange(document, hit.Span));
        }

        if (await ProjectedSymbolAsync(document, offset, ct) is { } projected)
        {
            return new Hover(
                new MarkupContent("markdown", HoverHandler.Describe(projected, ct)),
                null);
        }

        return null;
    }

    /// <summary>Signature help only ever applies to the inline C#; markup has no calls.</summary>
    public static async Task<Protocol.SignatureHelp?> SignatureHelpAsync(
        SignatureHelpParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (document, offset))
            return null;

        if (AspxProjectionService.Get(document) is not { } projection
            || projection.ToProjected(offset) is not { } projected)
            return null;

        return await SignatureHelpHandler.SignatureHelpAsync(
            projection.Document, projected, p.Context, ct);
    }

    // ---- Outline ---------------------------------------------------------------------------

    public static async Task<DocumentSymbol[]> DocumentSymbolAsync(
        DocumentSymbolParams p, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);
        var document = await AspxDocumentService.GetAsync(path, ct);
        if (document?.Tree is not { } root)
            return [];

        var symbols = new List<DocumentSymbol>();

        foreach (var directive in root.Directives)
        {
            symbols.Add(new DocumentSymbol(
                $"@{directive.DirectiveType}",
                DirectiveDetail(directive),
                LspSymbolKind.Module,
                ToRange(document, AspxSymbolResolver.Span(directive.Range)),
                ToRange(document, AspxSymbolResolver.Span(directive.Range)),
                []));
        }

        symbols.AddRange(root.Children.SelectMany(c => ToSymbols(document, c)));

        return symbols.ToArray();
    }

    private static string? DirectiveDetail(DirectiveNode directive)
    {
        foreach (string interesting in new[] { "Inherits", "Src", "TagName" })
        {
            foreach (var (key, value) in directive.Attributes)
            {
                if (key.Value.Equals(interesting, StringComparison.OrdinalIgnoreCase))
                    return value.Value;
            }
        }
        return null;
    }

    private static IEnumerable<DocumentSymbol> ToSymbols(AspxDocument document, Node node)
    {
        if (node is not ControlNode control)
        {
            // Plain HTML is structure, not outline; keep walking for controls inside it.
            if (node is ContainerNode container)
            {
                foreach (var child in container.Children.SelectMany(c => ToSymbols(document, c)))
                    yield return child;
            }
            yield break;
        }

        var children = control.Children
            .SelectMany(c => ToSymbols(document, c))
            .Concat(control.Templates.SelectMany(t => TemplateSymbol(document, t)))
            .ToArray();

        var range = ToRange(document, FullSpan(control));
        yield return new DocumentSymbol(
            control.Id ?? control.Name.Value,
            control.ControlType.Name,
            control.Id is null ? LspSymbolKind.Object : LspSymbolKind.Field,
            range,
            ToRange(document, AspxSymbolResolver.Span(control.StartTag.ElementRange)),
            children);
    }

    private static IEnumerable<DocumentSymbol> TemplateSymbol(AspxDocument document, TemplateNode template)
    {
        var range = ToRange(document, FullSpan(template));
        yield return new DocumentSymbol(
            template.Name.Value,
            "template",
            LspSymbolKind.Property,
            range,
            ToRange(document, AspxSymbolResolver.Span(template.StartTag.ElementRange)),
            template.Children.SelectMany(c => ToSymbols(document, c)).ToArray());
    }

    private static TextSpan FullSpan(ElementNode element)
    {
        var start = element.StartTag.Range;
        var end = element.EndTag?.Range ?? element.StartTag.Range;
        return TextSpan.FromBounds(
            start.Start.Offset, Math.Max(start.End.Offset, end.End.Offset));
    }

    public static async Task<FoldingRange[]> FoldingRangeAsync(FoldingRangeParams p, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);
        var document = await AspxDocumentService.GetAsync(path, ct);
        if (document?.Tree is not { } root)
            return [];

        var ranges = new List<FoldingRange>();
        var lines = document.SourceText.Lines;

        foreach (var element in AspxSymbolResolver.EnumerateElements(root))
        {
            if (element.EndTag is null)
                continue;

            int startLine = lines.GetLinePosition(Clamp(document, element.StartTag.Range.End.Offset)).Line;
            int endLine = lines.GetLinePosition(Clamp(document, element.EndTag.Range.Start.Offset)).Line;
            if (endLine > startLine)
                ranges.Add(new FoldingRange(startLine, endLine, null));
        }

        return ranges.DistinctBy(r => (r.StartLine, r.EndLine)).ToArray();
    }

    // ---- Diagnostics -----------------------------------------------------------------------

    /// <summary>
    /// Markup diagnostics: unresolved controls and properties, unbalanced tags, event attributes
    /// naming a handler that does not exist — the one that carries a fix — and resource keys no
    /// <c>.resx</c> in their probe order defines.
    /// </summary>
    public static async Task<Protocol.Diagnostic[]> DiagnosticsAsync(string filePath, CancellationToken ct)
    {
        var document = await AspxDocumentService.GetAsync(filePath, ct);
        if (document is null)
            return [];

        var parse = document.Parse.RawDiagnostics
            .Select(d => (Microsoft.CodeAnalysis.Diagnostic)d)
            .Where(d => d.Severity != DiagnosticSeverity.Hidden)
            .Select(d => new Protocol.Diagnostic(
                ToRange(document, d.Location.SourceSpan),
                LspConverters.ToLspSeverity(d.Severity),
                d.Id,
                "roslyn-sense",
                d.GetMessage()))
            .ToArray();

        var resources = await AspxResourceHandler.DiagnosticsAsync(document, ct);

        return resources.Length == 0 ? parse : [.. parse, .. resources];
    }

    // ---- Rename ----------------------------------------------------------------------------

    public static async Task<PrepareRenameResult?> PrepareRenameAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (document, offset))
            return null;

        // Ahead of the markup resolve, because a resource key is not a symbol and this handler
        // returns before any contributor is reached once the hit carries none.
        foreach (var provider in
                 LanguageScope.Process.Contributors<ISymbolFreeRenameProvider>())
        {
            if (await provider.PrepareAsync(document.FilePath, offset, ct) is { } prepared)
                return prepared;
        }

        var hit = AspxSymbolResolver.ResolveAt(document, offset);
        if (hit is null || hit.Kind is AspxHitKind.FileReference)
            return null;

        // A tag name is the type's name; renaming it from markup would rename the framework
        // type, which is never what the gesture means.
        if (hit.Kind == AspxHitKind.ControlType && hit.Symbol?.Locations.Any(l => l.IsInSource) != true)
            return null;

        if (hit.Symbol is null && hit.Kind != AspxHitKind.Code)
            return null;

        return new PrepareRenameResult(
            ToRange(document, hit.Span),
            hit.Name ?? document.Text.Substring(hit.Span.Start, hit.Span.Length));
    }

    public static async Task<WorkspaceEdit?> RenameAsync(RenameParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (document, offset))
            return null;

        // Ahead of the markup resolve, for the same reason prepareRename is.
        foreach (var provider in
                 LanguageScope.Process.Contributors<ISymbolFreeRenameProvider>())
        {
            if (await provider.RenameAsync(
                    document.FilePath, offset, p.NewName, document.Project, ct) is { } edit)
            {
                return edit;
            }
        }

        var symbol = AspxSymbolResolver.ResolveAt(document, offset)?.Symbol
            ?? await ProjectedSymbolAsync(document, offset, ct);
        if (symbol is null)
            return null;

        var changes = new Dictionary<string, List<TextEdit>>(StringComparer.OrdinalIgnoreCase);

        void Add(string uri, TextEdit edit)
        {
            if (!changes.TryGetValue(uri, out var list))
                changes[uri] = list = [];
            if (!list.Contains(edit))
                list.Add(edit);
        }

        var solution = await Renamer.RenameSymbolAsync(
            document.Project.Solution, symbol, new SymbolRenameOptions(), p.NewName, ct);

        foreach (var change in solution.GetChanges(document.Project.Solution).GetProjectChanges())
        {
            foreach (var id in change.GetChangedDocuments())
            {
                var updated = solution.GetDocument(id)!;
                var original = document.Project.Solution.GetDocument(id)!;
                if (original.FilePath is not { Length: > 0 } path)
                    continue;

                var text = await original.GetTextAsync(ct);
                foreach (var edit in await updated.GetTextChangesAsync(original, ct))
                {
                    Add(LspConverters.PathToUri(path),
                        new TextEdit(LspConverters.ToRange(text.Lines, edit.Span), edit.NewText ?? ""));
                }
            }
        }

        foreach (var (uri, edit) in await RenameEditsAsync(symbol, document.Project, p.NewName, ct))
            Add(uri, edit);

        return changes.Count == 0
            ? null
            : new WorkspaceEdit(changes.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }

    /// <summary>
    /// The markup edits a rename needs on top of Roslyn's own: every tag, attribute and handler
    /// name that referred to the symbol.
    /// </summary>
    /// <remarks>
    /// Shared with the C# rename handler on purpose. Renaming a handler from the code-behind has
    /// to rewrite the <c>OnClick=</c> that names it just as renaming it from the markup does —
    /// Roslyn sees neither, so without this the attribute is left naming a method that no longer
    /// exists.
    /// </remarks>
    public static async Task<IReadOnlyList<(string Uri, TextEdit Edit)>> RenameEditsAsync(
        ISymbol symbol, Project project, string newName, CancellationToken ct)
    {
        var edits = new List<(string, TextEdit)>();

        foreach (var reference in await AspxReferenceService.FindAsync(symbol, project, ct))
        {
            edits.Add((
                LspConverters.PathToUri(reference.FilePath),
                new TextEdit(
                    LspConverters.ToRange(reference.Text.Lines, reference.Span),
                    AspxReferenceService.RenamedText(reference, newName))));
        }

        return edits;
    }

    // ---- Shared plumbing -------------------------------------------------------------------

    private static async Task<(AspxDocument Document, int Offset)?> ResolveAsync(
        TextDocumentIdentifier textDocument, Position position, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(textDocument.Uri);
        var document = await AspxDocumentService.GetAsync(path, ct);
        if (document is null)
            return null;

        return (document, LspConverters.ToOffset(document.SourceText, position));
    }

    /// <summary>The symbol under a caret that sits inside inline C#, resolved through the
    /// projected document.</summary>
    private static async Task<ISymbol?> ProjectedSymbolAsync(
        AspxDocument document, int offset, CancellationToken ct)
    {
        if (AspxProjectionService.Get(document) is not { } projection)
            return null;
        if (projection.ToProjected(offset) is not { } projected)
            return null;

        return await SymbolFinder.FindSymbolAtPositionAsync(projection.Document, projected, ct);
    }

    internal static LspRange ToRange(AspxDocument document, TextSpan span) =>
        LspConverters.ToRange(document.SourceText.Lines, Clamp(document, span));

    private static TextSpan Clamp(AspxDocument document, TextSpan span)
    {
        int length = document.SourceText.Length;
        int start = Math.Clamp(span.Start, 0, length);
        int end = Math.Clamp(span.End, start, length);
        return TextSpan.FromBounds(start, end);
    }

    private static int Clamp(AspxDocument document, int offset) =>
        Math.Clamp(offset, 0, document.SourceText.Length);

    private static LspLocation ToLocation(string filePath, SourceText text, TextSpan span)
    {
        int length = text.Length;
        int start = Math.Clamp(span.Start, 0, length);
        int end = Math.Clamp(span.End, start, length);
        return new LspLocation(
            LspConverters.PathToUri(filePath),
            LspConverters.ToRange(text.Lines, TextSpan.FromBounds(start, end)));
    }

    private static LspLocation FileStart(string path) =>
        new(LspConverters.PathToUri(path), new LspRange(new Position(0, 0), new Position(0, 0)));
}
