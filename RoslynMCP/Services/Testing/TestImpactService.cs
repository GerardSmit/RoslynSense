using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynMCP.Services.Testing;

/// <summary>Why a test was selected — the part a caller most wants to see before spending
/// minutes on a run.</summary>
public enum ImpactReason
{
    /// <summary>Coverage says this test executed a line the diff touched.</summary>
    CoveredChangedLines,

    /// <summary>Coverage says this test touches the changed file, but the file has moved since
    /// the map was built, so which lines no longer means anything.</summary>
    CoveredChangedFile,

    /// <summary>The test itself was edited.</summary>
    TestChanged,

    /// <summary>No coverage for this code — the test references a symbol the diff declared or
    /// changed, found by walking references.</summary>
    ReferencesChangedCode,
}

public sealed record ImpactedTest(
    string FullyQualifiedName,
    string ClassFullName,
    string ProjectPath,
    ImpactReason Reason,
    /// <summary>The changed file that pulled this test in.</summary>
    string? Because = null);

/// <summary>What a selection decided, and what it could not decide.</summary>
public sealed record TestImpactSelection(
    IReadOnlyList<ImpactedTest> Tests,
    IReadOnlyList<ChangedFile> ChangedFiles,
    /// <summary>Changed source files with neither coverage nor a reference path to any test.
    /// Those changes are going untested by this run, which is worth saying out loud.</summary>
    IReadOnlyList<string> UncoveredFiles,
    string Description,
    bool MapWasEmpty,
    string? Error = null)
{
    public static TestImpactSelection Failed(string error) =>
        new([], [], [], "", false, error);

    public IEnumerable<IGrouping<string, ImpactedTest>> ByProject() =>
        Tests.GroupBy(t => t.ProjectPath, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Picks the tests worth running for the changes in the working copy: coverage decides where
/// coverage exists, and a reference walk covers what coverage cannot know about yet — code
/// written since the map was built, which is most of what a diff contains.
/// </summary>
public static class TestImpactService
{
    /// <summary>How far the reference walk goes from a changed symbol before giving up. Three
    /// hops reaches a test through a helper and a facade; going further mostly finds the
    /// whole suite through a shared abstraction.</summary>
    private const int MaxReferenceDepth = 3;

    public static async Task<TestImpactSelection> SelectAsync(
        string anchorPath,
        GitChangeScope scope = GitChangeScope.Uncommitted,
        string? reference = null,
        bool useReferenceWalk = true,
        CancellationToken ct = default)
    {
        var changes = await GitChangeService.GetChangesAsync(anchorPath, scope, reference, ct);
        if (changes.Error is not null)
            return TestImpactSelection.Failed(changes.Error);

        var sourceChanges = changes.Files
            .Where(f => f.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sourceChanges.Count == 0)
        {
            return new TestImpactSelection(
                [], changes.Files, [], changes.Description, MapWasEmpty: false,
                Error: null);
        }

        var map = TestCoverageMapStore.LoadNearest(anchorPath);

        var selected = new Dictionary<string, ImpactedTest>(StringComparer.Ordinal);
        var uncovered = new List<string>();

        foreach (var file in sourceChanges)
        {
            ct.ThrowIfCancellationRequested();

            int before = selected.Count;

            // The file has moved since coverage ran, so its recorded line numbers describe text
            // that is no longer there. Fall back to the file as a whole rather than matching
            // ranges that have drifted — over-selecting a few tests beats missing the one that
            // would have caught the change.
            bool stale = map.IsFileStale(file.FilePath);
            var ranges = stale || file.WholeFile ? [] : file.Ranges;

            foreach (var entry in map.EntriesCovering(file.FilePath, ranges))
            {
                var reason =
                    string.Equals(entry.SourceFilePath, file.FilePath, StringComparison.OrdinalIgnoreCase)
                        ? ImpactReason.TestChanged
                        : stale || file.WholeFile
                            ? ImpactReason.CoveredChangedFile
                            : ImpactReason.CoveredChangedLines;

                foreach (string test in entry.Tests)
                    Add(selected, new ImpactedTest(test, entry.ClassFullName, entry.ProjectPath, reason, file.FilePath));
            }

            if (selected.Count == before)
                uncovered.Add(file.FilePath);
        }

        // What coverage had nothing to say about — new files, and everything if the map was
        // never built. This is where a diff's most interesting changes live.
        if (useReferenceWalk && uncovered.Count > 0)
        {
            var stillUncovered = new List<string>();

            foreach (string file in uncovered)
            {
                ct.ThrowIfCancellationRequested();

                int before = selected.Count;
                var changed = sourceChanges.First(f =>
                    string.Equals(f.FilePath, file, StringComparison.OrdinalIgnoreCase));

                try
                {
                    await AddTestsReachingAsync(changed, selected, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ServiceLog.Warn(
                        $"Could not walk references for '{Path.GetFileName(file)}': {ex.Message}",
                        key: "test-impact-walk");
                }

                if (selected.Count == before)
                    stillUncovered.Add(file);
            }

            uncovered = stillUncovered;
        }

        return new TestImpactSelection(
            selected.Values.OrderBy(t => t.FullyQualifiedName, StringComparer.Ordinal).ToList(),
            changes.Files,
            uncovered,
            changes.Description,
            map.IsEmpty);
    }

    /// <summary>
    /// Walks outward from the symbols the diff touched until it reaches test methods.
    /// </summary>
    /// <remarks>
    /// This is what answers for code that has never been covered — a new method has no coverage
    /// by definition, and it is exactly the code a run should exercise. It sees only what the
    /// compiler sees: a call reached through reflection or a DI container is invisible here,
    /// which is the half of the problem the coverage map exists to solve.
    /// </remarks>
    private static async Task AddTestsReachingAsync(
        ChangedFile file, Dictionary<string, ImpactedTest> selected, CancellationToken ct)
    {
        string? projectPath = await WorkspaceService.FindContainingProjectAsync(file.FilePath, ct);
        if (projectPath is null)
            return;

        var (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, diagnosticWriter: TextWriter.Null, cancellationToken: ct);

        var document = WorkspaceService.FindDocumentInProject(project, file.FilePath);
        if (document is null)
            return;

        var model = await document.GetSemanticModelAsync(ct);
        var root = await document.GetSyntaxRootAsync(ct);
        if (model is null || root is null)
            return;

        var frontier = new List<ISymbol>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in root.DescendantNodes())
        {
            if (node is not MemberDeclarationSyntax and not LocalFunctionStatementSyntax)
                continue;

            var span = node.GetLocation().GetLineSpan();
            int start = span.StartLinePosition.Line + 1;
            int end = span.EndLinePosition.Line + 1;

            // A declaration counts when the diff touched any line inside it — the change may be
            // in the body, the signature, or an attribute above it.
            if (!file.WholeFile && !file.Ranges.Any(r => r.Start <= end && r.End >= start))
                continue;

            if (model.GetDeclaredSymbol(node, ct) is { } symbol && visited.Add(Key(symbol)))
                frontier.Add(symbol);
        }

        var solution = workspace.CurrentSolution;

        for (int depth = 0; depth < MaxReferenceDepth && frontier.Count > 0; depth++)
        {
            var next = new List<ISymbol>();

            foreach (var symbol in frontier)
            {
                ct.ThrowIfCancellationRequested();

                foreach (var group in await SymbolFinder.FindReferencesAsync(symbol, solution, ct))
                {
                    foreach (var location in group.Locations)
                    {
                        var referencingDocument = location.Document;
                        var referencingModel = await referencingDocument.GetSemanticModelAsync(ct);
                        var referencingRoot = await referencingDocument.GetSyntaxRootAsync(ct);
                        if (referencingModel is null || referencingRoot is null)
                            continue;

                        var node = referencingRoot.FindNode(location.Location.SourceSpan);
                        var method = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                        if (method is null)
                            continue;

                        if (referencingModel.GetDeclaredSymbol(method, ct) is not { } methodSymbol)
                            continue;

                        if (!visited.Add(Key(methodSymbol)))
                            continue;

                        if (IsTestMethod(method, referencingModel))
                        {
                            string fqn = $"{methodSymbol.ContainingType?.ToDisplayString()}.{methodSymbol.Name}";
                            Add(selected, new ImpactedTest(
                                fqn,
                                methodSymbol.ContainingType?.ToDisplayString() ?? "",
                                referencingDocument.Project.FilePath ?? "",
                                ImpactReason.ReferencesChangedCode,
                                file.FilePath));
                            continue;
                        }

                        // Not a test, but something a test may reach: keep walking outward.
                        next.Add(methodSymbol);
                    }
                }
            }

            frontier = next;
        }
    }

    private static void Add(Dictionary<string, ImpactedTest> selected, ImpactedTest test)
    {
        // First reason wins: coverage-backed selections run before the reference walk, and
        // "covered these changed lines" is the more informative answer.
        if (!selected.ContainsKey(test.FullyQualifiedName))
            selected[test.FullyQualifiedName] = test;
    }

    private static string Key(ISymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static bool IsTestMethod(MethodDeclarationSyntax method, SemanticModel model)
    {
        foreach (var attributeList in method.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                if (model.GetSymbolInfo(attribute).Symbol is IMethodSymbol constructor)
                {
                    string? ns = constructor.ContainingType.ContainingNamespace?.ToDisplayString();
                    if (ns is "Xunit" or "NUnit.Framework"
                        or "Microsoft.VisualStudio.TestTools.UnitTesting")
                        return true;
                    continue;
                }

                string name = attribute.Name.ToString();
                if (name is "Fact" or "Theory" or "Test" or "TestCase" or "TestMethod" or "DataTestMethod"
                    or "FactAttribute" or "TheoryAttribute" or "TestAttribute" or "TestCaseAttribute"
                    or "TestMethodAttribute" or "DataTestMethodAttribute")
                    return true;
            }
        }
        return false;
    }
}
