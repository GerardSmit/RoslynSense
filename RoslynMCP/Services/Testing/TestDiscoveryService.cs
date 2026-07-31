using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMCP.Services.Testing;

/// <summary>One discovered test method.</summary>
public sealed record DiscoveredTest(
    string Id,
    string FullyQualifiedName,
    string DisplayName,
    string ClassName,
    string? Namespace,
    string Framework,
    string? FilePath,
    int StartLine,
    int EndLine,
    string ProjectPath);

/// <summary>
/// Roslyn-based test discovery, shared by the MCP tool and the editor's Test Explorer.
/// Runs against the already-loaded compilation, so it costs a syntax walk rather than a
/// `dotnet test --list-tests` process, and it sees unsaved editor buffers through the
/// same overlay every other feature uses.
/// </summary>
public static class TestDiscoveryService
{
    private static readonly HashSet<string> s_testAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "FactAttribute",
        "Theory", "TheoryAttribute",
        "Test", "TestAttribute",
        "TestMethod", "TestMethodAttribute",
        "TestCase", "TestCaseAttribute",
        "TestCaseSource", "TestCaseSourceAttribute",
        "DataTestMethod", "DataTestMethodAttribute",
    };

    private static readonly HashSet<string> s_testNamespaces = new(StringComparer.Ordinal)
    {
        "Xunit",
        "NUnit.Framework",
        "Microsoft.VisualStudio.TestTools.UnitTesting",
    };

    public static async Task<IReadOnlyList<DiscoveredTest>> DiscoverAsync(
        string csprojPath,
        string? classNameFilter = null,
        string? sourceFileFilter = null,
        CancellationToken cancellationToken = default)
    {
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            csprojPath, diagnosticWriter: TextWriter.Null, cancellationToken: cancellationToken);

        var tests = new List<DiscoveredTest>();

        foreach (var document in project.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (sourceFileFilter is not null && document.FilePath is not null &&
                !string.Equals(document.FilePath, sourceFileFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (syntaxTree is null || semanticModel is null)
                continue;

            var root = await syntaxTree.GetRootAsync(cancellationToken);

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var (isTest, framework) = DetectTestMethod(method, semanticModel);
                if (!isTest)
                    continue;

                if (semanticModel.GetDeclaredSymbol(method, cancellationToken) is not { } methodSymbol)
                    continue;

                string containingClass = methodSymbol.ContainingType?.Name ?? "";
                if (classNameFilter is not null &&
                    !containingClass.Contains(classNameFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string fqn = $"{methodSymbol.ContainingType?.ToDisplayString()}.{methodSymbol.Name}";
                var identifierSpan = method.Identifier.GetLocation().GetLineSpan();
                var methodSpan = method.GetLocation().GetLineSpan();
                string? ns = methodSymbol.ContainingType?.ContainingNamespace is { IsGlobalNamespace: false } n
                    ? n.ToDisplayString()
                    : null;

                tests.Add(new DiscoveredTest(
                    Id: fqn,
                    FullyQualifiedName: fqn,
                    DisplayName: methodSymbol.Name,
                    ClassName: containingClass,
                    Namespace: ns,
                    Framework: framework,
                    FilePath: document.FilePath,
                    StartLine: identifierSpan.StartLinePosition.Line + 1,
                    EndLine: methodSpan.EndLinePosition.Line + 1,
                    ProjectPath: project.FilePath ?? csprojPath));
            }
        }

        return tests;
    }

    /// <summary>Test projects in the loaded solution, identified by actually containing tests
    /// rather than by naming convention.</summary>
    public static async Task<IReadOnlyList<(string ProjectPath, string ProjectName)>> FindTestProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        var solution = WorkspaceService.TryGetMostRecentSolution();
        if (solution is null)
            return [];

        var projects = new List<(string, string)>();
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (project.FilePath is not { Length: > 0 } path)
                continue;
            if (!ProjectClassifier.Classify(path).IsTestProject)
                continue;

            projects.Add((path, project.Name));
        }

        return projects
            .DistinctBy(p => p.Item1, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Item2, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (bool IsTest, string Framework) DetectTestMethod(
        MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        foreach (var attributeList in method.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                string name = attribute.Name.ToString();

                // The semantic check is authoritative — a method named after a test attribute,
                // or a same-named attribute from an unrelated library, must not count.
                if (semanticModel.GetSymbolInfo(attribute).Symbol is IMethodSymbol constructor)
                {
                    string? ns = constructor.ContainingType.ContainingNamespace?.ToDisplayString();
                    if (ns is not null && s_testNamespaces.Contains(ns))
                        return (true, FrameworkFromNamespace(ns));
                    continue;
                }

                // Unresolved symbol (missing reference, broken build): fall back to the name so
                // discovery still works in a half-loaded project.
                if (s_testAttributes.Contains(name))
                    return (true, FrameworkFromAttribute(name));
            }
        }

        return (false, "");
    }

    private static string FrameworkFromAttribute(string attributeName) => attributeName switch
    {
        "Fact" or "FactAttribute" or "Theory" or "TheoryAttribute" => "xUnit",
        "Test" or "TestAttribute" or "TestCase" or "TestCaseAttribute"
            or "TestCaseSource" or "TestCaseSourceAttribute" => "NUnit",
        "TestMethod" or "TestMethodAttribute" or "DataTestMethod" or "DataTestMethodAttribute" => "MSTest",
        _ => "Unknown",
    };

    private static string FrameworkFromNamespace(string ns) => ns switch
    {
        "Xunit" => "xUnit",
        "NUnit.Framework" => "NUnit",
        "Microsoft.VisualStudio.TestTools.UnitTesting" => "MSTest",
        _ => "Unknown",
    };
}
