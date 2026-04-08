using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class ValidateFileToolTests
{
    [Fact]
    public async Task WhenEmptyPathProvidedThenReturnsError()
    {
        var result = await ValidateFileTool.ValidateFile(filePath: "", runAnalyzers: false);

        Assert.Contains("Error", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenWhitespacePathProvidedThenReturnsError()
    {
        var result = await ValidateFileTool.ValidateFile(filePath: "   ", runAnalyzers: false);

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenNonExistentFileProvidedThenReturnsFileNotFoundError()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: Path.Combine(FixturePaths.SampleProjectDir, "DoesNotExist.cs"),
            runAnalyzers: false);

        Assert.Contains("does not exist", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenValidFileProvidedThenReportsNoSyntaxErrors()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.CalculatorFile,
            runAnalyzers: false);

        Assert.Contains("No syntax errors found", result);
    }

    [Fact]
    public async Task WhenValidFileProvidedThenReportsProjectLoaded()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.CalculatorFile,
            runAnalyzers: false);

        Assert.Contains("Project loaded successfully", result);
    }

    [Fact]
    public async Task WhenValidFileProvidedThenReportsDocumentFound()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.CalculatorFile,
            runAnalyzers: false);

        Assert.Contains("Document found", result);
    }

    [Fact]
    public async Task WhenBrokenSyntaxFileProvidedThenReportsSyntaxErrors()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.BrokenSyntaxFile,
            runAnalyzers: false);

        Assert.Contains("Syntax errors found", result);
        Assert.Contains("Line", result);
    }

    [Fact]
    public async Task WhenBrokenSemanticFileProvidedThenReportsSemanticAndCompilationErrors()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.BrokenSemanticFile,
            runAnalyzers: false);

        Assert.Contains("Semantic errors found", result);
        Assert.Contains("Compilation and analyzer diagnostics", result);
        Assert.Contains("CS", result);
    }

    [Fact]
    public async Task WhenAnalyzersEnabledThenValidationIncludesAnalyzerDiagnostics()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.WarningsFile,
            runAnalyzers: true);

        Assert.Contains("Running code analyzers", result);
        Assert.Contains("Compilation and analyzer diagnostics", result);
        Assert.Contains("CA", result);
    }

    [Fact]
    public async Task WhenFileIsNotPartOfProjectThenValidationListsProjectDocuments()
    {
        var writer = new StringWriter();

        await ValidateFileTool.ValidateFileInProjectContextAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".cs"),
            FixturePaths.SampleProjectFile,
            writer,
            runAnalyzers: false);

        var result = writer.ToString();

        Assert.Contains("Error: File not found in the project documents.", result);
        Assert.Contains("All project documents:", result);
    }

    [Fact]
    public async Task WhenAspxFileProvidedThenReturnsOutlineWithDirectives()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.DefaultAspxFile,
            runAnalyzers: false);

        // Should produce an ASPX outline (not a C# validation error)
        Assert.Contains("Directives", result, StringComparison.OrdinalIgnoreCase);
        // Should not return a tool-level error
        Assert.False(result.StartsWith("Error:"), "Unexpected tool-level error: " + result[..Math.Min(200, result.Length)]);
    }

    [Fact]
    public async Task WhenAscxFileProvidedThenReturnsOutline()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.HeaderControlFile,
            runAnalyzers: false);

        Assert.Contains("Directives", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.StartsWith("Error:"), "Unexpected tool-level error: " + result[..Math.Min(200, result.Length)]);
    }

    [Fact]
    public async Task WhenMasterPageProvidedThenReturnsOutline()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.SiteMasterFile,
            runAnalyzers: false);

        Assert.Contains("Directives", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.StartsWith("Error:"), "Unexpected tool-level error: " + result[..Math.Min(200, result.Length)]);
    }

    [Fact]
    public async Task WhenRazorFileProvidedThenReturnsDiagnosticsReport()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.CounterRazorFile,
            runAnalyzers: false);

        // Should produce a Razor validation report
        Assert.Contains("Razor Validation", result);
        Assert.Contains("Counter.razor", result);
    }

    [Fact]
    public async Task WhenRazorFileWithNoDiagnosticsThenReportsNoDiagnostics()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.CounterRazorFile,
            runAnalyzers: false);

        // Counter.razor should compile clean
        Assert.Contains("No diagnostics found", result);
    }

    [Fact]
    public async Task WhenAsmxFileProvidedThenReturnsOutline()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.DataServiceFile,
            runAnalyzers: false);

        Assert.Contains("Directives", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.StartsWith("Error:"), "Unexpected tool-level error: " + result[..Math.Min(200, result.Length)]);
    }

    [Fact]
    public async Task WhenAshxFileProvidedThenReturnsOutline()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.ImageHandlerFile,
            runAnalyzers: false);

        Assert.Contains("ASPX File", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.StartsWith("Error:"), "Unexpected tool-level error: " + result[..Math.Min(200, result.Length)]);
    }

    [Fact]
    public async Task WhenValidCSharpFileProvidedThenReturnsProjectContextOutput()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.CalculatorFile,
            runAnalyzers: false);

        // Calculator.cs is validated within project context
        Assert.Contains("Loading project:", result);
        Assert.Contains("Project loaded successfully", result);
        Assert.Contains("Document found", result);
    }

    [Fact]
    public async Task WhenBrokenCSharpFileProvidedThenReportsSyntaxErrors()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.BrokenSyntaxFile,
            runAnalyzers: false);

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenWeatherRazorFileProvidedThenReturnsValidationReport()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.WeatherRazorFile,
            runAnalyzers: false);

        Assert.Contains("Razor Validation", result);
        Assert.Contains("Weather.razor", result);
    }

    [Fact]
    public async Task WhenBrokenCSharpFileValidatedThenShowsSyntaxErrors()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.BrokenSyntaxFile,
            runAnalyzers: false);

        Assert.Contains("Syntax errors found", result);
    }

    [Fact]
    public async Task WhenBrokenSemanticFileValidatedThenShowsSemanticErrors()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.BrokenSemanticFile,
            runAnalyzers: false);

        Assert.Contains("Semantic errors found", result);
    }

    [Fact]
    public async Task WhenWarningsFileValidatedWithAnalyzersThenShowsAnalyzerDiagnostics()
    {
        var result = await ValidateFileTool.ValidateFile(
            filePath: FixturePaths.WarningsFile,
            runAnalyzers: true);

        Assert.Contains("Loading project:", result);
        Assert.Contains("analyzer", result, StringComparison.OrdinalIgnoreCase);
    }
}
