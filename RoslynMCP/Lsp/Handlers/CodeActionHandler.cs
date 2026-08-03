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

    public static async Task<LspCodeAction[]> CodeActionsAsync(
        CodeActionParams p, LspResolveCache cache, CancellationToken ct)
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

            configuration.AddRange(await ConfigurationActionsAsync(document, span, diagnostics, ct));
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
            .Select(a => new LspCodeAction(a.Title, a.Kind, Edit: null)
            {
                Data = new CodeActionData(cache.StoreAction(a.Action, document.Project.Solution)),
            })
            .ToArray();
    }

    /// <summary>
    /// "Suppress <c>SA1600</c>", "Configure <c>SA1600</c> severity" and the rest of Roslyn's
    /// configuration fixes for the diagnostics under the cursor.
    /// </summary>
    /// <remarks>
    /// These arrive as one grouping action per diagnostic with the real choices nested inside
    /// it. LSP has no nested code actions, so the group is flattened and its title folded into
    /// the child's — "Suppress SA1600 • in Suppression File" — which is also how the group reads
    /// in Visual Studio's own submenu.
    /// </remarks>
    private static async Task<List<Offered>> ConfigurationActionsAsync(
        Document document, TextSpan span,
        List<Microsoft.CodeAnalysis.Diagnostic> diagnostics, CancellationToken ct)
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
                    results.AddRange(Flatten(fix.Action));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* provider crashed — skip */ }
        }

        return results;
    }

    private static IEnumerable<Offered> Flatten(CodeAction action)
    {
        var nested = action.NestedActions;
        if (nested.IsDefaultOrEmpty)
            return [new Offered(action, "quickfix", action.Title)];

        return nested.Select(child =>
            new Offered(child, "quickfix", $"{action.Title} • {child.Title}"));
    }

    /// <summary>
    /// One entry in the lightbulb. <see cref="Title"/> is separate from the action's own so a
    /// flattened child can carry its group's name without wrapping the action itself.
    /// </summary>
    private readonly record struct Offered(CodeAction Action, string Kind, string Title);

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
            foreach (var projectChange in changed.GetChanges(oldSolution).GetProjectChanges())
            {
                foreach (var docId in projectChange.GetChangedDocuments())
                {
                    var oldDoc = oldSolution.GetDocument(docId);
                    var newDoc = changed.GetDocument(docId);
                    if (oldDoc?.FilePath is not { Length: > 0 } path || newDoc is null)
                        continue;

                    var oldText = await oldDoc.GetTextAsync(ct);
                    var textChanges = await newDoc.GetTextChangesAsync(oldDoc, ct);
                    var edits = textChanges
                        .Select(c => new TextEdit(LspConverters.ToRange(oldText.Lines, c.Span), c.NewText ?? ""))
                        .ToArray();
                    if (edits.Length > 0)
                        changes[LspConverters.PathToUri(path)] = edits;
                }
            }
            return changes.Count == 0 ? null : new WorkspaceEdit(changes);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null; // action can't produce a preview — drop it
        }
    }
}
