using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using CodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;
using LspCodeAction = RoslynMCP.Lsp.Protocol.CodeAction;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>textDocument/codeAction: quick fixes for diagnostics in the range + refactorings
/// at the selection. The initial response carries titles only; the workspace edit is computed
/// lazily in codeAction/resolve for the action the user actually picks.</summary>
internal static class CodeActionHandler
{
    private const int MaxActions = 25;

    /// <summary>Analyzer diagnostics now compete for these slots alongside compiler ones,
    /// so the range budget is wider than the compiler-only original.</summary>
    private const int MaxDiagnosticsPerRange = 25;

    /// <summary>
    /// Slots that fixes and refactorings cannot take, held for suppress and configure.
    /// </summary>
    /// <remarks>
    /// Without a reservation these lose every time: they are computed last, and a diagnostic
    /// with several fixes fills the budget before they are reached. Losing them is the specific
    /// failure that matters — a rule you cannot turn off from the editor is one you disable
    /// globally instead.
    /// </remarks>
    private const int ReservedForConfiguration = 6;

    /// <summary>
    /// The picker the client runs for a collapsed group: [group]. Handled entirely in the
    /// editor — it shows the choices, then asks for the chosen leaf through
    /// <c>codeAction/resolve</c> like any other action.
    /// </summary>
    /// <remarks>
    /// Deliberately absent from <see cref="ExecuteCommandHandler.Commands"/>. An id advertised
    /// there is one the client forwards straight back to the server as
    /// <c>workspace/executeCommand</c>, which would take the command away from the extension
    /// that implements it.
    /// </remarks>
    public const string PickNestedActionCommand = "roslynSense.pickCodeAction";

