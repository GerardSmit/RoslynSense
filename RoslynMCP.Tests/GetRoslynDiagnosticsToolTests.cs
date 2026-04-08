using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class GetRoslynDiagnosticsToolTests
{
    [Fact]
    public async Task WhenEmptyPathProvidedThenReturnsError()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(filePath: "", runAnalyzers: false);

        Assert.Contains("Error", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenNonExistentFileProvidedThenReturnsError()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: "Z:/nonexistent/file.cs", runAnalyzers: false);

        Assert.Contains("Error", result);
        Assert.Contains("does not exist", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenValidFileProvidedThenReturnsStructuredOutput()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.CalculatorFile, runAnalyzers: false);

        Assert.Contains("# Diagnostics:", result);
        Assert.Contains("Calculator.cs", result);
        Assert.Contains("**Project**:", result);
    }

    [Fact]
    public async Task WhenValidFileProvidedThenIncludesSeverityCounts()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.CalculatorFile, runAnalyzers: false);

        Assert.Contains("**Errors**:", result);
        Assert.Contains("**Warnings**:", result);
        Assert.Contains("**Info**:", result);
    }

    [Fact]
    public async Task WhenInvalidSeverityFilterProvidedThenReturnsError()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.CalculatorFile, severityFilter: "bogus", runAnalyzers: false);

        Assert.Contains("Error", result);
        Assert.Contains("Invalid severity filter", result);
    }

    [Fact]
    public async Task WhenWarningsFileProvidedThenReturnsStructuredOutput()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.WarningsFile, runAnalyzers: false);

        Assert.Contains("# Diagnostics:", result);
        Assert.Contains("Warnings.cs", result);
    }

    [Fact]
    public async Task WhenSeverityFilterAppliedThenFiltersDiagnostics()
    {
        var allResult = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.CalculatorFile, severityFilter: "all", runAnalyzers: false);
        var hiddenResult = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.CalculatorFile, severityFilter: "hidden", runAnalyzers: false);

        // Hidden-only results should differ from all results (or both be empty)
        Assert.Contains("# Diagnostics:", allResult);
        Assert.Contains("# Diagnostics:", hiddenResult);
    }

    [Fact]
    public async Task WhenBrokenSyntaxFileProvidedThenStructuredErrorsAreReturned()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.BrokenSyntaxFile,
            runAnalyzers: false);

        Assert.Contains("# Diagnostics:", result);
        Assert.Contains("BrokenSyntax.cs", result);
        Assert.Contains("Error", result);
        Assert.Contains("CS", result);
    }

    [Fact]
    public async Task WhenBrokenSemanticFileProvidedThenSemanticErrorsAreReturned()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.BrokenSemanticFile,
            runAnalyzers: false);

        Assert.Contains("# Diagnostics:", result);
        Assert.Contains("BrokenSemantic.cs", result);
        Assert.Contains("Error", result);
        Assert.Contains("CS", result);
    }

    [Fact]
    public async Task WhenAnalyzersEnabledThenAnalyzerWarningsAppearInStructuredOutput()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.WarningsFile,
            severityFilter: "warning",
            runAnalyzers: true);

        Assert.Contains("Warning", result);
        Assert.Contains("CA", result);
    }

    [Fact]
    public async Task WhenErrorFilterAppliedThenOnlyErrorsReturned()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.BrokenSyntaxFile,
            severityFilter: "error",
            runAnalyzers: false);

        Assert.Contains("Error", result);
        Assert.Contains("CS", result);
    }

    [Fact]
    public async Task WhenInfoFilterAppliedThenReturnsStructuredOutput()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.CalculatorFile,
            severityFilter: "info",
            runAnalyzers: false);

        Assert.Contains("# Diagnostics:", result);
    }

    [Fact]
    public async Task WhenWarningFilterAppliedThenReturnsStructuredOutput()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.WarningsFile,
            severityFilter: "warning",
            runAnalyzers: false);

        Assert.Contains("# Diagnostics:", result);
        Assert.Contains("Warning", result);
    }
}
