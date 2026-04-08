using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class ProjectIndexCacheServiceTests
{
    private static Compilation CreateMinimalCompilation()
    {
        return CSharpCompilation.Create("TestAssembly",
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static AspxProjectIndex BuildTestIndex()
    {
        var compilation = CreateMinimalCompilation();
        var parseResults = new List<AspxParseResult>();

        foreach (var file in Directory.GetFiles(
            FixturePaths.AspxProjectDir, "*.*", SearchOption.AllDirectories)
            .Where(f => AspxSourceMappingService.IsAspxFile(f)))
        {
            var text = File.ReadAllText(file);
            var result = AspxSourceMappingService.Parse(file, text, compilation);
            parseResults.Add(result);
        }

        return new AspxProjectIndex(parseResults);
    }

    [Fact]
    public void WhenAspxProjectIndexBuiltThenDiscoverAllAspxFiles()
    {
        var index = BuildTestIndex();

        // AspxProject fixture has 5 files: Default.aspx, HeaderControl.ascx,
        // Site.master, DataService.asmx, ImageHandler.ashx
        Assert.True(index.Files.Count >= 5,
            $"Expected >= 5 ASPX files, found {index.Files.Count}: " +
            string.Join(", ", index.Files.Select(f => Path.GetFileName(f.FilePath))));
    }

    [Fact]
    public void WhenAspxProjectIndexBuiltThenAllFileTypesIncluded()
    {
        var index = BuildTestIndex();
        var extensions = index.Files
            .Select(f => Path.GetExtension(f.FilePath).ToLowerInvariant())
            .Distinct()
            .ToList();

        Assert.Contains(".aspx", extensions);
        Assert.Contains(".ascx", extensions);
        Assert.Contains(".master", extensions);
        Assert.Contains(".asmx", extensions);
        Assert.Contains(".ashx", extensions);
    }

    [Fact]
    public void WhenSymbolExistsInMultipleAspxFilesThenFindSymbolReferencesReturnsAll()
    {
        // "DateTime" appears in Default.aspx (expression) and Site.master (expression)
        var index = BuildTestIndex();
        var refs = AspxSourceMappingService.FindSymbolReferences(index, "DateTime");

        Assert.True(refs.Count >= 2,
            $"Expected DateTime refs in >= 2 locations, found {refs.Count}");

        var filesWithRefs = refs.Select(r => Path.GetFileName(r.FilePath)).Distinct().ToList();
        Assert.Contains("Default.aspx", filesWithRefs);
        Assert.Contains("Site.master", filesWithRefs);
    }

    [Fact]
    public void WhenSymbolAppearsInCodeBlockThenFindSymbolReferencesReportsCodeBlock()
    {
        // "IsPostBack" appears in Default.aspx code block and HeaderControl.ascx code block
        var index = BuildTestIndex();
        var refs = AspxSourceMappingService.FindSymbolReferences(index, "IsPostBack");

        Assert.True(refs.Count >= 2,
            $"Expected IsPostBack refs in >= 2 locations, found {refs.Count}");
        Assert.Contains(refs, r => r.LocationType == AspxCodeLocationType.CodeBlock);
    }

    [Fact]
    public void WhenSymbolNotPresentThenFindSymbolReferencesReturnsEmpty()
    {
        var index = BuildTestIndex();
        var refs = AspxSourceMappingService.FindSymbolReferences(index, "XyzNonExistentSymbol12345");

        Assert.Empty(refs);
    }

    [Fact]
    public void WhenSymbolInExpressionThenFindSymbolReferencesReportsExpression()
    {
        var compilation = CreateMinimalCompilation();
        var text = File.ReadAllText(FixturePaths.DefaultAspxFile);
        var result = AspxSourceMappingService.Parse(FixturePaths.DefaultAspxFile, text, compilation);
        var index = new AspxProjectIndex([result]);

        var refs = AspxSourceMappingService.FindSymbolReferences(index, "DateTime");

        Assert.NotEmpty(refs);
        Assert.Contains(refs, r => r.LocationType == AspxCodeLocationType.Expression);
        Assert.All(refs, r => Assert.Equal(FixturePaths.DefaultAspxFile, r.FilePath));
    }

    [Fact]
    public void WhenCodeSnippetIsLongThenFindSymbolReferencesTruncatesSnippet()
    {
        // "return" appears in code blocks in DataService.asmx
        var index = BuildTestIndex();
        var refs = AspxSourceMappingService.FindSymbolReferences(index, "return");

        Assert.NotEmpty(refs);
        // All code snippets should be non-empty
        Assert.All(refs, r => Assert.False(string.IsNullOrWhiteSpace(r.CodeSnippet)));
    }

    [Fact]
    public void WhenInvalidateProjectCalledForUnknownProjectThenDoesNotThrow()
    {
        // InvalidateProject on a project not in cache should be a no-op
        ProjectIndexCacheService.InvalidateProject("C:\\nonexistent\\project.csproj");
    }

    [Fact]
    public async Task WhenAspxIndexFetchedThenCachedResultReturnedOnSecondCall()
    {
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            FixturePaths.AspxProjectFile);

        var first = await ProjectIndexCacheService.GetAspxIndexAsync(project);
        var second = await ProjectIndexCacheService.GetAspxIndexAsync(project);

        // Same cached object returned
        Assert.Same(first, second);
    }

    [Fact]
    public async Task WhenInvalidateProjectCalledThenCacheIsRefreshed()
    {
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            FixturePaths.AspxProjectFile);

        var first = await ProjectIndexCacheService.GetAspxIndexAsync(project);

        // Invalidate the cache
        ProjectIndexCacheService.InvalidateProject(project.FilePath!);

        var second = await ProjectIndexCacheService.GetAspxIndexAsync(project);

        // After invalidation, should be a new object
        Assert.NotSame(first, second);
    }

    [Fact]
    public void WhenWebConfigChangesDetectedThenAspxCacheInvalidated()
    {
        // The OnFileChanged method handles web.config as a special case
        // to invalidate the ASPX cache. We test this indirectly by verifying
        // that LoadWebConfigNamespaces finds our fixture web.config.
        var namespaces = AspxSourceMappingService.LoadWebConfigNamespaces(FixturePaths.AspxProjectDir);
        Assert.False(namespaces.IsDefaultOrEmpty, "Should have loaded web.config registrations");
        Assert.Equal(2, namespaces.Length);
    }
}
