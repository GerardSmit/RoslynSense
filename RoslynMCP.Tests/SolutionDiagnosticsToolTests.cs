using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>Solution-wide diagnostics for the AI: one call instead of a per-file loop that can
/// only report on files someone thought to ask about.</summary>
public class SolutionDiagnosticsToolTests
{
    [Fact]
    public async Task ReportsErrorsGroupedByProject()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.BrokenProjectFile);

        string result = await SolutionDiagnosticsTool.GetSolutionDiagnostics(new MarkdownFormatter());

        Assert.Contains("Solution diagnostics", result);
        Assert.Contains("BrokenProject", result);
        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsAnInvalidSeverityFilter()
    {
        string result = await SolutionDiagnosticsTool.GetSolutionDiagnostics(
            new MarkdownFormatter(), severityFilter: "catastrophic");

        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public async Task RespectsTheResultCap()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.BrokenProjectFile);

        string result = await SolutionDiagnosticsTool.GetSolutionDiagnostics(
            new MarkdownFormatter(), severityFilter: "all", maxResults: 1);

        int listed = result.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal));
        Assert.True(listed <= 1, $"listed {listed} entries despite a cap of 1");
    }

    [Fact]
    public async Task SaysSoWhenAProjectIsClean()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        string result = await SolutionDiagnosticsTool.GetSolutionDiagnostics(
            new MarkdownFormatter(), severityFilter: "error");

        // The sample fixture compiles; a clean solution should say so rather than print a
        // header with nothing under it.
        Assert.Contains("No error diagnostics", result);
    }
}
