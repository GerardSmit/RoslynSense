using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.MsBuild.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// What the document cache reuses, rather than what it returns.
/// </summary>
/// <remarks>
/// Asserting on the returned tree proves nothing: a cache that reparsed the whole file on every
/// keystroke returns exactly the same document as one that spliced. The only way to pin the
/// behaviour is to count the work, which is why the cache keeps parse counters.
/// </remarks>
[Collection(SharedState.Name)]
public class MsBuildDocumentCacheTests
{
    private const string Path = @"C:\src\App\App.csproj";

    private static string Props(int entries) =>
        "<Project>\n  <ItemGroup>\n"
        + string.Concat(Enumerable.Range(0, entries).Select(i =>
            $"    <PackageVersion Include=\"Package{i}\" Version=\"1.0.{i}\" />\n"))
        + "  </ItemGroup>\n</Project>";

    [Fact]
    public void TheSameTextIsNotParsedTwice()
    {
        MsBuildDocumentCache.Clear();

        var text = SourceText.From(Props(20));

        var first = MsBuildDocumentCache.For(Path, text);
        var second = MsBuildDocumentCache.For(Path, text);

        // The same tree object, not merely an equal one — every provider that fires for one
        // keystroke shares this.
        Assert.Same(first.Root, second.Root);
        Assert.Equal(1, MsBuildDocumentCache.FullParses);
        Assert.Equal(0, MsBuildDocumentCache.IncrementalParses);
    }

    [Fact]
    public void AKeystrokeSplicesTheEditIntoThePreviousTree()
    {
        MsBuildDocumentCache.Clear();

        var before = SourceText.From(Props(50));
        MsBuildDocumentCache.For(Path, before);

        // A single character typed into one version, the way an editor reports it: a change applied
        // to the previous text, so the two share a lineage and the change range is known exactly.
        int at = before.ToString().IndexOf("1.0.7\"", StringComparison.Ordinal) + 5;
        var after = before.WithChanges(new TextChange(new TextSpan(at, 0), "1"));

        var updated = MsBuildDocumentCache.For(Path, after);

        Assert.Equal(1, MsBuildDocumentCache.FullParses);
        Assert.Equal(1, MsBuildDocumentCache.IncrementalParses);

        // Spliced, not stale: the tree describes the text it was asked about.
        Assert.Equal(after.ToString(), updated.Root.ToFullString());
        Assert.Contains(
            "Version=\"1.0.71\"",
            updated.Root.ToFullString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A file that changed on disk has no relationship to the one previously cached — it may have
    /// been rewritten wholesale, or by another branch checkout. Carrying nodes over would be
    /// unsound, so it reparses.
    /// </summary>
    [Fact]
    public void TextWithNoSharedLineageIsReparsedWhole()
    {
        MsBuildDocumentCache.Clear();

        MsBuildDocumentCache.For(Path, SourceText.From(Props(10)));
        MsBuildDocumentCache.For(Path, SourceText.From(Props(11)));

        Assert.Equal(2, MsBuildDocumentCache.FullParses);
        Assert.Equal(0, MsBuildDocumentCache.IncrementalParses);
    }

    [Fact]
    public void InvalidatingAFileDropsOnlyThatFile()
    {
        MsBuildDocumentCache.Clear();

        const string other = @"C:\src\App\Directory.Packages.props";
        var text = SourceText.From(Props(5));

        var kept = MsBuildDocumentCache.For(other, text);
        MsBuildDocumentCache.For(Path, text);

        MsBuildDocumentCache.Invalidate(Path);

        // The invalidated file reparses; its neighbour still answers from the cache.
        Assert.Same(kept.Root, MsBuildDocumentCache.For(other, text).Root);
        Assert.Equal(2, MsBuildDocumentCache.FullParses);

        MsBuildDocumentCache.For(Path, text);
        Assert.Equal(3, MsBuildDocumentCache.FullParses);
    }
}
