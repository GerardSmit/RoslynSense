using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Lists and applies Roslyn code actions (quick fixes) for diagnostics at a given position.
/// </summary>
[McpServerToolType]
public static class CodeActionsTool
{
    [McpServerTool, Description(
        "List available code fixes for a diagnostic at a position in a C# file. " +
        "Provide a code snippet with [| |] delimiters around the code with the diagnostic. " +
        "Returns available fixes. Use applyIndex to apply a specific fix. " +
        "Also discovers refactorings (Extract Method, Introduce Variable, etc.) at the marked position.")]
    public static async Task<string> GetCodeActions(
        [Description("Path to the C# file.")] string filePath,
        [Description(
            "Code snippet with [| |] markers around the location with the diagnostic, " +
            "e.g. '[|MyUndefinedType|] x = null;'.")]
        string markupSnippet,
        [Description("Optional: 1-based index of the code action to apply. If omitted, only lists available actions.")]
        int? applyIndex = null,
        [Description("Approximate line number near the target snippet. Used to pick the closest match when the snippet appears multiple times.")]
        int? hintLine = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(markupSnippet))
                return "Error: markupSnippet cannot be empty.";

            if (!MarkupString.TryParse(markupSnippet, out var markup, out string? parseError))
                return $"Error: Invalid markup snippet. {parseError}";

            var errors = new StringBuilder();
            var fileCtx = await ToolHelper.ResolveFileAsync(filePath, errors, cancellationToken);
            if (fileCtx is null)
                return errors.ToString();

            // Find the marked span using the existing snippet-matching infrastructure
            if (fileCtx.Document is null)
                return "Error: File not found in project.";

            var document = fileCtx.Document;
            var sourceText = await document.GetTextAsync(cancellationToken);
            string fileContent = sourceText.ToString();

            var matches = MarkupSymbolResolver.FindAllOccurrences(fileContent, markup!.PlainText);
            if (matches.Count == 0)
                return "Snippet not found in file.";

            MarkupSymbolResolver.SnippetMatch match;
            if (matches.Count > 1)
            {
                if (hintLine.HasValue)
                {
                    match = matches.OrderBy(m =>
                        Math.Abs(sourceText.Lines.GetLineFromPosition(m.FileOffset).LineNumber + 1 - hintLine.Value))
                        .First();
                }
                else
                {
                    var lineNumbers = matches
                        .Select(m => sourceText.Lines.GetLineFromPosition(m.FileOffset).LineNumber + 1)
                        .ToList();
                    return $"Snippet matched {lineNumbers.Count} locations (lines {string.Join(", ", lineNumbers)}). Add surrounding context to disambiguate.";
                }
            }
            else
            {
                match = matches[0];
            }
            int markedStart = MarkupSymbolResolver.MapSnippetOffsetToFile(fileContent, match, markup.PlainText, markup.SpanStart);
            int markedEnd = MarkupSymbolResolver.MapSnippetOffsetToFile(fileContent, match, markup.PlainText, markup.SpanStart + markup.SpanLength);
            var markedSpan = new TextSpan(markedStart, markedEnd - markedStart);

