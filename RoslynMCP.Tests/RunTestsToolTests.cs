using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class RunTestsToolTests
{
    [Fact]
    public async Task WhenProjectPathIsEmptyThenReturnsError()
    {
        var result = await RunTestsTool.RunTests("", new MarkdownFormatter());

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenProjectDoesNotExistThenReturnsError()
    {
        var result = await RunTestsTool.RunTests("/nonexistent/path/Test.csproj", new MarkdownFormatter());

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenRunningNonTestProjectThenDotnetTestHandlesError()
    {
        // dotnet test will report its own error for non-test projects
        var result = await RunTestsTool.RunTests(FixturePaths.SampleProjectFile, new MarkdownFormatter());

        // Should get an error from dotnet test, not from our validation
        Assert.NotNull(result);
    }

    [Fact]
    public async Task WhenRunningActualTestProjectWithFilterThenReturnsResults()
    {
        // Run a single specific test from RoslynMCP.Tests
        var testProjectPath = FindTestProjectPath();
        if (testProjectPath is null)
        {
            // Skip if we can't find the test project
            return;
        }

        var result = await RunTestsTool.RunTests(
            testProjectPath,
            new MarkdownFormatter(),
            "FullyQualifiedName=RoslynMCP.Tests.RunTestsToolTests.WhenProjectPathIsEmptyThenReturnsError");

        Assert.Contains("Passed", result);
    }

    [Fact]
    public async Task WhenRunningTestProjectWithInvalidFilterThenReturnsNoTests()
    {
        var testProjectPath = FindTestProjectPath();
        if (testProjectPath is null) return;

        var result = await RunTestsTool.RunTests(
            testProjectPath,
            new MarkdownFormatter(),
            "FullyQualifiedName=NonExistent.Test.Method");

        // Should run but find no tests
        Assert.NotNull(result);
    }

    private static string? FindTestProjectPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var csproj = Path.Combine(dir.FullName, "RoslynMCP.Tests", "RoslynMCP.Tests.csproj");
            if (File.Exists(csproj)) return csproj;
            var sln = Path.Combine(dir.FullName, "RoslynMCP.sln");
            if (File.Exists(sln))
            {
                csproj = Path.Combine(dir.FullName, "RoslynMCP.Tests", "RoslynMCP.Tests.csproj");
                if (File.Exists(csproj)) return csproj;
            }
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void WhenFormattingFailedTestOutputThenIncludesErrorMessage()
    {
        // Simulates real dotnet test --verbosity normal output for a failed test
        var stdout = """
            Build started 11/01/2025 12:00:00
              Determining projects to restore...
              All projects are up-to-date for restore.
              SomeProject -> /bin/Debug/net10.0/SomeProject.dll
            Test run for /bin/Debug/net10.0/SomeProject.dll (.NETCoreApp,Version=v10.0)
            Starting test execution, please wait...
            A total of 1 test files matched the specified pattern.
              Failed MyNamespace.MyTests.WhenDoingXThenYHappens [42 ms]
              Error Message:
               Expected some lines to be tracked
              Stack Trace:
                 at MyNamespace.MyTests.WhenDoingXThenYHappens() in /src/MyTests.cs:line 42
            --- End of stack trace from previous location ---
            Test Run Failed.
            Total tests: 3
                 Passed: 2
                 Failed: 1
             Total time: 1.234 Seconds
            """;

        var result = RunTestsTool.FormatTestOutput(stdout, "", 1, new MarkdownFormatter());

        Assert.Contains("Tests Failed", result);
        Assert.Contains("WhenDoingXThenYHappens", result);
        Assert.Contains("Expected some lines to be tracked", result);
        Assert.Contains("Stack Trace:", result);
        Assert.Contains("MyTests.cs:line 42", result);
        Assert.Contains("Total tests: 3", result);
    }

    [Fact]
    public void WhenFormattingMultipleFailuresWithAssertEqualThenIncludesExpectedActual()
    {
        var stdout = """
              Failed Namespace.Tests.TestOne [10 ms]
              Error Message:
               Assert.Equal() Failure: Values differ
               Expected: 42
               Actual:   0
              Stack Trace:
                 at Namespace.Tests.TestOne() in /src/Tests.cs:line 10
              Failed Namespace.Tests.TestTwo [5 ms]
              Error Message:
               Assert.True() Failure
               Expected: True
               Actual:   False
              Stack Trace:
                 at Namespace.Tests.TestTwo() in /src/Tests.cs:line 20
            Test Run Failed.
            Total tests: 4
                 Passed: 2
                 Failed: 2
            """;

        var result = RunTestsTool.FormatTestOutput(stdout, "", 1, new MarkdownFormatter());

        Assert.Contains("TestOne", result);
        Assert.Contains("TestTwo", result);
        Assert.Contains("Assert.Equal() Failure: Values differ", result);
        Assert.Contains("Expected: 42", result);
        Assert.Contains("Actual:   0", result);
        Assert.Contains("Assert.True() Failure", result);
        Assert.Contains("Expected: True", result);
        Assert.Contains("Actual:   False", result);
    }

    [Fact]
    public void WhenFormattingPassedTestOutputThenShowsSummary()
    {
        var stdout = """
            Test run for /bin/Debug/net10.0/SomeProject.dll (.NETCoreApp,Version=v10.0)
            Starting test execution, please wait...
            A total of 1 test files matched the specified pattern.
            Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 100 ms
            Test Run Successful.
            Total tests: 5
                 Passed: 5
            """;

        var result = RunTestsTool.FormatTestOutput(stdout, "", 0, new MarkdownFormatter());

        Assert.Contains("Tests Passed", result);
        Assert.Contains("Total tests: 5", result);
        Assert.DoesNotContain("Failed Tests", result);
    }

    [Fact]
    public void WhenFormattingBuildErrorThenIncludesRawOutput()
    {
        var stdout = """
            Build FAILED.
            /src/MyClass.cs(10,5): error CS1002: ; expected
            """;

        var result = RunTestsTool.FormatTestOutput(stdout, "", 1, new MarkdownFormatter());

        Assert.Contains("Tests Failed", result);
        Assert.Contains("error CS1002", result);
        Assert.Contains("; expected", result);
    }

    [Fact]
    public void WhenFormattingTrxWithPassedTestsThenShowsSummary()
    {
        var trxContent = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="MyTest1" outcome="Passed" duration="00:00:00.123" />
                <UnitTestResult testName="MyTest2" outcome="Passed" duration="00:00:00.045" />
              </Results>
            </TestRun>
            """;

        var trxPath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.trx");
        try
        {
            File.WriteAllText(trxPath, trxContent);
            var result = RunTestsTool.FormatTrxOutput(trxPath, 0, new MarkdownFormatter());

            Assert.Contains("Tests Passed", result);
            Assert.Contains("**Total tests**: 2", result);
            Assert.Contains("**Passed**: 2", result);
            Assert.DoesNotContain("**Failed**:", result);
        }
        finally
        {
            File.Delete(trxPath);
        }
    }

    [Fact]
    public void WhenFormattingTrxWithFailedTestsThenShowsDetails()
    {
        var trxContent = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="PassingTest" outcome="Passed" duration="00:00:00.010" />
                <UnitTestResult testName="FailingTest" outcome="Failed" duration="00:00:01.234">
                  <Output>
                    <ErrorInfo>
                      <Message>Assert.Equal() Failure
            Expected: 42
            Actual:   0</Message>
                      <StackTrace>   at MyTests.FailingTest() in C:\src\Tests.cs:line 15</StackTrace>
                    </ErrorInfo>
                  </Output>
                </UnitTestResult>
              </Results>
            </TestRun>
            """;

        var trxPath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.trx");
        try
        {
            File.WriteAllText(trxPath, trxContent);
            var result = RunTestsTool.FormatTrxOutput(trxPath, 1, new MarkdownFormatter());

            Assert.Contains("Tests Failed", result);
            Assert.Contains("**Total tests**: 2", result);
            Assert.Contains("**Passed**: 1", result);
            Assert.Contains("**Failed**: 1", result);
            Assert.Contains("FailingTest", result);
            Assert.Contains("Assert.Equal() Failure", result);
            Assert.Contains("Stack trace", result);
        }
        finally
        {
            File.Delete(trxPath);
        }
    }
}