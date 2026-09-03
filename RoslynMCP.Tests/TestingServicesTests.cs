using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.Testing;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>The services behind both the MCP test tools and the editor's Test Explorer:
/// Roslyn discovery, TRX parsing, and filter construction.</summary>
[Collection(SharedState.Name)]
public class TestingServicesTests
{
    [Fact]
    public async Task DiscoveryFindsTestsWithTheirLocationAndFramework()
    {
        var tests = await TestDiscoveryService.DiscoverAsync(FixturePaths.DebugTestProjectFile);

        Assert.NotEmpty(tests);
        var fact = tests.First(t => t.DisplayName == "Add_ReturnsSum");
        Assert.Equal("xUnit", fact.Framework);
        Assert.Equal("CalculatorTests", fact.ClassName);
        Assert.Equal("DebugTestProject.CalculatorTests.Add_ReturnsSum", fact.FullyQualifiedName);
        Assert.Equal("DebugTestProject", fact.Namespace);
        Assert.True(fact.StartLine > 0);
        Assert.True(fact.EndLine >= fact.StartLine);
        Assert.NotNull(fact.FilePath);
    }

    [Fact]
    public async Task DiscoveryIgnoresMethodsThatMerelyLookLikeTests()
    {
        var tests = await TestDiscoveryService.DiscoverAsync(FixturePaths.DebugTestProjectFile);

        // A method named Fact, and methods carrying same-named attributes from an unrelated
        // namespace, must not be discovered — name matching alone gets all of these wrong.
        Assert.DoesNotContain(tests, t => t.DisplayName == "Fact");
        Assert.DoesNotContain(tests, t => t.DisplayName == "NotATestDespiteTheAttributeName");
        Assert.DoesNotContain(tests, t => t.DisplayName == "AlsoNotATest");
    }

    [Fact]
    public async Task DiscoveryCanBeScopedToOneFile()
    {
        string calculatorTests = Path.Combine(FixturePaths.DebugTestProjectDir, "CalculatorTests.cs");

        var all = await TestDiscoveryService.DiscoverAsync(FixturePaths.DebugTestProjectFile);
        var scoped = await TestDiscoveryService.DiscoverAsync(
            FixturePaths.DebugTestProjectFile,
            classNameFilter: null,
            sourceFileFilter: calculatorTests);

        Assert.NotEmpty(scoped);
        Assert.True(scoped.Count <= all.Count);
        Assert.All(scoped, t => Assert.Equal(
            calculatorTests, t.FilePath, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiscoveryCanBeFilteredByClassName()
    {
        var tests = await TestDiscoveryService.DiscoverAsync(
            FixturePaths.DebugTestProjectFile, classNameFilter: "CalculatorTests");

        Assert.NotEmpty(tests);
        Assert.All(tests, t => Assert.Contains("CalculatorTests", t.ClassName, StringComparison.Ordinal));
    }

    [Fact]
    public void TrxParserReadsOutcomesDurationsAndFailureDetail()
    {
        string trx = WriteTrx("""
            <?xml version="1.0" encoding="UTF-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="N.C.Passes" outcome="Passed" duration="00:00:00.0120000" />
                <UnitTestResult testName="N.C.Fails" outcome="Failed" duration="00:00:00.5000000">
                  <Output>
                    <ErrorInfo>
                      <Message>Assert.Equal() Failure&#10;Expected: 3&#10;Actual: 4</Message>
                      <StackTrace>   at N.C.Fails()</StackTrace>
                    </ErrorInfo>
                  </Output>
                </UnitTestResult>
                <UnitTestResult testName="N.C.Skipped" outcome="NotExecuted" />
              </Results>
            </TestRun>
            """);
        try
        {
            var results = TrxParser.Parse(trx);

            Assert.Equal(3, results.Count);
            var passed = results.Single(r => r.FullyQualifiedName == "N.C.Passes");
            Assert.True(passed.Passed);
            Assert.Equal(12, passed.DurationMs);

            var failed = results.Single(r => r.FullyQualifiedName == "N.C.Fails");
            Assert.True(failed.Failed);
            Assert.Contains("Expected: 3", failed.ErrorMessage!);
            Assert.Contains("at N.C.Fails()", failed.StackTrace!);
            Assert.Equal(500, failed.DurationMs);

            Assert.False(results.Single(r => r.FullyQualifiedName == "N.C.Skipped").Passed);
        }
        finally
        {
            File.Delete(trx);
        }
    }

    [Fact]
    public void TrxParserStripsDataDrivenArgumentsFromTestNames()
    {
        // TRX reports theory cases as "Name(x: 1)"; results are matched to discovered tests by
        // fully-qualified name, so the argument suffix has to come off or nothing matches.
        Assert.Equal("N.C.Theory", TrxParser.NormalizeTestName("N.C.Theory(value: 1)"));
        Assert.Equal("N.C.Plain", TrxParser.NormalizeTestName("N.C.Plain"));
    }

    [Fact]
    public void TrxParserReturnsNothingForAMissingOrTruncatedFile()
    {
        Assert.Empty(TrxParser.Parse(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.trx")));

        string truncated = WriteTrx("<TestRun><Results><UnitTestResult");
        try { Assert.Empty(TrxParser.Parse(truncated)); }
        finally { File.Delete(truncated); }
    }

    [Fact]
    public void FilterBuilderProducesAnOrOfFullNamesAndNothingForAnEmptySet()
    {
        Assert.Null(TestRunService.BuildFilter([]));

        string filter = TestRunService.BuildFilter(["N.C.A", "N.C.B", "N.C.A"])!;

        Assert.Equal("FullyQualifiedName~N.C.A | FullyQualifiedName~N.C.B", filter);
    }

    [Fact]
    public async Task TestProjectsAndDiscoveryAreReachableOverTheLspSurface()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.DebugTestProjectFile);

        var projects = await TestHandler.ProjectsAsync(default);
        Assert.Contains(projects, p =>
            string.Equals(p.ProjectPath, FixturePaths.DebugTestProjectFile, StringComparison.OrdinalIgnoreCase));

        var tests = await TestHandler.DiscoverAsync(
            new TestDiscoverParams(FixturePaths.DebugTestProjectFile), default);
        Assert.Contains(tests, t => t.DisplayName == "Add_ReturnsSum");
    }

    private static string WriteTrx(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"roslyn-sense-test-{Guid.NewGuid():N}.trx");
        File.WriteAllText(path, content);
        return path;
    }
}
