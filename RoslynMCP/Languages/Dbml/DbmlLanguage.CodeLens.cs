using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using LspCodeLens = RoslynMCP.Lsp.Protocol.CodeLens;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.Dbml;

internal sealed partial class DbmlLanguage : ILanguageCodeLensProvider, ILanguageCodeLensGeneration
{
    /// <summary>
    /// What a lens count on a <c>.dbml</c> depends on, so <see cref="CodeLensResolveMemo"/> can tell
    /// when one is still good.
    /// </summary>
    /// <remarks>
    /// Three snapshots, each covering a way the answer moves. <c>Text</c> is a new instance on every
    /// keystroke, so an edited model is never answered from the old one. <c>Index</c> is replaced
    /// whenever the compilation or SqlMetal's output moves, so a regenerated designer is never
    /// answered from the old binding. <c>Solution</c> is replaced when projects are grafted in — which
    /// matters more here than anywhere: a <c>DataContext</c> is in a data-access project and every one
    /// of its consumers is somewhere else, so a count taken before they loaded would read zero over a
    /// column the whole solution selects.
    /// </remarks>
    private sealed record LensGeneration(SourceText Text, DbmlGeneratedIndex Index, Solution Solution);

    public async ValueTask<object?> LensGenerationAsync(string uri, CancellationToken ct)
    {
        if (await DbmlWorkspace.GetAsync(LspConverters.UriToPath(uri), ct) is not { } view)
            return null;

        return view.Project is { } project
            ? new LensGeneration(view.Text, view.Index, project.Solution)
            : null;
    }

    /// <summary>As many as the peek window can usefully show, matching the C# handler.</summary>
    private const int MaxLensLocations = 100;

    private const string ReferencesKind = "references";

    /// <summary>
    /// A count over every declaration the model generates code for: the context, each table, each
    /// entity type, each column, each association and each function.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counts are the reason to open a <c>.dbml</c> rather than a nicety on top of it. A model
    /// accumulates columns nobody selects and associations nobody traverses, and there is no way to
    /// tell from the file which — the generated property is what the solution names, and the reader
    /// would have to know what SqlMetal called it before they could ask. A number in the gutter is
    /// that question answered before it is asked, and it is what makes dropping a column something a
    /// reader can decide rather than guess.
    /// </para>
    /// <para>
    /// Nothing is produced when the index bound nothing. Every lens would read "0 references" over a
    /// column that is used, and a wrong number is worse than no number — a project that has not been
    /// built has no designer, and the information diagnostic on the file is what says so.
    /// </para>
    /// <para>
    /// The counting is deferred to <see cref="ResolveCodeLensAsync"/>: codeLens is re-requested on
    /// every edit and every scroll, and each count is a solution-wide <c>SymbolFinder</c> sweep. A
    /// model with sixty columns would otherwise start sixty of them per keystroke.
    /// </para>
    /// </remarks>
    public async Task<LspCodeLens[]> CodeLensAsync(CodeLensParams p, CancellationToken ct)
    {
        string uri = p.TextDocument.Uri;

        if (await DbmlWorkspace.GetAsync(LspConverters.UriToPath(uri), ct) is not { } view
            || view.Database.IsEmpty)
        {
            return [];
        }

        var lines = view.Text.Lines;
        var lenses = new List<LspCodeLens>();

        // On the <Database> element, so it reads as the file's own action the way the per-table
        // refresh reads as the table's. Like refresh it needs no binding — it reads the catalogue
        // and writes the file — so it is offered on a model whose project has never been built.
        if (Spanned(view.Database))
        {
            lenses.Add(new LspCodeLens(
                LspConverters.ToRange(lines, LensSpan(view.Database)),
                new Command("Add from database", "roslynSense.dbmlAddFromDatabase", [uri])));
        }

        // Refresh does not depend on the binding — it reads the database and rewrites the file — so
        // it is offered on a model whose project has never been built, which is exactly when a table
        // is most likely to be half-written.
        foreach (var table in view.Database.Tables)
        {
            if (Spanned(table))
                lenses.Add(RefreshLens(uri, lines, LensSpan(table), table.Name));
        }

        // Nothing to count until SqlMetal has run, and a lens is a number or it is noise.
        if (view.Index.IsEmpty)
            return [.. lenses];

        foreach (var declaration in view.Database.AllDeclarations())
        {
            ct.ThrowIfCancellationRequested();

            // A span the reader cannot see is a lens sitting on line zero. An element that produced
            // no selection span did not parse far enough to be worth a number.
            if (Spanned(declaration))
                lenses.Add(UnresolvedLens(uri, lines, LensSpan(declaration), ReferencesKind));
        }

        return [.. lenses];
    }

