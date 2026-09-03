using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using LspCodeLens = RoslynMCP.Lsp.Protocol.CodeLens;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.Proto;

internal sealed partial class ProtoLanguage : ILanguageCodeLensProvider, ILanguageCodeLensGeneration
{
    /// <summary>
    /// What a lens count on a <c>.proto</c> depends on, so <see cref="CodeLensResolveMemo"/> can
    /// tell when one is still good.
    /// </summary>
    /// <remarks>
    /// Three snapshots, and each covers a way the answer moves. <c>Text</c> is a new instance on
    /// every keystroke, so an edited schema is never answered from the old one. <c>Index</c> is
    /// replaced whenever the compilation or protoc's output moves, so regenerated code is never
    /// answered from the old generation. <c>Solution</c> is replaced when projects are grafted in,
    /// so a count taken while only the contracts project was loaded does not outlive the arrival of
    /// the consumers that widen it — which is the case that would otherwise leave "0 references"
    /// over an rpc the whole solution calls.
    /// </remarks>
    private sealed record LensGeneration(SourceText Text, ProtoGeneratedIndex Index, Solution Solution);

    public async ValueTask<object?> LensGenerationAsync(string uri, CancellationToken ct)
    {
        if (await ProtoWorkspace.GetAsync(LspConverters.UriToPath(uri), ct) is not { } view)
            return null;

        return view.Project is { } project
            ? new LensGeneration(view.Text, view.Index, project.Solution)
            : null;
    }

    /// <summary>As many as the peek window can usefully show, matching the C# handler.</summary>
    private const int MaxLensLocations = 100;

    /// <summary>A <c>service</c>: the classes deriving from the base protoc generated for it.</summary>
    private const string ImplementationsKind = "implementations";

    /// <summary>An <c>rpc</c>: every call site of the methods it generates.</summary>
    private const string ReferencesKind = "references";

    /// <summary>
    /// A count over every declaration a <c>.proto</c> generates code for: implementations for a
    /// <c>service</c>, and call sites for an <c>rpc</c>, a <c>message</c>, a field, an <c>enum</c>
    /// and an enum value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A message and a field earn a lens for the same reason an rpc does. The class protoc writes
    /// for a message is what a consumer constructs and the property it writes for a field is what a
    /// consumer reads, so "who uses this" is a question about the schema and not about the
    /// generated file — and it is the one question the gutter can answer without the user having to
    /// know the C# name protoc chose.
    /// </para>
    /// <para>
    /// A <c>oneof</c> is left out on purpose: its generated members carry none of protoc's anchors,
    /// so nothing binds back to it and the lens could only ever read zero.
    /// </para>
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

        // Flattened, so a message nested three deep and a field inside a oneof are reached on the
        // same walk as a top-level service.
        foreach (var declaration in view.Parse.AllDeclarations)
        {
            ct.ThrowIfCancellationRequested();

            foreach (string kind in LensKindsFor(declaration.Kind))
                lenses.Add(UnresolvedLens(uri, lines, declaration.Name.Span, kind));
        }

        return [.. lenses];
    }

    private static readonly string[] s_serviceKinds = [ImplementationsKind, ReferencesKind];
    private static readonly string[] s_referenceKind = [ReferencesKind];

    /// <summary>
    /// A service earns both counts, in the order C# puts them: who implements the contract, and
    /// who uses it.
    /// </summary>
    /// <remarks>
    /// Implementations alone was the reported gap. A service is the one declaration whose two
    /// questions have different answers — the server deriving from its base, and every place the
    /// generated client is injected, registered or called — and offering only the first makes the
    /// second look like it was answered.
    /// </remarks>
    private static string[] LensKindsFor(ProtoDeclarationKind kind) => kind switch
    {
        ProtoDeclarationKind.Service => s_serviceKinds,

        ProtoDeclarationKind.Rpc
            or ProtoDeclarationKind.Message
            or ProtoDeclarationKind.Field
            or ProtoDeclarationKind.Enum
            or ProtoDeclarationKind.EnumValue => s_referenceKind,

        _ => [],
    };

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

        var lensWatch = System.Diagnostics.Stopwatch.StartNew();
        var locations = await LensLocationsAsync(data, ct);
        lensWatch.Stop();

        // A code lens resolves on every scroll, so its cost is paid continuously rather than once.
        // The threshold is set above the warm-up curve a cold contracts project produces — measured
        // 335/257/132/97/75 ms and then under 20 ms as the compilation and the search caches fill —
        // so this reports a lens that is genuinely slow rather than narrating a normal first scroll.
        if (lensWatch.ElapsedMilliseconds >= 500)
        {
            Console.Error.WriteLine(
                $"[Proto] codeLens/resolve {data.Kind} at {data.Line}:{data.Character} took " +
                $"{lensWatch.ElapsedMilliseconds} ms ({locations.Length} location(s)).");
        }

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
