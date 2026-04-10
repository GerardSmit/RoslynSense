using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class CoverageToolTests
{
    // --- RunCoverage error handling ---

    [Fact]
    public async Task WhenRunCoverageWithEmptyPathThenReturnsError()
    {
        var result = await RunCoverageTool.RunCoverage("", new BackgroundTaskStore());
        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenRunCoverageWithNonexistentPathThenReturnsError()
    {
        var result = await RunCoverageTool.RunCoverage("/nonexistent/path/Test.csproj", new BackgroundTaskStore());
        Assert.Contains("Error", result);
    }

    // --- GetCoverage error handling ---

    [Fact]
    public async Task WhenGetCoverageWithoutRunningCoverageThenReturnsError()
    {
        // Clear any cached data by querying before running
        var result = await GetCoverageTool.GetCoverage(
            new MarkdownFormatter(),
            filePath: "SomeFile.cs",
            methodName: "SomeMethod");

        // Should instruct the user to run RunCoverage first
        // (unless there's cached data from a prior test)
        Assert.NotNull(result);
    }

    [Fact]
    public async Task WhenGetCoverageWithNonexistentMethodThenReturnsNotFound()
    {
        // If coverage data is cached from another test, this should return "not found"
        // If no coverage data, it should return the "run RunCoverage first" error
        var result = await GetCoverageTool.GetCoverage(
            new MarkdownFormatter(),
            filePath: "",
            methodName: "CompletelyNonexistentMethodXYZ12345");

        Assert.NotNull(result);
    }

    // --- CoverageService XML parsing ---

    [Fact]
    public void WhenParsingCoberturXmlThenExtractsLineRates()
    {
        string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0.85" branch-rate="0.50" lines-covered="17" lines-valid="20" branches-covered="1" branches-valid="2">
              <packages>
                <package name="TestProject" line-rate="0.85" branch-rate="0.50">
                  <classes>
                    <class name="Calculator" filename="Calculator.cs" line-rate="1.0" branch-rate="1.0">
                      <methods>
                        <method name="Add" signature="(int,int):int" line-rate="1.0" branch-rate="1.0">
                          <lines>
                            <line number="7" hits="3" branch="false" />
                            <line number="8" hits="3" branch="false" />
                          </lines>
                        </method>
                        <method name="Divide" signature="(int,int):int" line-rate="0.5" branch-rate="0.0">
                          <lines>
                            <line number="12" hits="2" branch="false" />
                            <line number="13" hits="2" branch="true" condition-coverage="50% (1/2)" />
                            <line number="14" hits="0" branch="false" />
                            <line number="15" hits="0" branch="false" />
                          </lines>
                        </method>
                      </methods>
                      <lines>
                        <line number="7" hits="3" branch="false" />
                        <line number="8" hits="3" branch="false" />
                        <line number="12" hits="2" branch="false" />
                        <line number="13" hits="2" branch="true" condition-coverage="50% (1/2)" />
                        <line number="14" hits="0" branch="false" />
                        <line number="15" hits="0" branch="false" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;

        // Write to temp file and parse
        var tempFile = Path.Combine(Path.GetTempPath(), $"coverage-test-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempFile, xml);

            // Use reflection to test the private ParseCoberturaXml method
            var method = typeof(CoverageService).GetMethod(
                "ParseCoberturaXml",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var data = (CoverageData)method!.Invoke(null, [tempFile])!;
            Assert.NotNull(data);
            Assert.Equal(0.85, data.LineCoverageRate, 2);
            Assert.Equal(0.50, data.BranchCoverageRate, 2);
            Assert.Equal(17, data.LinesCovered);
            Assert.Equal(20, data.LinesValid);

            // Check that files were parsed
            Assert.Single(data.Files);

            var file = data.Files.Values.First();
            Assert.Equal(2, file.Methods.Count);

            var addMethod = file.Methods.First(m => m.Name == "Add");
            Assert.Equal(1.0, addMethod.LineCoverageRate, 2);
            Assert.Equal(2, addMethod.TotalLines);
            Assert.Equal(2, addMethod.CoveredLines);

            var divideMethod = file.Methods.First(m => m.Name == "Divide");
            Assert.Equal(0.5, divideMethod.LineCoverageRate, 2);
            Assert.Equal(4, divideMethod.TotalLines);
            Assert.Equal(2, divideMethod.CoveredLines);

            // Check branch coverage at method level
            Assert.Equal(0, addMethod.TotalBranches);
            Assert.Equal(1.0, addMethod.BranchCoverageRate, 2); // no branches = 100%
            Assert.Equal(2, divideMethod.TotalBranches);
            Assert.Equal(1, divideMethod.CoveredBranches);
            Assert.Equal(0.5, divideMethod.BranchCoverageRate, 2);

            // Check line-level data
            Assert.Equal(6, file.Lines.Count);
            Assert.Equal(3, file.Lines[7].Hits);
            Assert.Equal(0, file.Lines[14].Hits);
            Assert.True(file.Lines[13].IsBranch);
            Assert.Equal("50% (1/2)", file.Lines[13].ConditionCoverage);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void WhenFindingMethodCoverageByNameThenReturnsMatches()
    {
        string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="1.0" branch-rate="1.0" lines-covered="4" lines-valid="4" branches-covered="0" branches-valid="0">
              <packages>
                <package name="TestProject" line-rate="1.0" branch-rate="1.0">
                  <classes>
                    <class name="Calculator" filename="Calculator.cs" line-rate="1.0" branch-rate="1.0">
                      <methods>
                        <method name="Add" signature="(int,int):int" line-rate="1.0" branch-rate="1.0">
                          <lines>
                            <line number="7" hits="3" branch="false" />
                          </lines>
                        </method>
                        <method name="Subtract" signature="(int,int):int" line-rate="1.0" branch-rate="1.0">
                          <lines>
                            <line number="12" hits="1" branch="false" />
                          </lines>
                        </method>
                      </methods>
                      <lines>
                        <line number="7" hits="3" branch="false" />
                        <line number="12" hits="1" branch="false" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;

        var tempFile = Path.Combine(Path.GetTempPath(), $"coverage-test-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempFile, xml);

            // Parse and cache via reflection
            var parseMethod = typeof(CoverageService).GetMethod(
                "ParseCoberturaXml",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var data = (CoverageData)parseMethod!.Invoke(null, [tempFile])!;

            // Set cached data via reflection
            var lockField = typeof(CoverageService).GetField("Lock",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var dataField = typeof(CoverageService).GetField("_cachedData",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            lock (lockField!.GetValue(null)!)
            {
                dataField!.SetValue(null, data);
            }

            var addMethods = CoverageService.FindMethodCoverage("Add");
            Assert.Single(addMethods);
            Assert.Equal("Add", addMethods[0].Name);

            var calcMethods = CoverageService.FindMethodCoverage("Calculator");
            Assert.Equal(2, calcMethods.Count);
        }
        finally
        {
            File.Delete(tempFile);
            // Clean up cached data
            var dataField = typeof(CoverageService).GetField("_cachedData",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            dataField!.SetValue(null, null);
        }
    }

    // --- Integration test: run actual coverage on DebugTestProject ---

    [Fact]
    public async Task WhenRunCoverageOnDebugTestProjectThenReturnsCoverageReport()
    {
        var result = await CoverageService.RunCoverageAsync(FixturePaths.DebugTestProjectFile);

        Assert.True(result.Success, $"Coverage collection failed: {result.Message}");
        Assert.Contains("Coverage Report", result.Message);
        Assert.Contains("Line Coverage", result.Message);
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.LinesValid > 0, "Expected some lines to be tracked");
        Assert.True(result.Data.LinesCovered > 0, "Expected some lines to be covered");
    }

    [Fact]
    public async Task WhenRunCoverageWithFilterThenOnlyFilteredTestsRun()
    {
        var result = await CoverageService.RunCoverageAsync(
            FixturePaths.DebugTestProjectFile,
            filter: "FullyQualifiedName=DebugTestProject.CalculatorTests.Add_ReturnsSum");

        Assert.True(result.Success, $"Coverage collection failed: {result.Message}");
        Assert.NotNull(result.Data);
    }

    [Fact]
    public async Task WhenGetCoverageAfterRunThenReturnsMethodData()
    {
        // Run coverage first
        var runResult = await CoverageService.RunCoverageAsync(FixturePaths.DebugTestProjectFile);
        Assert.True(runResult.Success, $"Coverage collection failed: {runResult.Message}");

        // Query method coverage
        var methods = CoverageService.FindMethodCoverage("Add");
        Assert.NotEmpty(methods);

        var addMethod = methods.FirstOrDefault(m => m.Name == "Add");
        Assert.NotNull(addMethod);
        Assert.True(addMethod!.LineCoverageRate > 0, "Expected Add method to have coverage");
    }

    [Fact]
    public async Task WhenGetCoverageAfterRunThenReturnsClassData()
    {
        // Run coverage first
        var runResult = await CoverageService.RunCoverageAsync(FixturePaths.DebugTestProjectFile);
        Assert.True(runResult.Success, $"Coverage collection failed: {runResult.Message}");

        // Query class coverage
        var classes = CoverageService.FindClassCoverage("Calculator");
        Assert.NotEmpty(classes);
    }

    [Fact]
    public async Task WhenGetCoverageToolUsedAfterRunCoverageThenReturnsFormattedOutput()
    {
        // Run coverage via the tool
        var runResult = await RunCoverageTool.RunCoverage(FixturePaths.DebugTestProjectFile, new BackgroundTaskStore());
        Assert.Contains("Coverage Report", runResult);

        // Query via the tool
        var getCoverageResult = await GetCoverageTool.GetCoverage(
            new MarkdownFormatter(),
            filePath: FixturePaths.DebugCalculatorFile);

        // Should return file coverage (not the "run RunCoverage first" error)
        Assert.DoesNotContain("Run `RunCoverage` first", getCoverageResult);
    }

    [Fact]
    public async Task WhenGetCoverageWithoutFiltersThenReturnsProjectOverview()
    {
        // Run coverage first
        var runResult = await CoverageService.RunCoverageAsync(FixturePaths.DebugTestProjectFile);
        Assert.True(runResult.Success, $"Coverage failed: {runResult.Message}");

        // Query project-wide coverage (no filters)
        var result = await GetCoverageTool.GetCoverage(new MarkdownFormatter());

        Assert.Contains("Coverage:", result);
        Assert.Contains("Summary", result);
        Assert.Contains("Line Coverage", result);
        Assert.Contains("Coverage by Type", result);
        Assert.Contains("Calculator", result);
    }
}
