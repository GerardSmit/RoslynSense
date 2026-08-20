using Microsoft.CodeAnalysis;
using RoslynMCP.Lsp.Completion;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Lsp.Search;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Search Everywhere: the ranked one-box search behind the extension's Ctrl+T. Covers the tier
/// arithmetic (types over methods over the rest, each kind's exact matches over its fuzzy ones),
/// the container words that narrow a query, the kind filters, and files as first-class results.
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
    public async Task ATypeWearingAHousePrefixOutranksAMemberSpelledExactly()
    {
        // Typing the part of a type name you remember is how people search for types whose prefix
        // is a team convention: "CatalogGateway" is meant to land on VendorCatalogGateway, not on
        // the property that happens to be spelled that way.
        var hits = await SearchAsync("CatalogGateway", includeFiles: false);

        Assert.Equal(SearchItemKind.Type, hits[0].Kind);
        Assert.Equal("VendorCatalogGateway", hits[0].Name);
        Assert.Contains(hits, h => h.Kind == SearchItemKind.Member && h.Name == "CatalogGateway");
    }

    [Fact]
    public async Task AMethodOutranksAPropertyOfTheSameName()
    {
        // Both match exactly, so only the kind separates them — and Ctrl+T is how people open
        // methods.
        var hits = await SearchAsync("SweepCatalog", includeFiles: false);

        var kinds = hits.Where(h => h.Name == "SweepCatalog").Select(h => h.SymbolKind).ToList();
        Assert.Equal([LspSymbolKind.Method, LspSymbolKind.Property], kinds);
    }

    [Theory]
    [InlineData("CatalogGateway", "VendorCatalogGateway", true)]
    [InlineData("ShopController", "SomePrefixShopController", true)]
    [InlineData("CatalogGateway", "CatalogGateway", true)]
    [InlineData("CatGate", "VendorCatalogGateway", false)]
    [InlineData("catalogGateway", "VendorCatalogGateway", false)]
    [InlineData("Gateway", "VendorCatalogGatewayFactory", true)]
    public void AWholeWordMatchIsAContiguousRunOfWholeWords(
        string pattern, string candidate, bool wholeWord)
    {
        // The tier above a plain camel-hump hit: every character landed on one unbroken run, and
        // every word that run touched was consumed entirely.
        var match = new IdentifierMatcher(pattern).Match(candidate);

        Assert.NotNull(match);
        Assert.Equal(wholeWord, match.Value.Score.IsWholeWordMatch());
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
    [InlineData("Calculator.cs:12")]
    [InlineData("Calculator.cs:line 12")]
    [InlineData("Calculator.cs:regel 12")]
    [InlineData("Calculator.cs:Zeile 12")]
    [InlineData("Calculator.cs:ligne 12")]
    [InlineData("Calculator.cs(12)")]
    [InlineData("Calculator.cs line 12")]
    [InlineData("Calculator.cs regel 12")]
    public async Task ALineReferenceNarrowsToFilesAndCarriesTheLine(string query)
    {
        // The shapes a pasted stack trace or compiler message produces, including .NET's
        // localised "line" (Dutch regel, German Zeile, French ligne, …).
        var hits = await SearchAsync(query);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal(SearchItemKind.File, h.Kind));

        var file = hits.First(h => h.Name.Equals("Calculator.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(11, file.Line);
    }

    [Theory]
    [InlineData("Calculator.cs:12:5")]
    [InlineData("Calculator.cs(12,5)")]
    public async Task AColumnInTheLineReferenceIsCarriedToo(string query)
    {
        var hits = await SearchAsync(query);

        var file = hits.First(h => h.Name.Equals("Calculator.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(11, file.Line);
        Assert.Equal(4, file.Character);
    }

    [Theory]
    [InlineData("Add(0")]
    [InlineData("Calculator:2")]
    public async Task TrailingDigitsOnASymbolishQueryKeepSymbolResults(string query)
    {
        // "Parse(0" is someone typing a signature, "Foo:2" maybe a habit from elsewhere —
        // neither names a file, so the trailing digits strip without dropping the symbols.
        var hits = await SearchAsync(query);

        Assert.Contains(hits, h => h.Kind != SearchItemKind.File);
    }

    [Fact]
    public async Task ABareExtensionWithALineCarriesTheLine()
    {
        // ".cs:12" narrows to files by extension and still opens on the line.
        var hits = await SearchAsync(".cs:12");

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal(SearchItemKind.File, h.Kind));
        Assert.All(hits, h => Assert.Equal(11, h.Line));
    }

    [Fact]
    public async Task ANameEndingInDigitsIsNotALineReference()
    {
        // "Form 12" would be, "Warnings" is not — digits inside a name must not be eaten.
        var hits = await SearchAsync("Warnings");

        Assert.Contains(hits, h => h.Kind != SearchItemKind.File);
    }

    [Fact]
    public async Task AForcedKindOverridesThePrefix()
    {
        // The panel's Classes tab forces types even though the query says members.
        var solution = await SolutionAsync(FixturePaths.SampleProjectFile);
        var hits = await SearchEverywhere.SearchAsync(
            solution, "m:Calculator", maxResults: 50, default, only: SearchItemKind.Type);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal(SearchItemKind.Type, h.Kind));
    }

    [Fact]
    public async Task MetadataTypesAppearOnlyWhenAskedFor()
    {
        var solution = await SolutionAsync(FixturePaths.SampleProjectFile);

        var without = await SearchEverywhere.SearchAsync(
            solution, "Stopwatch", maxResults: 50, default);
        Assert.DoesNotContain(without, h => h.Uri is not null);

        var with = await SearchEverywhere.SearchAsync(
            solution, "Stopwatch", maxResults: 50, default, includeMetadata: true);
        var metadata = with.FirstOrDefault(h => h.Uri is not null);

        Assert.NotNull(metadata);
        Assert.Equal(SearchItemKind.Type, metadata!.Kind);
        Assert.StartsWith("roslynsense-metadata:", metadata.Uri);
        Assert.Equal("Stopwatch", metadata.Name);
        Assert.Equal("System.Diagnostics", metadata.Container);
    }

    [Fact]
    public async Task MetadataTypesSharingANameSurviveDedup()
    {
        // Func`1..Func`17 share the stripped name, the namespace and the assembly — only the
        // reflection name in the Uri tells them apart, so it must be part of the dedup key.
        var solution = await SolutionAsync(FixturePaths.SampleProjectFile);
        var hits = await SearchEverywhere.SearchAsync(
            solution, "t:Func", maxResults: 50, default, includeMetadata: true);

        var uris = hits
            .Where(h => h.Uri is not null && h.Name == "Func")
            .Select(h => h.Uri)
            .Distinct()
            .ToList();
        Assert.True(uris.Count > 1, $"expected several Func arities, got {uris.Count}");
    }

    [Fact]
    public async Task ASolutionTypeOutranksAMetadataTypeOfTheSameName()
    {
        // "Calculator" exists in the fixture; every metadata hit must sit below every source hit.
        var solution = await SolutionAsync(FixturePaths.SampleProjectFile);
        var hits = await SearchEverywhere.SearchAsync(
            solution, "Calculator", maxResults: 50, default, includeMetadata: true);

        Assert.Null(hits[0].Uri);
        int firstMetadata = hits.ToList().FindIndex(h => h.Uri is not null);
        int lastSource = hits.ToList().FindLastIndex(h => h.Uri is null && h.Kind != SearchItemKind.File);
        if (firstMetadata >= 0)
            Assert.True(lastSource < firstMetadata,
                $"metadata hit at {firstMetadata} before source hit at {lastSource}");
    }

    [Theory]
    [InlineData(@"C:\src\App\obj\Debug\App.AssemblyInfo.cs", true)]
    [InlineData(@"C:\src\App\bin\Release\App.dll", true)]
    [InlineData(@"C:\src\App\node_modules\x\index.js", true)]
    [InlineData(@"C:\src\App\Models\Result.cs", false)]
    public void BuildOutputIsRecognisedByPath(string path, bool excluded) =>
        Assert.Equal(excluded, SearchFileRules.IsExcluded(path));

    [Fact]
    public async Task ABinaryAssetNeverOutranksSourceCode()
    {
        // Calc.png matches "calc" exactly, Calculator only by prefix — yet nobody searching
        // "calc" wants an image ahead of code, so the asset must sit below every code hit.
        var hits = (await SearchAsync("calc")).ToList();

        int asset = hits.FindIndex(h => h.Name.Equals("Calc.png", StringComparison.OrdinalIgnoreCase));
        int lastCode = hits.FindLastIndex(h =>
            h.Kind != SearchItemKind.File
            || h.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

        Assert.True(asset >= 0, $"Calc.png missing: {string.Join(", ", hits.Select(h => h.Name))}");
        Assert.True(lastCode < asset,
            $"asset at {asset} outranked code at {lastCode}: {string.Join(", ", hits.Select(h => h.Name))}");
    }

    [Theory]
    [InlineData("logo.png", true)]
    [InlineData("Archive.ZIP", true)]
    [InlineData("App.dll", true)]
    [InlineData("Calculator.cs", false)]
    [InlineData("web.config", false)]
    [InlineData("widgets.proto", false)]
    public void BinaryAssetsAreRecognisedByExtension(string name, bool binary) =>
        Assert.Equal(binary, SearchFileRules.IsBinaryAsset(name));

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