    private static bool Spanned(IDbmlDeclaration declaration) =>
        !declaration.SelectionSpan.IsEmpty || !declaration.Span.IsEmpty;

    /// <summary>
    /// The <b>Refresh table</b> action, carrying its command already — there is nothing to compute
    /// for it, and a lens the editor has to make a second round trip for before it can render a fixed
    /// label would be a request per table per scroll for no information.
    /// </summary>
    /// <remarks>
    /// The client command rather than one of the server's three: choosing a connection and confirming
    /// a removal are both questions for the user, and the server has no way to ask one. The client
    /// handler runs the quick-pick, calls <c>dbmlPlanRefresh</c>, shows what it says, and only then
    /// calls <c>dbmlApplyRefresh</c>.
    /// </remarks>
    private static LspCodeLens RefreshLens(
        string uri, TextLineCollection lines, TextSpan span, string tableName) =>
        new(LspConverters.ToRange(lines, span),
            new Command("Refresh table", "roslynSense.dbmlRefreshTable", [uri, tableName]));

    /// <summary>
    /// The span the lens sits above.
    /// </summary>
    /// <remarks>
    /// The selection span, which is the <c>Member</c> or <c>Name</c> attribute value — so the lens
    /// lands on the element's own line rather than on the line the tag happens to start on when the
    /// element is wrapped. The whole element is the fallback for a declaration that has neither.
    /// </remarks>
    private static TextSpan LensSpan(IDbmlDeclaration declaration) =>
        declaration.SelectionSpan.IsEmpty ? declaration.Span : declaration.SelectionSpan;

    /// <summary>
    /// Counts one visible lens: every call site of the member the element generated.
    /// </summary>
    /// <remarks>
    /// Through <see cref="DbmlReferenceService"/> rather than <c>SymbolFinder</c> directly, so the
    /// number in the gutter is the number the peek window shows and Shift+F12 lists. The designer's
    /// own mentions are excluded there, which is what the count is for: without it every column would
    /// read five or six before anyone had used it once. No search budget is passed — this runs as the
    /// view scrolls rather than because the user asked, and waiting on projects to load would make
    /// scrolling the model the slowest thing in the editor.
    /// </remarks>
    public async Task<LspCodeLens> ResolveCodeLensAsync(LspCodeLens lens, CancellationToken ct)
    {
        if (lens.Data is not { Kind: ReferencesKind } data)
            return lens;

        var locations = await LensLocationsAsync(data, ct);

        // A zero-count lens still carries the command with an empty location list: LSP requires a
        // non-empty command id, and an empty peek is a sane result for a click.
        return lens with
        {
            Command = new Command(
                $"{locations.Length} {(locations.Length == 1 ? "reference" : "references")}",
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

    private static async Task<LspLocation[]> LensLocationsAsync(CodeLensData data, CancellationToken ct)
    {
        if (await DbmlWorkspace.GetAsync(LspConverters.UriToPath(data.Uri), ct) is not { } view
            || view.Project is not { } project)
        {
            return [];
        }

        int offset = LspConverters.ToOffset(view.Text, new Position(data.Line, data.Character));

        if (DbmlSymbolResolver.ResolveAt(view, offset) is not { } hit)
            return [];

        // Definitions are dropped: the declaration is the line the lens is on, and counting it would
        // put "1 reference" over every column nobody uses.
        return await UsageLocationsAsync(
            await DbmlReferenceService.FindUsagesAsync(hit, view, ct),
            project, includeDefinitions: false, ct);
    }
}
