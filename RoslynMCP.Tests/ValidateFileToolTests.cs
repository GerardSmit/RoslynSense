using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Tests for the unified GetRoslynDiagnostics tool covering ASPX, Razor,
/// and C# validation scenarios (formerly ValidateFileToolTests).
/// </summary>
public class ValidateFileToolTests
{
    [Fact]
    public async Task WhenEmptyPathProvidedThenReturnsError()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(filePath: "", runAnalyzers: false);

        Assert.Contains("Error", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenWhitespacePathProvidedThenReturnsError()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(filePath: "   ", runAnalyzers: false);

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenNonExistentFileProvidedThenReturnsFileNotFoundError()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: Path.Combine(FixturePaths.SampleProjectDir, "DoesNotExist.cs"),
            runAnalyzers: false);

        Assert.Contains("does not exist", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenValidFileProvidedThenReportsDiagnosticsHeader()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.CalculatorFile,
            runAnalyzers: false);

        Assert.Contains("# Diagnostics:", result);
        Assert.Contains("Calculator.cs", result);
    }

    [Fact]
    public async Task WhenValidFileProvidedThenReportsZeroErrors()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.CalculatorFile,
            runAnalyzers: false);

        Assert.Contains("**Errors**: 0", result);
    }

    [Fact]
    public async Task WhenBrokenSyntaxFileProvidedThenReportsErrors()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.BrokenSyntaxFile,
            runAnalyzers: false);

        Assert.Contains("Error", result);
        Assert.Contains("CS", result);
    }

    [Fact]
    public async Task WhenBrokenSemanticFileProvidedThenReportsErrors()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.BrokenSemanticFile,
            runAnalyzers: false);

        Assert.Contains("Error", result);
        Assert.Contains("CS", result);
    }

    [Fact]
    public async Task WhenAnalyzersEnabledThenIncludesAnalyzerDiagnostics()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.WarningsFile,
            runAnalyzers: true);

        Assert.Contains("Warning", result);
        Assert.Contains("CA", result);
    }

    [Fact]
    public async Task WhenAspxFileProvidedThenReturnsOutlineWithDirectives()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.DefaultAspxFile,
            runAnalyzers: false,
            handlers: TestHandlers.Diagnostics);

        Assert.Contains("Directives", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.StartsWith("Error:"), "Unexpected tool-level error: " + result[..Math.Min(200, result.Length)]);
    }

    [Fact]
    public async Task WhenAscxFileProvidedThenReturnsOutline()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.HeaderControlFile,
            runAnalyzers: false,
            handlers: TestHandlers.Diagnostics);

        Assert.Contains("Directives", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.StartsWith("Error:"), "Unexpected tool-level error: " + result[..Math.Min(200, result.Length)]);
    }

    [Fact]
    public async Task WhenMasterPageProvidedThenReturnsOutline()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.SiteMasterFile,
            runAnalyzers: false,
            handlers: TestHandlers.Diagnostics);

        Assert.Contains("Directives", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.StartsWith("Error:"), "Unexpected tool-level error: " + result[..Math.Min(200, result.Length)]);
    }

    [Fact]
    public async Task WhenRazorFileProvidedThenReturnsDiagnosticsReport()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.CounterRazorFile,
            runAnalyzers: false,
            handlers: TestHandlers.Diagnostics);

        Assert.Contains("Razor Validation", result);
        Assert.Contains("Counter.razor", result);
    }

    [Fact]
    public async Task WhenRazorFileWithNoDiagnosticsThenReportsNoDiagnostics()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.CounterRazorFile,
            runAnalyzers: false,
            handlers: TestHandlers.Diagnostics);

        Assert.Contains("No diagnostics found", result);
    }

    [Fact]
    public async Task WhenAsmxFileProvidedThenReturnsOutline()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.DataServiceFile,
            runAnalyzers: false,
            handlers: TestHandlers.Diagnostics);

        Assert.Contains("Directives", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.StartsWith("Error:"), "Unexpected tool-level error: " + result[..Math.Min(200, result.Length)]);
    }

    [Fact]
    public async Task WhenAshxFileProvidedThenReturnsOutline()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.ImageHandlerFile,
            runAnalyzers: false,
            handlers: TestHandlers.Diagnostics);

        Assert.Contains("ASPX File", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.StartsWith("Error:"), "Unexpected tool-level error: " + result[..Math.Min(200, result.Length)]);
    }

    [Fact]
    public async Task WhenWeatherRazorFileProvidedThenReturnsValidationReport()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(
            filePath: FixturePaths.WeatherRazorFile,
            runAnalyzers: false,
            handlers: TestHandlers.Diagnostics);

        Assert.Contains("Razor Validation", result);
        Assert.Contains("Weather.razor", result);
    }
}
