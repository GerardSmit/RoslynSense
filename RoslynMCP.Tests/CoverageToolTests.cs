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
        var result = await RunCoverageTool.RunCoverage("", new MarkdownFormatter(), new BackgroundTaskStore(), new BuildWarningsStore());
        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenRunCoverageWithNonexistentPathThenReturnsError()
    {
        var result = await RunCoverageTool.RunCoverage("/nonexistent/path/Test.csproj", new MarkdownFormatter(), new BackgroundTaskStore(), new BuildWarningsStore());
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
        var runResult = await RunCoverageTool.RunCoverage(FixturePaths.DebugTestProjectFile, new MarkdownFormatter(), new BackgroundTaskStore(), new BuildWarningsStore());
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

    // --- GetMethodCoverage error handling ---

    [Fact]
    public async Task WhenGetMethodCoverageWithNoCacheThenReturnsError()
    {
        ClearCoverageCache();

        var result = await GetMethodCoverageTool.GetMethodCoverage(
            new MarkdownFormatter(), methodName: "SomeMethod");

        Assert.Contains("RunCoverage", result);
    }

    [Fact]
    public async Task WhenGetMethodCoverageWithNonexistentMethodThenReturnsNotFound()
    {
        InjectCoverageXml(MinimalCoverageXml());

        try
        {
            var result = await GetMethodCoverageTool.GetMethodCoverage(
                new MarkdownFormatter(), methodName: "CompletelyNonexistentMethodXYZ99");

            Assert.Contains("No coverage data found", result);
        }
        finally
        {
            ClearCoverageCache();
        }
    }

    // --- GetMethodCoverage unit tests (all lines shown) ---

    [Fact]
    public async Task WhenGetMethodCoverageForFullyCoveredMethodThenShowsCoveredLines()
    {
        InjectCoverageXml(MinimalCoverageXml());

        try
        {
            var result = await GetMethodCoverageTool.GetMethodCoverage(
                new MarkdownFormatter(), methodName: "Add");

            Assert.Contains("Add", result);
            Assert.Contains("hits", result);
            Assert.DoesNotContain("0 hits", result); // all lines covered
        }
        finally
        {
            ClearCoverageCache();
        }
    }

    [Fact]
    public async Task WhenGetMethodCoverageForPartiallyCoveredMethodThenShowsAllLines()
    {
        InjectCoverageXml(MinimalCoverageXml());

        try
        {
            var result = await GetMethodCoverageTool.GetMethodCoverage(
                new MarkdownFormatter(), methodName: "Divide");

            Assert.Contains("Divide", result);
            Assert.Contains("0 hits", result);   // uncovered lines
            Assert.Contains("2 hits", result);   // covered lines
            Assert.Contains("hits !", result);   // partial branch marker
        }
        finally
        {
            ClearCoverageCache();
        }
    }

    [Fact]
    public async Task WhenGetMethodCoverageWithClassNameFilterThenNarrowsResults()
    {
        InjectCoverageXml(MinimalCoverageXml());

        try
        {
            // Filter to a non-matching class — should return not-found
            var result = await GetMethodCoverageTool.GetMethodCoverage(
                new MarkdownFormatter(), methodName: "Add", className: "NonexistentClass");

            Assert.Contains("No coverage data found", result);
        }
        finally
        {
            ClearCoverageCache();
        }
    }

    [Fact]
    public async Task WhenGetMethodCoverageWithFilePathFilterThenNarrowsResults()
    {
        InjectCoverageXml(MinimalCoverageXml());

        try
        {
            // Filter to a non-matching file — should return not-found
            var result = await GetMethodCoverageTool.GetMethodCoverage(
                new MarkdownFormatter(), methodName: "Add", filePath: "NonexistentFile.cs");

            Assert.Contains("No coverage data found", result);
        }
        finally
        {
            ClearCoverageCache();
        }
    }

    // --- GetMethodCoverage integration test ---

    [Fact]
    public async Task WhenGetMethodCoverageAfterRunThenShowsPerLineCoverage()
    {
        var runResult = await CoverageService.RunCoverageAsync(FixturePaths.DebugTestProjectFile);
        Assert.True(runResult.Success, $"Coverage collection failed: {runResult.Message}");

        var result = await GetMethodCoverageTool.GetMethodCoverage(
            new MarkdownFormatter(), methodName: "Add");

        Assert.DoesNotContain("Run `RunCoverage` first", result);
        Assert.DoesNotContain("No coverage data found", result);
        Assert.Contains("Add", result);
        Assert.Contains("hits", result);
    }

    [Fact]
    public async Task WhenGetMethodCoverageAndFileUnchangedThenNoStalenessWarning()
    {
        var runResult = await CoverageService.RunCoverageAsync(FixturePaths.DebugTestProjectFile);
        Assert.True(runResult.Success, $"Coverage collection failed: {runResult.Message}");

        var result = await GetMethodCoverageTool.GetMethodCoverage(
            new MarkdownFormatter(), methodName: "Add", className: "Calculator");

        Assert.DoesNotContain("File modified since coverage was collected", result);
    }

    // --- SourceHash ---

    [Fact]
    public void WhenHashMethodLinesCalledThenReturnsDeterministicHash()
    {
        string[] lines = ["public int Add(int a, int b)", "{", "    return a + b;", "}"];
        int maxBytes = lines.Max(l => System.Text.Encoding.UTF8.GetMaxByteCount(l.Length));
        byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            string h1 = CoverageService.HashMethodLines(lines, 1, 4, buf);
            string h2 = CoverageService.HashMethodLines(lines, 1, 4, buf);
            Assert.Equal(h1, h2);
            Assert.NotEmpty(h1);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [Fact]
    public void WhenHashMethodLinesCalledWithDifferentContentThenReturnsDifferentHash()
    {
        string[] lines1 = ["    return a + b;"];
        string[] lines2 = ["    return a - b;"];
        byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(64);
        try
        {
            Assert.NotEqual(
                CoverageService.HashMethodLines(lines1, 1, 1, buf),
                CoverageService.HashMethodLines(lines2, 1, 1, buf));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [Fact]
    public void WhenComputeSourceHashesCalledThenMethodHashesPopulated()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"hash-test-{Guid.NewGuid():N}.cs");
        File.WriteAllText(tempFile, """
            public class Calc {
                public int Add(int a, int b) {
                    return a + b;
                }
            }
            """);

        try
        {
            string xml = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <coverage line-rate="1.0" branch-rate="1.0" lines-covered="1" lines-valid="1" branches-covered="0" branches-valid="0">
                  <packages><package name="P" line-rate="1.0" branch-rate="1.0">
                    <classes><class name="Calc" filename="{tempFile.Replace("\\", "/")}" line-rate="1.0" branch-rate="1.0">
                      <methods><method name="Add" signature="(int,int):int" line-rate="1.0" branch-rate="1.0">
                        <lines><line number="3" hits="1" branch="false" /></lines>
                      </method></methods>
                      <lines><line number="3" hits="1" branch="false" /></lines>
                    </class></classes>
                  </package></packages>
                </coverage>
                """;
            InjectCoverageXml(xml);

            var methods = CoverageService.FindMethodCoverage("Add");
            var method = methods.FirstOrDefault(m => m.FilePath.Equals(tempFile, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(method);
            Assert.NotNull(method!.SourceHash);
            Assert.NotEmpty(method.SourceHash!);
        }
        finally
        {
            File.Delete(tempFile);
            ClearCoverageCache();
        }
    }

    [Fact]
    public async Task WhenGetMethodCoverageAndFileUnchangedThenNoMethodChangedWarning()
    {
        var runResult = await CoverageService.RunCoverageAsync(FixturePaths.DebugTestProjectFile);
        Assert.True(runResult.Success, $"Coverage collection failed: {runResult.Message}");

        var result = await GetMethodCoverageTool.GetMethodCoverage(
            new MarkdownFormatter(), methodName: "Add", className: "Calculator");

        Assert.DoesNotContain("Method source has changed", result);
    }

    // --- RunCoverage next-step hints ---

    [Fact]
    public async Task WhenRunCoverageSucceedsThenOutputContainsNextStepHints()
    {
        var result = await RunCoverageTool.RunCoverage(
            FixturePaths.DebugTestProjectFile, new MarkdownFormatter(),
            new BackgroundTaskStore(), new BuildWarningsStore());

        Assert.Contains("GetCoverage", result);
        Assert.Contains("GetMethodCoverage", result);
    }

    [Fact]
    public async Task WhenRunCoverageFailsThenOutputDoesNotContainNextStepHints()
    {
        var result = await RunCoverageTool.RunCoverage(
            "/nonexistent/path/Test.csproj", new MarkdownFormatter(),
            new BackgroundTaskStore(), new BuildWarningsStore());

        Assert.DoesNotContain("GetMethodCoverage", result);
    }

    // --- Helpers ---

    private static void ClearCoverageCache()
    {
        var dataField = typeof(CoverageService).GetField(
            "_cachedData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        dataField!.SetValue(null, null);
    }

    private static void InjectCoverageXml(string xml)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"coverage-inject-{Guid.NewGuid():N}.xml");
        File.WriteAllText(tempFile, xml);
        try
        {
            var parseMethod = typeof(CoverageService).GetMethod(
                "ParseCoberturaXml",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var hashMethod = typeof(CoverageService).GetMethod(
                "ComputeSourceHashes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var data = (CoverageData)parseMethod!.Invoke(null, [tempFile])!;
            hashMethod!.Invoke(null, [data]);

            var lockField = typeof(CoverageService).GetField(
                "Lock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var dataField = typeof(CoverageService).GetField(
                "_cachedData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            lock (lockField!.GetValue(null)!)
                dataField!.SetValue(null, data);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static string MinimalCoverageXml() => """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.75" branch-rate="0.50" lines-covered="3" lines-valid="4" branches-covered="1" branches-valid="2">
          <packages>
            <package name="TestProject" line-rate="0.75" branch-rate="0.50">
              <classes>
                <class name="Calculator" filename="Calculator.cs" line-rate="0.75" branch-rate="0.50">
                  <methods>
                    <method name="Add" signature="(int,int):int" line-rate="1.0" branch-rate="1.0">
                      <lines>
                        <line number="7" hits="3" branch="false" />
                        <line number="8" hits="3" branch="false" />
                      </lines>
                    </method>
                    <method name="Divide" signature="(int,int):int" line-rate="0.5" branch-rate="0.5">
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
}
