using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using CodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;
using LspCodeAction = RoslynMCP.Lsp.Protocol.CodeAction;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>textDocument/codeAction: quick fixes for diagnostics in the range + refactorings
/// at the selection. Edits are resolved eagerly into a <see cref="WorkspaceEdit"/> (no
/// codeAction/resolve round-trip; the action count is capped to keep this bounded).</summary>
internal static class CodeActionHandler
{
    private const int MaxActions = 25;

    public static async Task<LspCodeAction[]> CodeActionsAsync(CodeActionParams p, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document is null)
            return Array.Empty<LspCodeAction>();

        var text = await document.GetTextAsync(ct);
        var span = LspConverters.ToTextSpan(text, p.Range);

        var actions = new List<(CodeAction Action, string Kind)>();

        // Quick fixes for diagnostics intersecting the range.
        var model = await document.GetSemanticModelAsync(ct);
        if (model is not null)
        {
            var diagnostics = model.GetDiagnostics(cancellationToken: ct)
                .Where(d => d.Location.IsInSource && d.Location.SourceSpan.IntersectsWith(span))
                .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                .Take(10)
                .ToList();

            foreach (var diagnostic in diagnostics)
            {
                foreach (var provider in CodeFixCatalog.GetCodeFixProviders())
                {
                    if (actions.Count >= MaxActions)
                        break;
                    if (!provider.FixableDiagnosticIds.Contains(diagnostic.Id))
                        continue;

                    var collected = new List<CodeAction>();
                    var context = new CodeFixContext(document, diagnostic, (a, _) => collected.Add(a), ct);
                    try { await provider.RegisterCodeFixesAsync(context); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* provider crashed — skip */ }

                    actions.AddRange(collected.Select(a => (a, "quickfix")));
                }
            }
        }

        // Refactorings at the selection.
        foreach (var provider in CodeFixCatalog.GetRefactoringProviders())
        {
            if (actions.Count >= MaxActions)
                break;

            var collected = new List<CodeAction>();
            var context = new CodeRefactoringContext(document, span, a => collected.Add(a), ct);
            try { await provider.ComputeRefactoringsAsync(context); }
            catch (OperationCanceledException) { throw; }
            catch { /* provider crashed — skip */ }

            actions.AddRange(collected.Select(a => (a, "refactor")));
        }

        var results = new List<LspCodeAction>();
        foreach (var (action, kind) in actions.Take(MaxActions))
        {
            var edit = await TryResolveEditAsync(document.Project.Solution, action, ct);
            if (edit is not null)
                results.Add(new LspCodeAction(action.Title, kind, edit));
        }
        return results.ToArray();
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
