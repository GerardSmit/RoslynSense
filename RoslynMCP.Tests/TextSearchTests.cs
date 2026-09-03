using RoslynMCP.Lsp.Search;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The Text tab of Search Everywhere: a literal scan over the same file corpus the file search
/// walks, so a match in a .proto or a .md is as reachable as one in a .cs.
/// </summary>
public class TextSearchTests
{
    [Fact]
    public async Task FindsALiteralInSource()
    {
        var (hits, _) = await SearchAsync("class Calculator");

        var hit = hits.FirstOrDefault(h => h.FilePath.EndsWith("Calculator.cs", StringComparison.OrdinalIgnoreCase));
        Assert.True(hit is not null, $"no hit in Calculator.cs; got: {string.Join(", ", hits.Take(5).Select(h => h.FilePath))}");
        Assert.Contains("class Calculator", hit!.LineText, StringComparison.Ordinal);
        Assert.True(hit.Character >= 0);
    }

    [Fact]
    public async Task TheMatchIsCaseInsensitive()
    {
        var (hits, _) = await SearchAsync("CLASS CALCULATOR");

        Assert.Contains(hits, h => h.FilePath.EndsWith("Calculator.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NonCompiledFilesAreSearchedToo()
    {
        // The .csproj itself is not a Roslyn document, but its text is part of the solution.
        var (hits, _) = await SearchAsync("TargetFramework");

        Assert.Contains(hits, h => h.FilePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheCapIsHonouredAndReported()
    {
        // Single characters occur everywhere; the cap has to hold and say it was hit.
        var (hits, truncated) = await SearchAsync("e", maxResults: 5);

        Assert.True(hits.Count <= 5);
        Assert.True(truncated);
    }

    [Fact]
    public async Task AnEmptyQueryFindsNothing()
    {
        var (hits, truncated) = await SearchAsync("   ");

        Assert.Empty(hits);
        Assert.False(truncated);
    }

    [Fact]
    public async Task AUtf16FileIsTextNotBinary()
    {
        // Utf16Notes.txt is UTF-16 LE with a BOM: half its bytes are NUL, but the BOM vouches
        // for it, so the binary probe must let it through to the line scan.
        var (hits, _) = await SearchAsync("utf16-needle");

        Assert.Contains(hits, h => h.FilePath.EndsWith("Utf16Notes.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PositionsPointAtTheMatch()
    {
        var (hits, _) = await SearchAsync("class Calculator");
        var hit = hits.First(h => h.FilePath.EndsWith("Calculator.cs", StringComparison.OrdinalIgnoreCase));

        string line = (await File.ReadAllLinesAsync(hit.FilePath))[hit.Line];
        Assert.Equal("class Calculator", line.Substring(hit.Character, "class Calculator".Length), ignoreCase: true);
    }

    private static async Task<(IReadOnlyList<TextHit> Hits, bool Truncated)> SearchAsync(
        string query, int maxResults = 100)
    {
        SolutionFileIndex.Clear();
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.SampleProjectFile, default);
        return await TextSearch.SearchAsync(project.Solution, query, maxResults, default);
    }
}
