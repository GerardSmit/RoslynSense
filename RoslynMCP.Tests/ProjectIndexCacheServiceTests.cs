using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;
using RoslynMCP.Languages.WebForms.Core;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
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

    /// <summary>
    /// The index orders its files however they arrived.
    /// </summary>
    /// <remarks>
    /// Both builders parse in parallel into a <see cref="System.Collections.Concurrent.ConcurrentBag{T}"/>,
    /// so unordered the index comes out in whichever order the thread pool finished. Consumers
    /// answer with the first match — resolving a code-behind class back to the markup naming it,
    /// above all — and that is invisible until two files name the same class, at which point the
    /// same question gets a different answer between two runs of it.
    /// </remarks>
    [Fact]
    public void TheIndexOrdersItsFilesHoweverTheyArrive()
    {
        var arrived = BuildTestIndex().Files.ToList();
        arrived.Reverse();

        var files = new AspxProjectIndex(arrived).Files.Select(f => f.FilePath).ToList();

        Assert.Equal([.. files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)], files);
    }

    // --- Markup reference search ------------------------------------------------------------
    //
    // These replace the AspxSourceMappingService.FindSymbolReferences tests. That search matched
    // the symbol's name as a substring of every expression and code block in the index, so it
    // asserted things that were only ever true by accident: that "DateTime" is a reference
    // because the word appears, that "return" has references at all. The MCP tools now go
    // through the same bound search the editor uses, so what is asserted is what is actually a
    // reference.

    [Fact]
    public async Task WhenSymbolIsUsedInInlineCodeThenOnlyRealMentionsAreReferences()
    {
        var document = await AspxDocumentService.GetAsync(FixturePaths.EventWiringAspxFile, default);
        var total = document!.CodeBehind!.GetMembers("Total").Single();

        var references = await AspxReferenceService.FindAsync(total, document.Project, default);

        var inPage = references
            .Where(r => string.Equals(r.FilePath, FixturePaths.EventWiringAspxFile, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // The call in <script runat="server"> and the one in <%= %>. The page also writes the
        // word in a comment and in a string literal; the substring search counted both.
        Assert.Equal(2, inPage.Count);
        Assert.All(inPage, r => Assert.Equal("Total", r.Text.ToString(r.Span)));
    }

    [Fact]
    public async Task WhenSymbolIsNotMentionedThenNoReferencesAreFound()
    {
        var document = await AspxDocumentService.GetAsync(FixturePaths.EventWiringAspxFile, default);
        var handler = document!.CodeBehind!.GetMembers("Existing_Click").Single();

        var references = await AspxReferenceService.FindAsync(handler, document.Project, default);

        // The OnClick attribute that names it, and nothing in Default.aspx or Site.master, which
        // never mention it.
        Assert.NotEmpty(references);
        Assert.All(references, r =>
            Assert.Equal(
                Path.GetFileName(FixturePaths.EventWiringAspxFile),
                Path.GetFileName(r.FilePath)));
    }

    [Fact]
    public async Task WhenFindingUsagesOfAMarkupCalledMethodThenCommentsAndStringsAreNotReported()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.EventWiringCodeBehindFile,
            markupSnippet: "protected int [|Total|]() => 42;",
            fmt: new MarkdownFormatter());

        Assert.Contains("ASPX References", result);
        Assert.Contains("2 reference(s) in ASPX/ASCX files", result);

        // The two lines the old substring search also reported. Their absence is the fix.
        Assert.DoesNotContain("only mentioned here", result);
        Assert.DoesNotContain("string note", result);
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
