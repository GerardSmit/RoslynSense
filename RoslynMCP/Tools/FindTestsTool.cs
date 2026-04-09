using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class FindTestsTool
{
    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "FactAttribute",
        "Theory", "TheoryAttribute",
        "Test", "TestAttribute",
        "TestMethod", "TestMethodAttribute",
        "TestCase", "TestCaseAttribute",
        "InlineData", "InlineDataAttribute"
    };

    private static readonly HashSet<string> TestNamespaces = new(StringComparer.Ordinal)
    {
        "Xunit",
        "NUnit.Framework",
        "Microsoft.VisualStudio.TestTools.UnitTesting"
    };

    /// <summary>
    /// Finds test methods that reference a given symbol.
    /// </summary>
    [McpServerTool, Description(
        "Find test methods that reference a symbol. Provide a code snippet with [| |] " +
        "delimiters around the target symbol. Returns test method names, file paths, and line numbers.")]
    public static async Task<string> FindTests(
        [Description("Path to the file containing the symbol.")] string filePath,
        [Description(
            "Code snippet with [| |] markers around the target symbol, " +
            "e.g. 'public void [|ProcessData|](string input)'.")]
        string markupSnippet,
        [Description(
            "When true, also uses cached coverage data to find tests that execute " +
            "this code at runtime. Requires RunCoverage to have been called first. Default: false.")]
        bool useCoverage = false,
        [Description("Approximate line number near the target snippet. Used to pick the closest match when the snippet appears multiple times.")]
        int? hintLine = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var errors = new StringBuilder();
            var ctx = await ToolHelper.ResolveSymbolAsync(filePath, markupSnippet, errors, cancellationToken, hintLine);
            if (ctx is null) return errors.ToString();
            if (!ctx.IsResolved) return ToolHelper.FormatResolutionError(ctx.Resolution);

            var symbol = ctx.Symbol!;
            var testMethods = new List<TestMethodInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Search in the current solution
            await SearchForTestReferencesAsync(
                symbol, ctx.Workspace.CurrentSolution, testMethods, seen, cancellationToken);

            // Search referencing projects (e.g., test projects that reference the current project)
            if (ctx.Project.FilePath is not null)
            {
                var referencingProjects = WorkspaceService.FindReferencingProjects(ctx.Project.FilePath);
                foreach (var refProjectPath in referencingProjects)
                {
                    try
                    {
                        var (refWorkspace, refProject) = await WorkspaceService.GetOrOpenProjectAsync(
                            refProjectPath, diagnosticWriter: TextWriter.Null, cancellationToken: cancellationToken);

                        // Find the equivalent symbol in the referencing workspace's solution
                        var refSolution = refWorkspace.CurrentSolution;
                        var refDoc = WorkspaceService.FindDocumentInProject(
                            refProject, ctx.File.SystemPath)
                            ?? FindDocumentInSolution(refSolution, ctx.File.SystemPath);

                        if (refDoc is null) continue;

                        var refModel = await refDoc.GetSemanticModelAsync(cancellationToken);
                        var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                        if (refModel is null || refRoot is null) continue;

                        // Re-resolve the symbol in the referencing workspace
                        var originalLocation = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                        if (originalLocation is null) continue;

                        var node = refRoot.FindNode(originalLocation.SourceSpan);
                        var refSymbol = refModel.GetDeclaredSymbol(node, cancellationToken)
                            ?? refModel.GetSymbolInfo(node, cancellationToken).Symbol;

                        if (refSymbol is null) continue;

                        await SearchForTestReferencesAsync(
                            refSymbol, refSolution, testMethods, seen, cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"[FindTests] Error searching referencing project '{refProjectPath}': {ex.Message}");
                    }
                }
            }

            // Coverage-based test discovery
            if (useCoverage)
            {
                var coverageTests = FindTestsViaCoverage(symbol, ctx.File.SystemPath);
                if (coverageTests is not null)
                {
                    foreach (var ct in coverageTests)
                    {
                        if (seen.Add(ct.FullyQualifiedName))
                            testMethods.Add(ct);
                    }
                }
            }

            if (testMethods.Count == 0)
            {
                if (useCoverage)
                    return "No test methods found that reference this symbol (static analysis + coverage). " +
                           "Ensure RunCoverage has been called to update coverage data.";
                return "No test methods found that reference this symbol.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Found {testMethods.Count} test method(s) referencing `{symbol.Name}`:");
            sb.AppendLine();

            foreach (var group in testMethods.GroupBy(t => t.ClassName))
            {
                sb.AppendLine($"**{group.Key}**");
                foreach (var test in group)
                {
                    sb.AppendLine($"  - {test.MethodName} ({test.FilePath}:{test.Line})");
                }
            }

            sb.AppendLine();
            sb.AppendLine("**dotnet test filter:**");
            if (testMethods.Count == 1)
            {
                sb.AppendLine($"`--filter \"FullyQualifiedName={testMethods[0].FullyQualifiedName}\"`");
            }
            else
            {
                sb.AppendLine($"`--filter \"FullyQualifiedName~{testMethods[0].ClassName}\"`");
            }

            return sb.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static async Task SearchForTestReferencesAsync(
        ISymbol symbol,
        Solution solution,
        List<TestMethodInfo> testMethods,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);

        foreach (var reference in references)
        {
            foreach (var location in reference.Locations)
            {
                var doc = location.Document;
                var syntaxTree = await doc.GetSyntaxTreeAsync(cancellationToken);
                var semanticModel = await doc.GetSemanticModelAsync(cancellationToken);
                if (syntaxTree is null || semanticModel is null) continue;

                var root = await syntaxTree.GetRootAsync(cancellationToken);
                var node = root.FindNode(location.Location.SourceSpan);

                var method = FindEnclosingTestMethod(node, semanticModel);
                if (method is null) continue;

                var methodSymbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);
                if (methodSymbol is null) continue;

                var fqn = $"{methodSymbol.ContainingType.ToDisplayString()}.{methodSymbol.Name}";
                if (!seen.Add(fqn)) continue;

                var lineSpan = method.Identifier.GetLocation().GetLineSpan();
                testMethods.Add(new TestMethodInfo(
                    fqn,
                    doc.FilePath ?? "",
                    lineSpan.StartLinePosition.Line + 1));
            }
        }
    }

    private static Document? FindDocumentInSolution(Solution solution, string filePath)
    {
        foreach (var project in solution.Projects)
        {
            var doc = WorkspaceService.FindDocumentInProject(project, filePath);
            if (doc is not null) return doc;
        }
        return null;
    }

    private static MethodDeclarationSyntax? FindEnclosingTestMethod(
        SyntaxNode node, SemanticModel semanticModel)
    {
        var current = node;
        while (current is not null)
        {
            if (current is MethodDeclarationSyntax method)
            {
                if (IsTestMethod(method, semanticModel))
                    return method;
                return null;
            }

            if (current is ClassDeclarationSyntax)
                return null;

            current = current.Parent;
        }
        return null;
    }

    private static bool IsTestMethod(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        foreach (var attrList in method.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();

                // Quick check by name
                if (TestAttributes.Contains(name))
                    return true;

                // Check with semantic model for full qualification
                var symbol = semanticModel.GetSymbolInfo(attr).Symbol;
                if (symbol is IMethodSymbol ctorSymbol)
                {
                    var ns = ctorSymbol.ContainingType.ContainingNamespace?.ToDisplayString();
                    if (ns != null && TestNamespaces.Contains(ns))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Attempts to find tests that cover a symbol's source location using cached coverage data.
    /// Returns null if no coverage data is available.
    /// </summary>
    private static List<TestMethodInfo>? FindTestsViaCoverage(ISymbol symbol, string filePath)
    {
        var data = CoverageService.GetCachedCoverage(out _, out _);
        if (data is null) return null;

        // Get the full span of the symbol (declaration through end of body)
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null) return null;

        var lineSpan = location.GetLineSpan();
        int startLine = lineSpan.StartLinePosition.Line + 1;
        int endLine = lineSpan.EndLinePosition.Line + 1;

        // For methods, try to get the full body span
        if (symbol is IMethodSymbol)
        {
            foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
            {
                var syntax = syntaxRef.GetSyntax();
                var fullSpan = syntax.GetLocation().GetLineSpan();
                startLine = Math.Min(startLine, fullSpan.StartLinePosition.Line + 1);
                endLine = Math.Max(endLine, fullSpan.EndLinePosition.Line + 1);
            }
        }

        var fileCov = CoverageService.GetFileCoverage(filePath);
        if (fileCov is null) return null;

        // Find methods in coverage data whose lines overlap with the symbol's span
        var coveredLines = fileCov.Lines
            .Where(kv => kv.Key >= startLine && kv.Key <= endLine && kv.Value.Hits > 0)
            .Select(kv => kv.Key)
            .ToHashSet();

        if (coveredLines.Count == 0) return null;

        // Find which coverage methods contain these covered lines
        var coveringMethods = fileCov.Methods
            .Where(m => m.Lines.Any(l => coveredLines.Contains(l.LineNumber)))
            .ToList();

        if (coveringMethods.Count == 0) return null;

        // For test files, the covering methods ARE the test methods.
        // For production code, the covering methods are the production methods that contain
        // the covered lines — Cobertura doesn't track which tests hit them.
        var results = new List<TestMethodInfo>();
        foreach (var method in coveringMethods)
        {
            if (!string.IsNullOrEmpty(method.FilePath) &&
                (method.FilePath.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
                 method.FilePath.Contains("Spec", StringComparison.OrdinalIgnoreCase)))
            {
                var firstLine = method.Lines.FirstOrDefault()?.LineNumber ?? 0;
                results.Add(new TestMethodInfo(method.FullName, method.FilePath, firstLine));
            }
        }

        return results.Count > 0 ? results : null;
    }

    private sealed record TestMethodInfo(string FullyQualifiedName, string FilePath, int Line)
    {
        public string ClassName => FullyQualifiedName.Contains('.')
            ? FullyQualifiedName[..FullyQualifiedName.LastIndexOf('.')]
            : "";

        public string MethodName => FullyQualifiedName.Contains('.')
            ? FullyQualifiedName[(FullyQualifiedName.LastIndexOf('.') + 1)..]
            : FullyQualifiedName;
    }
}
