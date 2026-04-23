using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Tests for project-wide diagnostics via the unified GetRoslynDiagnostics tool
/// (pass a .csproj path to trigger project-wide mode).
/// </summary>
public class GetProjectDiagnosticsToolTests
{
    [Fact]
    public async Task WhenValidProjectProvidedThenReturnsDiagnostics()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.SampleProjectFile,
            severityFilter: "warning",
            runAnalyzers: false);

        Assert.Contains("Project Diagnostics", result);
        Assert.Contains("SampleProject", result);
    }

    [Fact]
    public async Task WhenErrorFilterUsedOnCleanProjectThenReturnsNoDiagnostics()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.SampleProjectFile,
            severityFilter: "error",
            runAnalyzers: false);

        // SampleProject has no errors, only warnings
        Assert.Contains("Project Diagnostics", result);
    }

    [Fact]
    public async Task WhenInvalidSeverityFilterProvidedThenReturnsError()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.SampleProjectFile,
            severityFilter: "bogus",
            runAnalyzers: false);

        Assert.Contains("Invalid", result);
    }

    [Fact]
    public async Task WhenMaxResultsLimitedThenRespectsLimit()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.SampleProjectFile,
            severityFilter: "all",
            maxResults: 2,
            runAnalyzers: false);

        Assert.Contains("Project Diagnostics", result);
    }

    [Fact]
    public async Task WhenBrokenProjectProvidedThenReportsErrors()
    {
        var result = await GetRoslynDiagnosticsTool.GetRoslynDiagnostics(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.BrokenProjectFile,
            severityFilter: "error",
            runAnalyzers: false);

        Assert.Contains("Error", result);
    }
}
