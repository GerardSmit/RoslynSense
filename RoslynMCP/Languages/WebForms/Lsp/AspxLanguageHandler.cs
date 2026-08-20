using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Languages;
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

        // `Eval("Entity.Images")`. Also not a symbol: the argument is a string the runtime
        // reflects over, so the projection binds it to System.String and the caret's real
        // destination — the property the segment names — is reachable only from the item type.
        if (await DataBoundMemberAsync(document, offset, ct) is { } bound)
            return WithoutDesigners(
                await NavigationHandlers.DefinitionLocationsAsync(
                    bound, document.Project, typeDefinition, ct));

        // The caret is already on the declaration, so there is no definition to go to — the
        // question a user asks here is the other one. See ControlIdUsagesAsync.
        if (hit is { Kind: AspxHitKind.ControlId, Symbol: { } declared } && !typeDefinition)
            return await ControlIdUsagesAsync(document, hit, declared, ct);

        // The same caret on a template-nested ID: no field is generated for it, so its usages
        // are the FindControl("id") call sites — including the discovered wrapper methods —
        // that reach the control at runtime.
        if (hit is { Kind: AspxHitKind.ControlId, Symbol: null, Name.Length: > 0 } && !typeDefinition)
            return await FindControlCallSitesAsync(document, hit.Name, ct);

        // A tag naming a user control: its markup is the control, not the class behind it.
        if (hit is { Kind: AspxHitKind.ControlType, Symbol: INamedTypeSymbol control }
            && await UserControlMarkupAsync(control, ct) is { } markup)
        {
            return markup;
        }

        if (hit is { Symbol: { } symbol })
        {
            return WithoutDesigners(
                await NavigationHandlers.DefinitionLocationsAsync(
                    symbol, document.Project, typeDefinition, ct));
        }

        // Before the symbol lookup, for the reason the C# handler asks first: inside a literal
        // Roslyn binds to nothing, so a resource key in a <% %> block would fall through to an
        // empty answer. The destinations are .resx files, so nothing needs mapping back.
        if (await ProjectedEmbeddedAsync(document, offset, ct) is
            { Language: IEmbeddedDefinitionProvider embedded } embeddedContext)
        {
            return await embedded.DefinitionAsync(embeddedContext, typeDefinition, ct);
        }

        if (await ProjectedSymbolAsync(document, offset, ct) is { } projected)
        {
            // A local or label declared inside a code block lives in the projection, which is
            // not a file anyone can open. Its declaration is really in the markup.
            if (InProjection(document, projected) is { Length: > 0 } inMarkup)
                return inMarkup;

            // Through the contributors, as the C# handler does: one field must not answer with the
            // markup ID from a .ascx.cs and with the designer line from the .ascx beside it.
            var found = await NavigationHandlers.DefinitionLocationsAsync(
                projected, document.Project, typeDefinition, ct);

            // typeDefinition asks what type the control is, and the designer is not in that way.
            if (typeDefinition)
                return found;

            // The symbol belongs to the projection's forked compilation, and the contributor
            // compares with SymbolEqualityComparer, which never matches across two of them.
            // AnchorAsync keys on the solution, which has not moved; the compilation has.
            var current = await AspxDocumentService.CurrentProjectAsync(document, ct);
            var anchored = await current.GetCompilationAsync(ct) is { } compilation
                ? SymbolFinder.FindSimilarSymbols(projected.OriginalDefinition, compilation, ct)
                    .FirstOrDefault() ?? projected
                : projected;

            return await NavigationHandlers.WithContributionsAsync(
                [anchored.OriginalDefinition], current, [.. found], languages: null, ct);
        }

        return [];
    }

    /// <summary>
    /// The <c>.ascx</c> behind a tag, or null when the tag names a control that is only a class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A user control is its markup. The class in <c>CustomControl.ascx.cs</c> holds the handlers,
    /// but the tags, the layout and the <c>ID</c>s a caller came to read are in the
    /// <c>.ascx</c> — and F12 from a page into a <c>partial class</c> declaration is a jump out of
    /// the language the reader is working in, for a file that is half of the answer at best. The
    /// class is still one hop away, by F12 on anything inside the control it opens.
    /// </para>
    /// <para>
    /// The markup is derived from the type's own declaring files rather than from the
    /// <c>&lt;%@ Register Src="…" %&gt;</c> that brought the tag into scope. A prefix registered in
    /// <c>web.config</c> carries no <c>Src</c> at all, and a page that inherits its registrations
    /// from a master never wrote one — deriving from the class covers every way the tag could have
    /// resolved, and it is the same mapping the designer withdrawal uses in reverse.
    /// </para>
    /// <para>
    /// A control with no markup — a plain <c>WebControl</c> subclass, or anything from a referenced
    /// assembly — returns null so the caller falls through to the C# declaration it would have
    /// given before.
    /// </para>
    /// </remarks>
    private static async Task<LspLocation[]?> UserControlMarkupAsync(
        INamedTypeSymbol control, CancellationToken ct)
    {
        var results = new List<LspLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in control.OriginalDefinition.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();

            if (reference.SyntaxTree.FilePath is not { Length: > 0 } declaring
                || AspxSourceMappingService.MarkupPathFor(declaring) is not { } markupPath
                || !seen.Add(markupPath))
            {
                continue;
            }

            // Loaded, not merely derived: a class named after a page it does not belong to would
            // otherwise send F12 to a file that has nothing to do with the tag.
            var index = await WebFormsIndex.GetAsync(markupPath, ct);
            if (index is null)
                continue;

            // The Inherits value, which is where the markup names this class — the closest thing a
            // page has to a declaration of it, and it puts the caret at the top of the file either
            // way.
            results.Add(new LspLocation(
                LspConverters.PathToUri(markupPath),
                index.Inherits is { Length: > 0 }
                    ? LspConverters.ToRange(index.InheritsSpan)
                    : new LspRange(new Position(0, 0), new Position(0, 0))));
        }

        return results.Count > 0 ? [.. results] : null;
    }

    /// <summary>
    /// The same results without the generated designer halves.
    /// </summary>
    /// <remarks>
    /// A designer is a transcription of markup the caret is already in, so from a markup file it is
    /// never the useful half of a <c>partial class</c> — F12 on a tag offered both and made the
    /// editor ask which. Dropped only while something else survives, for the reason
    /// <c>NavigationHandlers</c> applies to its own withdrawal: landing somewhere imperfect beats a
    /// gesture that reports nothing.
    /// </remarks>
    private static LspLocation[] WithoutDesigners(LspLocation[] locations)
    {
        var kept = locations
            .Where(location =>
                !AspxSourceMappingService.IsDesignerPath(LspConverters.UriToPath(location.Uri)))
            .ToArray();

        return kept.Length > 0 ? kept : locations;
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
        var (project, target) = await AspxDocumentService.AnchorAsync(document, declared, ct);

        return
        [
            .. (await AllReferencesAsync(target, project, includeDeclaration: false, ct))
                .Where(location => !IsSelf(location, document.FilePath, range)),
        ];
    }

    /// <summary>
    /// The <c>FindControl("id")</c> call sites that reach a template-nested control, which are
    /// the only code references such a control has — no designer field is generated for it.
    /// </summary>
    /// <remarks>
    /// The whole project is searched, but when the page's own code-behind holds any of the call
    /// sites, only those are returned: a lookup of the same id from an unrelated page is a
    /// different control that merely shares the name.
    /// </remarks>
    private static async Task<LspLocation[]> FindControlCallSitesAsync(
        AspxDocument document, string controlId, CancellationToken ct)
    {
        var current = await AspxDocumentService.CurrentProjectAsync(document, ct);
        var wrappers = await ProjectIndexCacheService.GetFindControlWrappersAsync(current, ct);
        var references = await AspxSourceMappingService.FindControlByIdAsync(
            current, controlId, wrappers, ct);

        if (references.Count == 0)
            return [];

        var declaringFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (document.CodeBehind is { } codeBehind)
        {
            foreach (var reference in codeBehind.DeclaringSyntaxReferences)
            {
                if (reference.SyntaxTree.FilePath is { Length: > 0 } path)
                    declaringFiles.Add(Path.GetFullPath(path));
            }
        }

        var own = references
            .Where(reference => declaringFiles.Contains(Path.GetFullPath(reference.FilePath)))
            .ToList();

        return [.. (own.Count > 0 ? own : references).Select(CallSiteLocation).Distinct()];
    }

    /// <summary>A call site as a location: the id literal when its span was recorded, else the
    /// invocation's start.</summary>
    private static LspLocation CallSiteLocation(AspxSymbolReference reference)
    {
        var range = reference.LiteralSpan is { } span
            ? LspConverters.ToRange(span)
            : new LspRange(
                new Position(reference.Line - 1, reference.Column - 1),
                new Position(reference.Line - 1, reference.Column - 1));

        return new LspLocation(LspConverters.PathToUri(reference.FilePath), range);
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

        var resolved = AspxSymbolResolver.ResolveAt(document, offset)?.Symbol
            ?? await ProjectedSymbolAsync(document, offset, ct);
        if (resolved is null)
            return [];

        var (project, symbol) = await AspxDocumentService.AnchorAsync(document, resolved, ct);
        var solution = project.Solution;
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
            results.SelectMany(s => s.Locations).Where(l => l.IsInSource), project, ct);
    }

    public static async Task<LspLocation[]> ReferencesAsync(ReferenceParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (document, offset))
            return [];

        // The markup counterpart of the pre-pass in NavigationHandlers: a `<%$ Resources: %>`
        // argument resolves to no symbol, so a search started on one has to reach the pack that
        // knows what a resource key is before the resolve declines. The current project, not the
        // parse's snapshot: a key search reads document text, and its answers are positions in
        // the files as they are now.
        var current = await AspxDocumentService.CurrentProjectAsync(document, ct);
        foreach (var provider in
                 LanguageScope.Process.Contributors<ISymbolFreeReferenceProvider>())
        {
            if (await provider.ReferencesAsync(document.FilePath, offset, current, ct)
                is { } found)
            {
                return [.. found];
            }
        }

        var hit = AspxSymbolResolver.ResolveAt(document, offset);

        // A template-nested ID binds to no field, so its references are the FindControl call
        // sites — the same answer go-to-definition gives for this caret.
        if (hit is { Kind: AspxHitKind.ControlId, Symbol: null, Name.Length: > 0 })
        {
            var callSites = await FindControlCallSitesAsync(document, hit.Name, ct);

            return p.Context.IncludeDeclaration
                ? [new LspLocation(
                       LspConverters.PathToUri(document.FilePath), ToRange(document, hit.Span)),
                   .. callSites]
                : callSites;
        }

        var symbol = hit?.Symbol ?? await ProjectedSymbolAsync(document, offset, ct);
        if (symbol is null)
            return [];

        var (project, target) = await AspxDocumentService.AnchorAsync(document, symbol, ct);
        return await AllReferencesAsync(target, project, p.Context.IncludeDeclaration, ct);
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

        // `Eval("Entity.Images")`, in the same place the definition path takes it and for the same
        // reason: the argument is a string the runtime reflects over, so the projection binds it to
        // System.String and hovering it described the string rather than the property. Reached
        // before the symbol branch because that is what the projection would answer with.
        if (await DataBoundSegmentAsync(document, offset, ct) is { } binding
            && DescribeBinding(binding.Segment, binding.ItemType, document, ct) is { } bound)
        {
            return new Hover(
                new MarkupContent("markdown", bound),
                ToRange(document, binding.Segment.Span));
        }

        if (hit is { Symbol: { } symbol })
        {
            string markdown = HoverHandler.Describe(symbol, ct, document.Compilation);

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

        // Same order as the definition path: a key literal binds to no symbol, so the pack that
        // knows what it names has to be asked before the bind that will not find one.
        if (await ProjectedEmbeddedAsync(document, offset, ct) is
            { Language: IEmbeddedHoverProvider embedded } embeddedContext
            && await embedded.HoverAsync(embeddedContext, ct) is { } hover)
        {
            // The range the pack computed is in projected coordinates, which name characters no
            // one can see. Dropped rather than mapped: the caret's own word is what gets
            // highlighted then, which is the span the key occupies either way.
            return hover with { Range = null };
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
    public static Task<Protocol.Diagnostic[]> DiagnosticsAsync(string filePath, CancellationToken ct) =>
        DiagnosticsAsync(filePath, graph: null, ct);

    /// <summary>
    /// The <paramref name="graph"/> overload exists for the workspace sweep, which asks for every
    /// file in the project and should not rebuild the include graph per file.
    /// </summary>
    public static async Task<Protocol.Diagnostic[]> DiagnosticsAsync(
        string filePath, AspxIncludeGraph? graph, CancellationToken ct)
    {
        var document = await AspxDocumentService.GetAsync(filePath, ct);
        if (document is null)
            return [];

        // A file someone includes runs inline in the including page — its prefixes registered
        // there, its closing tags matching tags the page opened — so its standalone parse
        // reports errors the runtime can never produce. Answer from the includers' parses
        // instead, keeping only what is located in this file.
        graph ??= AspxIncludeService.GetGraph(document.Project);
        var rootIncluders = graph.RootIncluders(document.FilePath);

        // Only the *parse* needs the includers' context. What a resource key in this file names is
        // this file's own question, so a fragment gets the same key diagnostics a page does.
        var parse = rootIncluders.Length > 0
            ? await IncludeScopedDiagnosticsAsync(document, rootIncluders, ct)
            : document.Parse.RawDiagnostics
                // The parse inlines what this file includes, so diagnostics raised inside included
                // content carry the include's path — and its offsets, which mean nothing in this
                // buffer. They are reported on the include file itself, above.
                .Where(d => OwnedByDocument(d, document.FilePath))
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
        var embedded = await EmbeddedDiagnosticsAsync(document, ct);
        var bindings = await AspxBindingDiagnostics.DiagnosticsAsync(document, ct);

        return resources.Length == 0 && embedded.Length == 0 && bindings.Length == 0
            ? parse
            : [.. parse, .. resources, .. embedded, .. bindings];
    }

    /// <summary>
    /// What the embedded-string languages have to say about the literals in this page's inline
    /// code — a resource key naming an entry no <c>.resx</c> declares, above all.
    /// </summary>
    /// <remarks>
    /// The same pass the C# diagnostics run, over the projection, with the spans brought back. The
    /// builders and <c>resourcekey</c> attributes are already covered by
    /// <see cref="AspxResourceHandler.DiagnosticsAsync"/>, which reads them out of the parse tree;
    /// a key inside <c>&lt;%= LocalizeString("…") %&gt;</c> is a C# literal and reachable no other
    /// way. Anything the projection cannot map back is dropped rather than reported at a guessed
    /// position: scaffolding this server wrote is not something the user can fix.
    /// </remarks>
    private static async Task<Protocol.Diagnostic[]> EmbeddedDiagnosticsAsync(
        AspxDocument document, CancellationToken ct)
    {
        if (AspxProjectionService.Get(document) is not { } projection)
            return [];

        var found = await DiagnosticsHandler.EmbeddedDiagnosticsAsync(projection.Document, ct);
        if (found.Count == 0)
            return [];

        var mapped = new List<Protocol.Diagnostic>(found.Count);

        foreach (var diagnostic in found)
        {
            var projected = LspConverters.ToTextSpan(projection.Text, diagnostic.Range);

            if (projection.ToAspx(projected) is { } span)
                mapped.Add(diagnostic with { Range = ToRange(document, span) });
        }

        return [.. mapped];
    }

    /// <summary>A diagnostic with no file belongs to the parse that raised it; one with a file
    /// belongs where it says.</summary>
    private static bool OwnedByDocument(
        WebFormsCore.SourceGenerator.Models.ReportedDiagnostic diagnostic, string filePath)
    {
        string? path = diagnostic.FileLineSpan.Path;
        return string.IsNullOrEmpty(path) || PathsEqual(path, filePath);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Diagnostics for an include-only fragment: each root includer's parse inlined this file
    /// with the includer's registrations and open tags in scope; what that parse reported inside
    /// this file — spans are offsets in this file's own text — is this file's diagnostics. Two
    /// pages including the same fragment usually raise the same finding, hence the dedupe.
    /// </summary>
    private static async Task<Protocol.Diagnostic[]> IncludeScopedDiagnosticsAsync(
        AspxDocument document, IReadOnlyList<string> includers, CancellationToken ct)
    {
        var results = new List<Protocol.Diagnostic>();
        var seen = new HashSet<(string Id, int Start, int End, string Message)>();

        foreach (string includer in includers)
        {
            var parent = await AspxDocumentService.GetAsync(includer, ct);
            if (parent is null)
                continue;

            foreach (var reported in parent.Parse.RawDiagnostics)
            {
                if (string.IsNullOrEmpty(reported.FileLineSpan.Path)
                    || !PathsEqual(reported.FileLineSpan.Path, document.FilePath))
                    continue;

                Microsoft.CodeAnalysis.Diagnostic diagnostic = reported;
                if (diagnostic.Severity == DiagnosticSeverity.Hidden)
                    continue;

                string message = diagnostic.GetMessage();
                if (!seen.Add((diagnostic.Id, reported.TextSpan.Start, reported.TextSpan.End, message)))
                    continue;

                results.Add(new Protocol.Diagnostic(
                    ToRange(document, reported.TextSpan),
                    LspConverters.ToLspSeverity(diagnostic.Severity),
                    diagnostic.Id,
                    "roslyn-sense",
                    message));
            }
        }

        return [.. results];
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

        // Ahead of the markup resolve, for the same reason prepareRename is. The current project,
        // not the parse's snapshot: a key rename's edits are applied to the buffers the user has
        // now, and a stale snapshot's offsets would land them mid-word.
        var current = await AspxDocumentService.CurrentProjectAsync(document, ct);
        foreach (var provider in
                 LanguageScope.Process.Contributors<ISymbolFreeRenameProvider>())
        {
            if (await provider.RenameAsync(
                    document.FilePath, offset, p.NewName, current, ct) is { } edit)
            {
                return edit;
            }
        }

        var resolved = AspxSymbolResolver.ResolveAt(document, offset)?.Symbol
            ?? await ProjectedSymbolAsync(document, offset, ct);
        if (resolved is null)
            return null;

        // A rename's edits are applied to the buffers the user has now, so they have to be
        // computed against the current solution — the cached document's snapshot may predate
        // body edits that moved every span below them.
        var (project, symbol) = await AspxDocumentService.AnchorAsync(document, resolved, ct);

        var changes = new Dictionary<string, List<TextEdit>>(StringComparer.OrdinalIgnoreCase);

        void Add(string uri, TextEdit edit)
        {
            if (!changes.TryGetValue(uri, out var list))
                changes[uri] = list = [];
            if (!list.Contains(edit))
                list.Add(edit);
        }

        var solution = await Renamer.RenameSymbolAsync(
            project.Solution, symbol, new SymbolRenameOptions(), p.NewName, ct);

        foreach (var change in solution.GetChanges(project.Solution).GetProjectChanges())
        {
            foreach (var id in change.GetChangedDocuments())
            {
                var updated = solution.GetDocument(id)!;
                var original = project.Solution.GetDocument(id)!;
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

        foreach (var (uri, edit) in await RenameEditsAsync(symbol, project, p.NewName, ct))
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

    /// <summary>
    /// The member a data-binding path names at the caret — the <c>Images</c> of
    /// <c>Eval("Entity.Images")</c>, or the <c>Entity</c> when the caret is on that half.
    /// </summary>
    /// <summary>
    /// What a data-binding path segment is worth saying, or null when there is nothing.
    /// </summary>
    /// <remarks>
    /// A segment that bound is described the way the same member is described in C#, plus what
    /// System.Web keeps in metadata rather than in XML documentation. A segment that did not bind
    /// is worth a line only when the item type is known — otherwise the answer would be
    /// "not found on nothing", which is noise over a page whose container declares no
    /// <c>ItemType</c> and whose <c>DataSource</c> could not be traced.
    /// </remarks>
    internal static string? DescribeBinding(
        DataBindingSegment segment, INamedTypeSymbol? itemType,
        AspxDocument document, CancellationToken ct)
    {
        if (segment.Symbol is { } member)
        {
            string markdown = HoverHandler.Describe(member, ct, document.Compilation);

            if (string.IsNullOrWhiteSpace(member.GetDocumentationCommentXml(cancellationToken: ct))
                && FrameworkDocumentation.Describe(member, document.Compilation) is { } framework)
            {
                markdown += "\n\n" + framework;
            }

            return markdown;
        }

        if (itemType is null || segment.Name.Length == 0)
            return null;

        return $"`{itemType.ToDisplayString()}` has no member named `{segment.Name}`.";
    }

    internal static async Task<ISymbol?> DataBoundMemberAsync(
        AspxDocument document, int offset, CancellationToken ct) =>
        (await DataBoundSegmentAsync(document, offset, ct))?.Segment.Symbol;

    /// <summary>
    /// The path segment under the caret, and the type the path started from.
    /// </summary>
    /// <remarks>
    /// The item type comes back alongside the segment because an unresolved segment is worth
    /// saying something about, and what there is to say is which type it was looked for on. A
    /// segment resolving to nothing is the ordinary case while the name is still being typed, and
    /// the ordinary case for a misspelling too.
    /// </remarks>
    internal static async Task<(DataBindingSegment Segment, INamedTypeSymbol? ItemType)?>
        DataBoundSegmentAsync(AspxDocument document, int offset, CancellationToken ct)
    {
        if (DataBindingService.ArgumentAt(document.Text, offset) is not { } argument)
            return null;

        var itemType = await DataBindingService.ItemTypeAsync(document, offset, ct);
        var segments = DataBindingService.Segments(document.Text, argument, itemType);

        return DataBindingService.SegmentAt(segments, offset) is { } segment
            ? (segment, itemType)
            : null;
    }

    /// <summary>
    /// The embedded language claiming a caret that sits inside a string literal in inline C#.
    /// </summary>
    /// <remarks>
    /// The same call the C# handlers make, on the projected document. Without it the projection
    /// seam swallows every literal-borne feature in markup: a resource key inside
    /// <c>&lt;%= GetString("Information") %&gt;</c> binds to no symbol — string literals bind to
    /// nothing at all — so <see cref="ProjectedSymbolAsync"/> answers null and F12 lands nowhere,
    /// while the identical call in the <c>.ascx.cs</c> beside it navigates to the <c>.resx</c>.
    /// </remarks>
    internal static async Task<EmbeddedStringContext?> ProjectedEmbeddedAsync(
        AspxDocument document, int offset, CancellationToken ct)
    {
        if (AspxProjectionService.Get(document) is not { } projection
            || projection.ToProjected(offset) is not { } projected)
        {
            return null;
        }

        return await RoslynEmbeddedLanguages.Current.DetectAsync(projection.Document, projected, ct);
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
