using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class DiscoverTestsTool
{
    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "FactAttribute",
        "Theory", "TheoryAttribute",
        "Test", "TestAttribute",
        "TestMethod", "TestMethodAttribute",
        "TestCase", "TestCaseAttribute",
    };

    private static readonly HashSet<string> TestNamespaces = new(StringComparer.Ordinal)
    {
        "Xunit",
        "NUnit.Framework",
        "Microsoft.VisualStudio.TestTools.UnitTesting"
    };

    /// <summary>
    /// Discovers all test methods in a project using Roslyn semantic analysis.
    /// </summary>
    [McpServerTool, Description(
        "Discover all test methods in a .NET test project using static Roslyn analysis. " +
        "Returns test names, frameworks, file paths, and line numbers. " +
        "Useful for understanding test structure without running tests.")]
    public static async Task<string> DiscoverTests(
        [Description("Path to the test project (.csproj) or a source file in the test project.")]
        string projectPath,
        [Description("Optional class name filter (partial match). Only returns tests from matching classes.")]
        string? className = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return "Error: projectPath cannot be empty.";

            var normalizedInput = PathHelper.NormalizePath(projectPath);
            var csprojPath = PathHelper.ResolveCsprojPath(projectPath);
            if (csprojPath is null)
                return $"Error: Could not find a .csproj file for '{projectPath}'.";

            var (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(
                csprojPath, diagnosticWriter: TextWriter.Null, cancellationToken: cancellationToken);

            var tests = new List<TestInfo>();

            var sourceFileFilter = PathHelper.IsSourceFile(normalizedInput) ? normalizedInput : null;

            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (sourceFileFilter is not null && document.FilePath is not null &&
                    !string.Equals(document.FilePath, sourceFileFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                if (syntaxTree is null || semanticModel is null) continue;

                var root = await syntaxTree.GetRootAsync(cancellationToken);

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var (isTest, framework) = DetectTestMethod(method, semanticModel);
                    if (!isTest) continue;

                    var methodSymbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);
                    if (methodSymbol is null) continue;

                    var containingClass = methodSymbol.ContainingType?.Name ?? "";
                    if (className is not null &&
                        !containingClass.Contains(className, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var fqn = $"{methodSymbol.ContainingType?.ToDisplayString()}.{methodSymbol.Name}";
                    var lineSpan = method.Identifier.GetLocation().GetLineSpan();
                    var filePath = document.FilePath ?? "";

                    var endLineSpan = method.GetLocation().GetLineSpan();
                    tests.Add(new TestInfo(
                        fqn,
                        methodSymbol.Name,
                        containingClass,
                        framework,
                        filePath,
                        lineSpan.StartLinePosition.Line + 1,
                        endLineSpan.EndLinePosition.Line + 1));
                }
            }

            if (tests.Count == 0)
            {
                return className is not null
                    ? $"No test methods found matching class '{className}' in project."
                    : "No test methods found in project.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# Test Discovery: {Path.GetFileNameWithoutExtension(csprojPath)}");
            sb.AppendLine();
            sb.AppendLine($"Found **{tests.Count}** test method(s)");
            sb.AppendLine();

            foreach (var group in tests.GroupBy(t => t.ClassName).OrderBy(g => g.Key))
            {
                sb.AppendLine($"## {group.Key} ({group.First().Framework})");
                sb.AppendLine();
                sb.AppendLine("| # | Method | File | Lines |");
                sb.AppendLine("|---|--------|------|-------|");

                int i = 1;
                foreach (var test in group.OrderBy(t => t.Line))
                {
                    var projectDir = Path.GetDirectoryName(csprojPath);
                    var relPath = projectDir is not null
                        ? Path.GetRelativePath(projectDir, test.FilePath)
                        : test.FilePath;
                    string lineRange = test.EndLine > test.Line ? $"{test.Line}–{test.EndLine}" : $"{test.Line}";
                    sb.AppendLine($"| {i++} | {MarkdownFormatter.EscapeTableCell(test.MethodName)} | {relPath} | {lineRange} |");
                }
                sb.AppendLine();
            }

            // Summary by framework
            var frameworks = tests.GroupBy(t => t.Framework).OrderBy(g => g.Key);
            sb.AppendLine("**Frameworks:** " + string.Join(", ", frameworks.Select(g => $"{g.Key} ({g.Count()})")));

            return sb.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static (bool IsTest, string Framework) DetectTestMethod(
        MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        foreach (var attrList in method.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();

                // Quick check by name
                if (TestAttributes.Contains(name))
                {
                    return (true, InferFramework(name));
                }

                // Full semantic check
                var symbol = semanticModel.GetSymbolInfo(attr).Symbol;
                if (symbol is IMethodSymbol ctorSymbol)
                {
                    var ns = ctorSymbol.ContainingType.ContainingNamespace?.ToDisplayString();
                    if (ns is not null && TestNamespaces.Contains(ns))
                    {
                        return (true, InferFrameworkFromNamespace(ns));
                    }
                }
            }
        }

        return (false, "");
    }

    private static string InferFramework(string attributeName) => attributeName switch
    {
        "Fact" or "FactAttribute" or "Theory" or "TheoryAttribute" => "xUnit",
        "Test" or "TestAttribute" or "TestCase" or "TestCaseAttribute" => "NUnit",
        "TestMethod" or "TestMethodAttribute" => "MSTest",
        _ => "Unknown"
    };

    private static string InferFrameworkFromNamespace(string ns) => ns switch
    {
        "Xunit" => "xUnit",
        "NUnit.Framework" => "NUnit",
        "Microsoft.VisualStudio.TestTools.UnitTesting" => "MSTest",
        _ => "Unknown"
    };

    private sealed record TestInfo(
        string FullyQualifiedName,
        string MethodName,
        string ClassName,
        string Framework,
        string FilePath,
        int Line,
        int EndLine);
}