    public static async Task<LspCodeAction[]> CodeActionsAsync(
        CodeActionParams p, LspResolveCache cache, CancellationToken ct,
        bool clientPicksNestedActions = false)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document is null)
            return Array.Empty<LspCodeAction>();

        var text = await document.GetTextAsync(ct);
        var span = LspConverters.ToTextSpan(text, p.Range);

        var actions = new List<Offered>();
        var configuration = new List<Offered>();
        int fixBudget = MaxActions - ReservedForConfiguration;

        // Quick fixes for diagnostics intersecting the range. Analyzer diagnostics come from
        // cache only — computing them inside a lightbulb request would stall the UI, and the
        // publisher has almost always populated the cache by the time the user asks.
        var model = await document.GetSemanticModelAsync(ct);
        if (model is not null)
        {
            var analyzerDiagnostics = AnalyzerDiagnosticCache.TryGet(
                document, await AnalyzerDiagnosticCache.GetVersionAsync(document, ct));

            var diagnostics = DiagnosticsHandler
                .Merge(model.GetDiagnostics(cancellationToken: ct), analyzerDiagnostics)
                .Where(d => d.Location.IsInSource && d.Location.SourceSpan.IntersectsWith(span))
                .Where(d => d.Severity is DiagnosticSeverity.Error
                    or DiagnosticSeverity.Warning or DiagnosticSeverity.Info)
                .Take(MaxDiagnosticsPerRange)
                .ToList();

            foreach (var diagnostic in diagnostics)
            {
                foreach (var provider in CodeFixCatalog.GetCodeFixProviders(document.Project.Solution.Workspace))
                {
                    if (actions.Count >= fixBudget)
                        break;
                    if (!provider.FixableDiagnosticIds.Contains(diagnostic.Id))
                        continue;

                    var collected = new List<CodeAction>();
                    var context = new CodeFixContext(document, diagnostic, (a, _) => collected.Add(a), ct);
                    try { await provider.RegisterCodeFixesAsync(context); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* provider crashed — skip */ }

                    actions.AddRange(collected.Select(a => new Offered(a, "quickfix", a.Title)));
                }
            }

            configuration.AddRange(await ConfigurationActionsAsync(
                document, span, diagnostics,
                new Nesting(clientPicksNestedActions, cache, document.Project.Solution), ct));
        }

        // Refactorings at the selection.
        foreach (var provider in CodeFixCatalog.GetRefactoringProviders(document.Project.Solution.Workspace))
        {
            if (actions.Count >= fixBudget)
                break;

            var collected = new List<CodeAction>();
            var context = new CodeRefactoringContext(document, span, a => collected.Add(a), ct);
            try { await provider.ComputeRefactoringsAsync(context); }
            catch (OperationCanceledException) { throw; }
            catch { /* provider crashed — skip */ }

            actions.AddRange(collected.Select(a => new Offered(a, "refactor", a.Title)));
        }

        return actions.Take(fixBudget)
            .Concat(configuration.Take(MaxActions - Math.Min(actions.Count, fixBudget)))
            .Select(a => a.Group?.Invoke() is { } group
                // A group has no edit of its own: picking it opens the client's list, and the
                // leaf the user lands on is resolved from its own cached id.
                ? new LspCodeAction(a.Title, a.Kind, Edit: null)
                {
                    Command = new Command(a.Title, PickNestedActionCommand, [group]),
                }
                : new LspCodeAction(a.Title, a.Kind, Edit: null)
                {
                    Data = new CodeActionData(cache.StoreAction(a.Action!, document.Project.Solution)),
                })
            .ToArray();
    }

    /// <summary>
    /// "Suppress <c>SA1600</c>", "Configure <c>SA1600</c> severity" and the rest of Roslyn's
    /// configuration fixes for the diagnostics under the cursor.
    /// </summary>
    /// <remarks>
    /// These arrive as one grouping action per diagnostic with the real choices nested inside it,
    /// and there are a lot of them: three severity families of five each turns a lightbulb into a
    /// wall. A client that implements the picker command gets the group as a single entry and
    /// opens the choices in a quick pick; every other client keeps the flattening, where the
    /// group's title is folded into the child's — "Suppress SA1600 • in Suppression File" — which
    /// is also how the group reads in Visual Studio's own submenu.
    /// </remarks>
    private static async Task<List<Offered>> ConfigurationActionsAsync(
        Document document, TextSpan span,
        List<Microsoft.CodeAnalysis.Diagnostic> diagnostics, Nesting nesting, CancellationToken ct)
    {
        var results = new List<Offered>();
        if (diagnostics.Count == 0)
            return results;

        foreach (var provider in CodeFixCatalog.GetConfigurationFixProviders(document.Project.Solution.Workspace))
        {
            if (results.Count >= ReservedForConfiguration)
                break;

            var fixable = diagnostics.Where(provider.IsFixableDiagnostic).ToList();
            if (fixable.Count == 0)
                continue;

            try
            {
                var fixes = await provider.GetFixesAsync(document, span, fixable, ct);
                foreach (var fix in fixes)
                    results.AddRange(Offer(fix.Action, nesting));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* provider crashed — skip */ }
        }

        return results;
    }

    private static IEnumerable<Offered> Offer(CodeAction action, Nesting nesting)
    {
        var nested = action.NestedActions;
        if (nested.IsDefaultOrEmpty)
            return [new Offered(action, "quickfix", action.Title)];

        if (!nesting.ClientPicks)
        {
            return nested.Select(child =>
                new Offered(child, "quickfix", $"{action.Title} • {child.Title}"));
        }

        // The ellipsis is the only warning the user gets that this entry asks a question
        // instead of editing the file, which is the one thing a lightbulb entry normally never does.
        return [new Offered(null, "quickfix", $"{action.Title}…", () => Group(action, nesting))];
    }

    /// <summary>
    /// Turns a Roslyn action tree into the payload the client's picker walks. Every leaf is
    /// cached up front — the ids have to exist before the menu is drawn, because the picker runs
    /// after the code-action request is long over and has nothing but them to ask with.
    /// </summary>
    private static NestedCodeActionGroup Group(CodeAction action, Nesting nesting)
    {
        var nested = action.NestedActions;
        return nested.IsDefaultOrEmpty
            ? new NestedCodeActionGroup(
                action.Title, nesting.Cache.StoreAction(action, nesting.Solution), null)
            : new NestedCodeActionGroup(
                action.Title, null, nested.Select(child => Group(child, nesting)).ToArray());
    }

    /// <summary>Whether this connection can render a group, and what a group's leaves are
    /// cached against.</summary>
    private readonly record struct Nesting(bool ClientPicks, LspResolveCache Cache, Solution Solution);

    /// <summary>
    /// One entry in the lightbulb: either a single <see cref="Action"/> or a collapsed
    /// <see cref="Group"/>, never both. <see cref="Title"/> is separate from the action's own so
    /// a flattened child can carry its group's name without wrapping the action itself.
    /// </summary>
    /// <remarks>
    /// A collapsed entry carries a <em>factory</em> rather than a built group. Building one caches
    /// a resolve id for every leaf, and most entries never survive the budget — a line with many
    /// diagnostics produced hundreds of cached leaves per request to show six groups, which
    /// overran the resolve cache and evicted the ids of the groups still on screen. Opening one
    /// then resolved to nothing and the menu item silently did nothing.
    /// </remarks>
    private readonly record struct Offered(
        CodeAction? Action, string Kind, string Title, Func<NestedCodeActionGroup>? Group = null);

    /// <summary>codeAction/resolve: computes the workspace edit for one cached action.</summary>
    public static async Task<LspCodeAction> ResolveAsync(
        LspCodeAction action, LspResolveCache cache, CancellationToken ct)
    {
        if (action.Data is null || cache.GetAction(action.Data.Id) is not var (roslynAction, oldSolution) || roslynAction is null)
            return action; // evicted/unknown — client will surface "no edit" on apply

        var edit = await TryResolveEditAsync(oldSolution, roslynAction, ct);
        return action with { Edit = edit };
    }

    private static async Task<WorkspaceEdit?> TryResolveEditAsync(
        Solution oldSolution, CodeAction action, CancellationToken ct)
    {
        try
        {
            var operations = await action.GetOperationsAsync(ct);
            var changed = operations.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution;
            if (changed is null)
                return null;

            var changes = new Dictionary<string, TextEdit[]>();
            var created = new List<string>();

            foreach (var projectChange in changed.GetChanges(oldSolution).GetProjectChanges())
            {
                foreach (var docId in projectChange.GetChangedDocuments())
                    await RewriteAsync(oldSolution.GetDocument(docId), changed.GetDocument(docId));

                // The whole point of "Configure IDE0074 severity" is the line it writes into
                // .editorconfig, and an analyzer config document is not among the changed
                // *documents* — so without these the entry resolved to nothing and the menu item
                // did nothing when clicked.
                foreach (var docId in projectChange.GetChangedAnalyzerConfigDocuments())
                {
                    await RewriteAsync(
                        oldSolution.GetAnalyzerConfigDocument(docId),
                        changed.GetAnalyzerConfigDocument(docId));
                }

                // A fix that writes somewhere the project does not have yet: the first severity
                // configured in a project with no .editorconfig, or "Suppress in Suppression File"
                // before GlobalSuppressions.cs exists.
                foreach (var docId in projectChange.GetAddedDocuments())
                    await CreateAsync(changed.GetDocument(docId));

                foreach (var docId in projectChange.GetAddedAnalyzerConfigDocuments())
                    await CreateAsync(changed.GetAnalyzerConfigDocument(docId));
            }

            if (changes.Count == 0)
                return null;

            // No new files means no resource operations, and the simple form every client reads.
            if (created.Count == 0)
                return new WorkspaceEdit(changes);

            // Creations first: the edits that follow write into the files they make.
            object[] documentChanges =
            [
                .. created.Select(uri => (object)new CreateFile(uri)),
                .. changes.Select(entry => (object)new TextDocumentEdit(
                    new OptionalVersionedTextDocumentIdentifier(entry.Key), entry.Value)),
            ];
            return new WorkspaceEdit(changes, documentChanges);

            async Task RewriteAsync(TextDocument? oldDoc, TextDocument? newDoc)
            {
                if (oldDoc?.FilePath is not { Length: > 0 } path || newDoc is null)
                    return;

                var oldText = await oldDoc.GetTextAsync(ct);
                var newText = await newDoc.GetTextAsync(ct);

                // Documents carry tracked changes and give a tighter diff than comparing texts;
                // every other document kind has only its text.
                var textChanges = oldDoc is Document typedOld && newDoc is Document typedNew
                    ? await typedNew.GetTextChangesAsync(typedOld, ct)
                    : newText.GetTextChanges(oldText);

                var edits = textChanges
                    .Select(c => new TextEdit(LspConverters.ToRange(oldText.Lines, c.Span), c.NewText ?? ""))
                    .ToArray();
                if (edits.Length > 0)
                    changes[LspConverters.PathToUri(path)] = edits;
            }

            async Task CreateAsync(TextDocument? newDoc)
            {
                if (newDoc?.FilePath is not { Length: > 0 } path)
                    return;

                var uri = LspConverters.PathToUri(path);
                var text = await newDoc.GetTextAsync(ct);
                created.Add(uri);
                changes[uri] = [new TextEdit(
                    new Protocol.Range(new Position(0, 0), new Position(0, 0)), text.ToString())];
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null; // action can't produce a preview — drop it
        }
    }
}
