using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using LspCodeLens = RoslynMCP.Lsp.Protocol.CodeLens;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.Proto;

internal sealed partial class ProtoLanguage : ILanguageCodeLensProvider
{
    /// <summary>As many as the peek window can usefully show, matching the C# handler.</summary>
    private const int MaxLensLocations = 100;

    /// <summary>A <c>service</c>: the classes deriving from the base protoc generated for it.</summary>
    private const string ImplementationsKind = "implementations";

    /// <summary>An <c>rpc</c>: every call site of the methods it generates.</summary>
    private const string ReferencesKind = "references";

    /// <summary>
    /// A count over every <c>service</c> and every <c>rpc</c> — the two declarations in a
    /// <c>.proto</c> that hand-written code is on the other end of. A message is a generated class
    /// nothing derives from and a field is a generated property, so neither has an answer worth a
    /// line in the gutter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is produced when the project has not been built. Every lens would read "0
    /// implementations" over a contract that is implemented, and a wrong number is worse than no
    /// number — the information diagnostic on the file is what says why the counts are missing.
    /// </para>
    /// <para>
    /// The counting itself is deferred to <see cref="ResolveCodeLensAsync"/>, so only the lenses the
    /// editor actually renders pay for a solution-wide search. codeLens is re-requested on every
    /// edit and every scroll, and both counts here are <c>SymbolFinder</c> sweeps.
    /// </para>
    /// </remarks>
    public async Task<LspCodeLens[]> CodeLensAsync(CodeLensParams p, CancellationToken ct)
    {
        string uri = p.TextDocument.Uri;

        if (await ProtoWorkspace.GetAsync(LspConverters.UriToPath(uri), ct) is not { } view)
            return [];

        // Nothing to count until protoc has run, and a lens is a number or it is noise.
        if (view.Index.IsEmpty)
            return [];

        var lines = view.Text.Lines;
        var lenses = new List<LspCodeLens>();

        foreach (var service in view.Parse.Services)
        {
            ct.ThrowIfCancellationRequested();

            lenses.Add(UnresolvedLens(uri, lines, service.Name.Span, ImplementationsKind));

            foreach (var rpc in service.Rpcs)
                lenses.Add(UnresolvedLens(uri, lines, rpc.Name.Span, ReferencesKind));
        }

        return [.. lenses];
    }

    /// <summary>
    /// Counts one visible lens: the implementations of a service, or the call sites of an rpc.
    /// </summary>
    /// <remarks>
    /// Both go through <see cref="ProtoReferenceService"/> rather than through <c>SymbolFinder</c>
    /// directly, so the number in the gutter is the same number the peek window and the
    /// <c>find_usages</c> tool report. One proto declaration is several C# symbols — an rpc is a
    /// virtual method, its overrides and four or five client overloads — and a lens that searched
    /// any one of them alone would disagree with every other view of the same caret. The locations
    /// are converted by <see cref="ProtoNavigationHandler"/> for the same reason, one layer down:
    /// a result in a source-generated document has no file to open, and clicking the lens has to
    /// land where Shift+F12 lands.
    /// </remarks>
    public async Task<LspCodeLens> ResolveCodeLensAsync(LspCodeLens lens, CancellationToken ct)
    {
        if (lens.Data is not { } data
            || (data.Kind != ImplementationsKind && data.Kind != ReferencesKind))
        {
            return lens;
        }

        var locations = await LensLocationsAsync(data, ct);

        // A zero-count lens still carries the command with an empty location list: LSP requires a
        // non-empty command id, and an empty peek is a sane result for a click.
        string noun = data.Kind == ImplementationsKind
            ? locations.Length == 1 ? "implementation" : "implementations"
            : locations.Length == 1 ? "reference" : "references";

        return lens with
        {
            Command = new Command(
                $"{locations.Length} {noun}",
                "roslynSense.showReferences",
                [data.Uri, data.Line, data.Character, locations.Take(MaxLensLocations).ToArray()]),
        };
    }

    private static LspCodeLens UnresolvedLens(
        string uri, TextLineCollection lines, TextSpan span, string kind)
    {
        var start = lines.GetLinePosition(span.Start);

        return new LspCodeLens(LspConverters.ToRange(lines, span), Command: null)
        {
            Data = new CodeLensData(uri, start.Line, start.Character, kind),
        };
    }

    private static async Task<LspLocation[]> LensLocationsAsync(
        CodeLensData data, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(data.Uri);

        if (await ProtoWorkspace.GetAsync(path, ct) is not { } view)
            return [];

        if (view.Project is not { } project)
            return [];

        int offset = LspConverters.ToOffset(view.Text, new Position(data.Line, data.Character));

        if (ProtoSymbolResolver.ResolveAt(view, offset) is not { } hit)
            return [];

        // Definitions are dropped from a reference count: an rpc has several by construction — the
        // base's virtual method, the client's overloads and every hand-written override — so
        // keeping them would read "5 references" over an rpc nobody calls.
        return data.Kind == ImplementationsKind
            ? await ProtoNavigationHandler.SymbolLocationsAsync(
                await ProtoReferenceService.FindImplementationsAsync(hit, view.Index, project, ct),
                project, ct)
            : await ProtoNavigationHandler.UsageLocationsAsync(
                await ProtoReferenceService.FindUsagesAsync(hit, view.Index, project, ct),
                project, includeDefinitions: false, ct);
    }
}
