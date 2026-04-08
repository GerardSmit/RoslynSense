using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
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
        "Returns available fixes. Use applyIndex to apply a specific fix.")]
    public static async Task<string> GetCodeActions(
        [Description("Path to the C# file.")] string filePath,
        [Description(
            "Code snippet with [| |] markers around the location with the diagnostic, " +
            "e.g. '[|MyUndefinedType|] x = null;'.")]
        string markupSnippet,
        [Description("Optional: 1-based index of the code action to apply. If omitted, only lists available actions.")]
        int? applyIndex = null,
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
            if (matches.Count > 1)
            {
                var lineNumbers = matches
                    .Select(m => sourceText.Lines.GetLineFromPosition(m.FileOffset).LineNumber + 1)
                    .ToList();
                return $"Snippet matched {lineNumbers.Count} locations (lines {string.Join(", ", lineNumbers)}). Add surrounding context to disambiguate.";
            }

            var match = matches[0];
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

            if (diagnostics.Count == 0)
                return "No diagnostics found at the marked position.";

            // Find code fixes
            var codeFixProviders = GetCodeFixProviders(fileCtx.Project);
            var allActions = new List<(CodeAction Action, Diagnostic Diagnostic)>();

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
                    catch
                    {
                        // Some providers may fail
                    }

                    foreach (var action in actions)
                    {
                        allActions.Add((action, diagnostic));
                    }
                }
            }

            if (allActions.Count == 0)
            {
                var result = new StringBuilder();
                result.AppendLine("## Diagnostics Found");
                result.AppendLine();
                foreach (var d in diagnostics)
                {
                    var lineSpan = d.Location.GetLineSpan();
                    result.AppendLine($"- **{d.Id}** ({d.Severity}): {d.GetMessage()} (line {lineSpan.StartLinePosition.Line + 1})");
                }
                result.AppendLine();
                result.AppendLine("No code fixes available for these diagnostics.");
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
            sb.AppendLine("## Diagnostics");
            sb.AppendLine();
            foreach (var d in diagnostics.DistinctBy(d => d.Id + d.GetMessage()))
            {
                var lineSpan = d.Location.GetLineSpan();
                sb.AppendLine($"- **{d.Id}** ({d.Severity}): {d.GetMessage()} (line {lineSpan.StartLinePosition.Line + 1})");
            }
            sb.AppendLine();

            sb.AppendLine($"## Available Code Fixes ({allActions.Count})");
            sb.AppendLine();

            for (int i = 0; i < allActions.Count; i++)
            {
                var (action, diag) = allActions[i];
                sb.AppendLine($"{i + 1}. **{action.Title}** (fixes {diag.Id})");
            }

            sb.AppendLine();
            sb.AppendLine("Use `applyIndex` parameter to apply a specific fix.");

            return sb.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CodeActions] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private static IReadOnlyList<CodeFixProvider> GetCodeFixProviders(Project project)
    {
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

        return providers;
    }
}
