using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Mediator.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using LspCodeLens = RoslynMCP.Lsp.Protocol.CodeLens;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.Mediator;

/// <summary>
/// Everything the pack contributes to a request about C#, which is everything it does.
/// </summary>
/// <remarks>
/// The four seams answer one question between them — which call sites reach which handler — from
/// whichever end the request arrived at. Sharing <c>Core</c> is what keeps the gutter's count, the
/// peek's list and the AI tool's report from disagreeing about the same pair of files.
/// </remarks>
internal sealed partial class MediatorLanguage :
    ILanguageDefinitionRedirector,
    ILanguageReferenceContributor,
    ILanguageCallHierarchyContributor,
    ILanguageCodeLensContributor
{
    /// <summary>This pack's <see cref="CodeLensData.Kind"/>, read only under its own pack id.</summary>
    private const string SendersLensKind = "senders";

    /// <summary>
    /// F12 on a <c>Send</c>, a <c>Publish</c> or one of Zapto's generated extension methods,
    /// answering with the handler instead of the dispatcher Roslyn bound.
    /// </summary>
    public Task<IReadOnlyList<ISymbol>> RedirectAsync(NavigationContext context, CancellationToken ct) =>
        context.Symbol.Kind is SymbolKind.Method
            ? MediatorNavigationService.ResolveTargetsAsync(
                context.Document,
                context.Offset,
                context.Symbol,
                wantType: context.Kind == NavigationKind.TypeDefinition,
                ct)
            : Task.FromResult<IReadOnlyList<ISymbol>>([]);

    /// <summary>
    /// The dispatches that reach a handler, folded into find-references on it. Without them a
    /// handler called from a dozen places reports only its registration, which is the one line
    /// nobody was looking for.
    /// </summary>
    public async Task<IReadOnlyList<LspLocation>> ReferencesAsync(
        ISymbol symbol, Project project, CancellationToken ct)
    {
        var sites = await SitesForAsync(symbol, project, ct);
        return sites.Count == 0
            ? []
            : await HandlerHelpers.ToLocationsAsync(sites.Select(s => s.Location), project, ct);
    }

    /// <summary>
    /// The same call sites as callers, so the hierarchy and find-references cannot disagree about
    /// one caret.
    /// </summary>
    public async Task<IReadOnlyList<CallHierarchyIncomingCall>> IncomingCallsAsync(
        ISymbol symbol, Project project, CancellationToken ct)
    {
        var sites = await SitesForAsync(symbol, project, ct);
        var calls = new List<CallHierarchyIncomingCall>();

        foreach (var site in sites)
        {
            if (LspConverters.ToLocation(site.Location) is not { } location)
                continue;

            calls.Add(new CallHierarchyIncomingCall(
                new HierarchyItem(
                    site.ContainingMember ?? Path.GetFileName(site.FilePath),
                    LspSymbolKind.Method,
                    location.Uri,
                    location.Range,
                    location.Range,
                    site.LineText),
                [location.Range]));
        }

        return calls;
    }

    /// <summary>
    /// A count over every handler in the document.
    /// </summary>
    /// <remarks>
    /// Its own lens rather than leaving it to the reference lens, because the two answer different
    /// questions: a handler type is genuinely referenced by its registration and its tests, and
    /// what the reader wants to know is how many places send to it.
    /// </remarks>
    public async Task<IReadOnlyList<LspCodeLens>> CodeLensAsync(Document document, CancellationToken ct)
    {
        if (document.FilePath is not { Length: > 0 } path)
            return [];

        var model = await document.GetSemanticModelAsync(ct);
        var root = await document.GetSyntaxRootAsync(ct);
        var text = await document.GetTextAsync(ct);

        if (model is null || root is null || MediatorTypes.For(model.Compilation) is not { } types)
            return [];

        string uri = LspConverters.PathToUri(path);
        var lenses = new List<LspCodeLens>();

        foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            ct.ThrowIfCancellationRequested();

            if (model.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol handler
                || MediatorSymbols.HandlerInterfacesOf(handler).Length == 0)
            {
                continue;
            }

            lenses.Add(Lens(uri, text, declaration.Identifier.Span));

            foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(method, ct) is IMethodSymbol handle
                    && MediatorSymbols.IsHandleMethod(handle, types))
                {
                    lenses.Add(Lens(uri, text, method.Identifier.Span));
                }
            }
        }

        return lenses;
    }

    public async Task<LspCodeLens?> ResolveCodeLensAsync(LspCodeLens lens, CancellationToken ct)
    {
        if (lens.Data is not { Kind: SendersLensKind } data)
            return null;

        object[] arguments = [data.Uri, data.Line, data.Character, Array.Empty<LspLocation>()];
        var empty = lens with { Command = new Command("0 senders", ShowReferencesCommand, arguments) };

        var resolved = await HandlerHelpers.ResolveAsync(
            new TextDocumentIdentifier(data.Uri), new Position(data.Line, data.Character), ct);
        if (resolved is not var (document, _, offset) || document is null)
            return empty;

        if (await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct) is not { } symbol)
            return empty;

        var sites = await SitesForAsync(symbol, document.Project, ct);
        var locations = await HandlerHelpers.ToLocationsAsync(
            sites.Select(s => s.Location), document.Project, ct);

        return lens with
        {
            Command = new Command(
                locations.Length == 1 ? "1 sender" : $"{locations.Length} senders",
                ShowReferencesCommand,
                [data.Uri, data.Line, data.Character, locations]),
        };
    }

    /// <summary>The client-side command, shared with the C# reference lens.</summary>
    private const string ShowReferencesCommand = "roslynSense.showReferences";

    /// <summary>
    /// The inner gate. A caret on a local, a field or a namespace cannot be either end of a
    /// dispatch, and declining on the symbol kind costs nothing where resolving a compilation does
    /// not — every registered pack is asked about every C# symbol in the solution.
    /// </summary>
    private async Task<IReadOnlyList<MediatorDispatchSite>> SitesForAsync(
        ISymbol symbol, Project project, CancellationToken ct) =>
        InterestingSymbolKinds.Contains(symbol.Kind)
            ? await MediatorReferenceService.FindAsync(symbol, project, ct)
            : [];

    private static LspCodeLens Lens(string uri, SourceText text, TextSpan identifier)
    {
        var range = LspConverters.ToRange(text.Lines, identifier);

        return new LspCodeLens(range, Command: null)
        {
            // The pack id is what brings codeLens/resolve back here: the document is C# and belongs
            // to no pack, so the URI cannot say who emitted this.
            Data = new CodeLensData(
                uri, range.Start.Line, range.Start.Character, SendersLensKind, PackId),
        };
    }
}