            // Get diagnostics at the marked span
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel is null)
                return "Error: Unable to obtain semantic model.";

            var diagnostics = semanticModel.GetDiagnostics(markedSpan, cancellationToken)
                .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                .ToList();

            if (diagnostics.Count == 0)
            {
                // Also check syntax diagnostics
                var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
                if (syntaxTree is not null)
                {
                    diagnostics = syntaxTree.GetDiagnostics(cancellationToken)
                        .Where(d => d.Location.SourceSpan.IntersectsWith(markedSpan))
                        .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                        .ToList();
                }
            }

            // Find code fixes for diagnostics
            var allActions = new List<(CodeAction Action, string Source)>();

            if (diagnostics.Count > 0)
            {
                var codeFixProviders = GetCodeFixProviders(fileCtx.Project);
                foreach (var diagnostic in diagnostics)
                {
                    foreach (var provider in codeFixProviders)
                    {
                        if (!provider.FixableDiagnosticIds.Contains(diagnostic.Id))
                            continue;

                        var actions = new List<CodeAction>();
                        var context = new CodeFixContext(
                            document,
                            diagnostic,
                            (action, _) => actions.Add(action),
                            cancellationToken);

                        try
                        {
                            await provider.RegisterCodeFixesAsync(context);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[CodeActions] Provider {provider.GetType().Name} failed: {ex.Message}");
                        }

                        foreach (var action in actions)
                        {
                            allActions.Add((action, $"fixes {diagnostic.Id}"));
                        }
                    }
                }
            }

            // Find refactoring actions (work on any code selection, no diagnostics needed)
            var refactoringProviders = GetRefactoringProviders();
            foreach (var provider in refactoringProviders)
            {
                var actions = new List<CodeAction>();
                var context = new CodeRefactoringContext(
                    document,
                    markedSpan,
                    action => actions.Add(action),
                    cancellationToken);

                try
                {
                    await provider.ComputeRefactoringsAsync(context);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[CodeActions] Refactoring provider {provider.GetType().Name} failed: {ex.Message}");
                }

                foreach (var action in actions)
                {
                    allActions.Add((action, "refactoring"));
                }
            }

            if (allActions.Count == 0)
            {
                var result = new StringBuilder();
                if (diagnostics.Count > 0)
                {
                    result.AppendLine("## Diagnostics Found");
                    result.AppendLine();
                    foreach (var d in diagnostics)
                    {
                        var lineSpan = d.Location.GetLineSpan();
                        result.AppendLine($"- **{d.Id}** ({d.Severity}): {d.GetMessage()} (line {lineSpan.StartLinePosition.Line + 1})");
                    }
                    result.AppendLine();
                }
                result.AppendLine("No code actions or refactorings available at this position.");
                return result.ToString();
            }

            // Apply a specific action if requested
            if (applyIndex is not null)
            {
                if (applyIndex < 1 || applyIndex > allActions.Count)
                    return $"Error: applyIndex must be between 1 and {allActions.Count}.";

                var (actionToApply, _) = allActions[applyIndex.Value - 1];
                var operations = await actionToApply.GetOperationsAsync(cancellationToken);

                foreach (var op in operations)
                {
                    if (op is ApplyChangesOperation applyOp)
                    {
                        var newSolution = applyOp.ChangedSolution;
                        // Write changed documents to disk
                        foreach (var pid in newSolution.ProjectIds)
                        {
                            var oldProj = fileCtx.Workspace.CurrentSolution.GetProject(pid);
                            var newProj = newSolution.GetProject(pid);
                            if (oldProj is null || newProj is null) continue;

                            foreach (var docId in newProj.DocumentIds)
                            {
                                var oldDoc = oldProj.GetDocument(docId);
                                var newDoc = newProj.GetDocument(docId);
                                if (oldDoc is null || newDoc is null) continue;

                                var oldText = await oldDoc.GetTextAsync(cancellationToken);
                                var newText = await newDoc.GetTextAsync(cancellationToken);

                                if (oldText.ToString() != newText.ToString() && newDoc.FilePath is not null)
                                {
                                    await File.WriteAllTextAsync(newDoc.FilePath, newText.ToString(), cancellationToken);
                                }
                            }
                        }
                    }
                }

                ProjectIndexCacheService.InvalidateProject(fileCtx.ProjectPath);
                return $"Applied code fix: {actionToApply.Title}";
            }

            // List available actions
            var sb = new StringBuilder();
            if (diagnostics.Count > 0)
            {
                sb.AppendLine("## Diagnostics");
                sb.AppendLine();
                foreach (var d in diagnostics.DistinctBy(d => d.Id + d.GetMessage()))
                {
                    var lineSpan = d.Location.GetLineSpan();
                    sb.AppendLine($"- **{d.Id}** ({d.Severity}): {d.GetMessage()} (line {lineSpan.StartLinePosition.Line + 1})");
                }
                sb.AppendLine();
            }

            sb.AppendLine($"## Available Code Actions ({allActions.Count})");
            sb.AppendLine();

            for (int i = 0; i < allActions.Count; i++)
            {
                var (action, source) = allActions[i];
                sb.AppendLine($"{i + 1}. **{action.Title}** ({source})");
            }

            sb.AppendLine();
            sb.AppendLine("Use `applyIndex` parameter to apply a specific action.");

            return sb.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CodeActions] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private static IReadOnlyList<CodeFixProvider>? s_cachedProviders;

    private static IReadOnlyList<CodeFixProvider> GetCodeFixProviders(Project project)
    {
        if (s_cachedProviders is not null)
            return s_cachedProviders;

        var providers = new List<CodeFixProvider>();

        // Load built-in Roslyn C# code fix providers from the Features assembly
        try
        {
            var featuresAssembly = System.Reflection.Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features");

            foreach (var type in featuresAssembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(CodeFixProvider).IsAssignableFrom(type))
                    continue;

                try
                {
                    if (Activator.CreateInstance(type) is CodeFixProvider provider)
                        providers.Add(provider);
                }
                catch
                {
                    // Some providers require dependencies or special constructors
                }
            }
        }
        catch
        {
            // Features assembly may not be available
        }

        s_cachedProviders = providers;
        return providers;
    }

    private static IReadOnlyList<CodeRefactoringProvider>? s_cachedRefactoringProviders;

    private static IReadOnlyList<CodeRefactoringProvider> GetRefactoringProviders()
    {
        if (s_cachedRefactoringProviders is not null)
            return s_cachedRefactoringProviders;

        var providers = new List<CodeRefactoringProvider>();

        try
        {
            var featuresAssembly = System.Reflection.Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features");

            foreach (var type in featuresAssembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(CodeRefactoringProvider).IsAssignableFrom(type))
                    continue;

                try
                {
                    if (Activator.CreateInstance(type) is CodeRefactoringProvider provider)
                        providers.Add(provider);
                }
                catch
                {
                    // Some providers require dependencies or special constructors
                }
            }
        }
        catch
        {
            // Features assembly may not be available
        }

        s_cachedRefactoringProviders = providers;
        return providers;
    }
}
