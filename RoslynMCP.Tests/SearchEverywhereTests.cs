using Microsoft.CodeAnalysis;
using RoslynMCP.Lsp.Search;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Search Everywhere: the ranked one-box search behind the extension's Ctrl+T. Covers the tier
/// arithmetic (an exact type outranks an exact member outranks a fuzzy type), the container words
/// that narrow a query, the kind filters, and files as first-class results.
/// </summary>
public class SearchEverywhereTests
{
    [Fact]
    public async Task AnExactTypeNameComesFirst()
    {
        var hits = await SearchAsync("Calculator");

        Assert.Equal(SearchItemKind.Type, hits[0].Kind);
        Assert.Equal("Calculator", hits[0].Name);
    }

    [Fact]
    public async Task CamelHumpsFindTheType()
    {
        var hits = await SearchAsync("calc");

        Assert.Contains(hits, h => h.Kind == SearchItemKind.Type && h.Name == "Calculator");
    }

    [Fact]
    public async Task AnExactMemberOutranksAFuzzyType()
    {
        // "Add" is a method on Calculator; nothing is a type called exactly that, so the member
        // tier (exact) has to beat the type tier (non-exact).
        var hits = await SearchAsync("Add");

        Assert.Equal(SearchItemKind.Member, hits[0].Kind);
        Assert.Equal("Add", hits[0].Name);
    }

