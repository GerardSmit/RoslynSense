using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Solution folders — groupings that live in the solution file rather than on disk.
/// </summary>
/// <remarks>
/// Shared, because one test below binds a solution, and which solution this process is bound to
/// is what everything above the workspace means by "this session" — the launch targets, the
/// package list, the symbol search. Binding it from a parallel collection reached into whatever
/// was being asked at that moment.
/// </remarks>
[Collection(SharedState.Name)]
public sealed class SolutionFolderTests : IDisposable
{
    private const string EmptySolution = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        Global
        	GlobalSection(SolutionProperties) = preSolution
        		HideSolutionNode = FALSE
        	EndGlobalSection
        EndGlobal

        """;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"roslyn-sense-slnfolder-{Guid.NewGuid():N}");

    public SolutionFolderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task AddingAFolderWritesItIntoTheSolutionFile()
    {
        string solution = Path.Combine(_directory, "Test.sln");
        await File.WriteAllTextAsync(solution, EmptySolution);

        var result = await SolutionTreeEditHandler.EditAsync(
            new SolutionTreeEditParams(
                "addSolutionFolder",
                TargetUri: LspConverters.PathToUri(solution),
                Name: "Shared"),
            default);

        Assert.True(result.Ok, result.Message);
        Assert.Contains("Shared", await File.ReadAllTextAsync(solution));
    }

    // No test for "named a missing solution and none is open": the bound solution is process-wide
    // static state that another test in this assembly may already have set, so asserting the
    // failure would pass or fail on test order.

    [Fact]
    public async Task AFolderFallsBackToTheOpenSolutionWhenTheClientNamesNothingUsable()
    {
        // The tree node's id is only an echo of what the server bound; when the two disagree the
        // server's own answer is the right one, and the edit should still land.
        string solution = Path.Combine(_directory, "Bound.sln");
        await File.WriteAllTextAsync(solution, EmptySolution);

        // Restored when the scope ends. The binding outlives the test otherwise, and this one
        // names a file under a directory the test deletes — so every later test asking what this
        // session's solution is got a solution that no longer exists.
        using var bound = WorkspaceService.BindSolutionForTesting(solution);

        var result = await SolutionTreeEditHandler.EditAsync(
            new SolutionTreeEditParams(
                "addSolutionFolder",
                TargetUri: LspConverters.PathToUri(Path.Combine(_directory, "Stale.sln")),
                Name: "Shared"),
            default);

        Assert.True(result.Ok, result.Message);
        Assert.Contains("Shared", await File.ReadAllTextAsync(solution));
    }
}