    [Fact]
    public async Task ContainerWordsNarrowToTheMemberOfThatType()
    {
        var hits = await SearchAsync("Calculator.Add");

        var first = hits[0];
        Assert.Equal(SearchItemKind.Member, first.Kind);
        Assert.Equal("Add", first.Name);
        Assert.Equal("SampleProject.Calculator", first.Container);

        // Everything returned must live in the named container, not merely match the last word.
        Assert.All(hits, h => Assert.Contains("Calculator", h.Container ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWrongContainerFindsNothing()
    {
        Assert.Empty(await SearchAsync("TextUtilities.Add"));
    }

    [Fact]
    public async Task FilesAreResultsToo()
    {
        var hits = await SearchAsync("Calculator.cs");

        // The dot in a file name is not a container separator.
        var file = hits.FirstOrDefault(h => h.Kind == SearchItemKind.File);
        Assert.True(file is not null, $"no file hit; got: {string.Join(", ", hits.Take(5).Select(h => $"{h.Kind}:{h.Name}"))}");
        Assert.Equal("Calculator.cs", file!.Name);
    }

    [Fact]
    public async Task KindFiltersRestrictTheList()
    {
        var types = await SearchAsync("t:Calculator");
        Assert.All(types, h => Assert.Equal(SearchItemKind.Type, h.Kind));

        var files = await SearchAsync("f:Calculator");
        Assert.All(files, h => Assert.Equal(SearchItemKind.File, h.Kind));

        var members = await SearchAsync("m:Add");
        Assert.All(members, h => Assert.Equal(SearchItemKind.Member, h.Kind));
    }

    [Fact]
    public async Task FilesCanBeExcludedForWorkspaceSymbol()
    {
        var hits = await SearchAsync("Calculator", includeFiles: false);

        Assert.DoesNotContain(hits, h => h.Kind == SearchItemKind.File);
        Assert.Contains(hits, h => h.Kind == SearchItemKind.Type);
    }

    [Fact]
    public async Task TheShorterNameWinsATie()
    {
        // Both are types matching the prefix equally well; the shorter one is the likelier target.
        var hits = await SearchAsync("Ranking", includeFiles: false);
        var names = hits.Where(h => h.Kind == SearchItemKind.Type).Select(h => h.Name).ToList();

        if (names.Count > 1)
            Assert.True(names[0].Length <= names[1].Length, string.Join(", ", names));
    }

    [Fact]
    public async Task ResultsAreCappedButRankedFirst()
    {
        var hits = await SearchAsync("a", maxResults: 5);

        Assert.True(hits.Count <= 5);
        // The cap is applied after sorting, so the list is still in score order.
        Assert.Equal(hits.OrderBy(h => h.Score).Select(h => h.Score), hits.Select(h => h.Score));
    }

    [Fact]
    public async Task AnEmptyQueryReturnsNothing()
    {
        Assert.Empty(await SearchAsync("   "));
    }

    [Fact]
    public async Task FilesThatAreNotCompiledAreStillFound()
    {
        // .proto is not a Roslyn document, so a search over the compilation could never see it.
        var hits = await SearchAsync(".proto", FixturePaths.ProtoProjectFile);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal(SearchItemKind.File, h.Kind));
        Assert.Contains(hits, h => h.Name == "widgets.proto");
        Assert.All(hits, h => Assert.EndsWith(".proto", h.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnExtensionQueryDoesNotDragInSymbols()
    {
        var hits = await SearchAsync(".cs", FixturePaths.SampleProjectFile);

        Assert.NotEmpty(hits);
        Assert.DoesNotContain(hits, h => h.Kind != SearchItemKind.File);
    }

    [Fact]
    public async Task NonCompiledFilesAreFoundByNameToo()
    {
        var hits = await SearchAsync("widgets", FixturePaths.ProtoProjectFile);

        Assert.Contains(hits, h => h.Kind == SearchItemKind.File && h.Name == "widgets.proto");
    }

    [Fact]
    public async Task BuildOutputIsNeverAResult()
    {
        // obj/ holds a generated AssemblyInfo.cs for every SDK project — it used to be the first
        // hit for "assembly", which is never what was wanted.
        var hits = await SearchAsync("AssemblyInfo", FixturePaths.SampleProjectFile);
        string obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        string bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";

        Assert.DoesNotContain(hits, h => h.FilePath.Contains(obj, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(hits, h => h.FilePath.Contains(bin, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedFilesComeLast()
    {
        var hits = await SearchAsync("Designer", FixturePaths.AspxProjectFile);

        var names = hits.Where(h => h.Kind == SearchItemKind.File).Select(h => h.Name).ToList();
        int handWritten = names.FindIndex(n => n.Equals("Designer.aspx.cs", StringComparison.OrdinalIgnoreCase));
        int generated = names.FindIndex(n => n.Equals("Designer.aspx.designer.cs", StringComparison.OrdinalIgnoreCase));

        Assert.True(handWritten >= 0 && generated >= 0, string.Join(", ", names));
        Assert.True(handWritten < generated, $"generated file outranked hand-written: {string.Join(", ", names)}");
    }

    [Theory]
    [InlineData(@"C:\src\App\obj\Debug\App.AssemblyInfo.cs", true)]
    [InlineData(@"C:\src\App\bin\Release\App.dll", true)]
    [InlineData(@"C:\src\App\node_modules\x\index.js", true)]
    [InlineData(@"C:\src\App\Models\Result.cs", false)]
    public void BuildOutputIsRecognisedByPath(string path, bool excluded) =>
        Assert.Equal(excluded, SearchFileRules.IsExcluded(path));

    [Theory]
    [InlineData("Form1.Designer.cs", true)]
    [InlineData("Widgets.g.cs", true)]
    [InlineData("SampleProject.AssemblyInfo.cs", true)]
    [InlineData("Calculator.cs", false)]
    [InlineData("widgets.proto", false)]
    public void GeneratedFilesAreRecognisedByName(string name, bool generated) =>
        Assert.Equal(generated, SearchFileRules.IsGenerated(name));

    private static async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query, int maxResults = 50, bool includeFiles = true)
    {
        var solution = await SolutionAsync(FixturePaths.SampleProjectFile);
        return await SearchEverywhere.SearchAsync(solution, query, maxResults, default, includeFiles);
    }

    private static async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, string projectFile)
    {
        var solution = await SolutionAsync(projectFile);
        return await SearchEverywhere.SearchAsync(solution, query, maxResults: 50, default);
    }

    private static async Task<Solution> SolutionAsync(string projectFile)
    {
        // The index caches per directory; a fixture another test wrote must not be answered
        // from a stale walk.
        SolutionFileIndex.Clear();

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(projectFile, default);
        return project.Solution;
    }
}
