using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Services;
using RoslynMCP.Services.Testing;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Test impact selection: the diff parser, the per-test coverage map it is matched against, and
/// the choice the two make together. The point of the feature is running fewer tests without
/// running the wrong fewer — so what is asserted here is mostly which tests come back, and why.
/// </summary>
public class TestImpactTests
{
    // ---- The diff parser ----------------------------------------------------------------

    [Fact]
    public void ParseUnifiedDiff_ReadsNewSideLineRanges()
    {
        const string diff = """
            diff --git a/src/Widget.cs b/src/Widget.cs
            index 1111111..2222222 100644
            --- a/src/Widget.cs
            +++ b/src/Widget.cs
            @@ -10,3 +10,4 @@ public class Widget
            +    public int Extra => 1;
            @@ -40 +41,2 @@
            +    // and here
            """;

        var files = GitChangeService.ParseUnifiedDiff(diff, @"C:\repo");

        var file = Assert.Single(files);
        Assert.Equal(Path.GetFullPath(@"C:\repo\src/Widget.cs"), file.FilePath);
        Assert.Equal([new LineRange(10, 13), new LineRange(41, 42)], file.Ranges);
        Assert.False(file.WholeFile);
    }

    [Fact]
    public void ParseUnifiedDiff_TreatsDeletionOnlyHunkAsTheLineItCollapsedOnto()
    {
        // "+12,0" — nothing was added, so the change is visible at line 12 of the new file.
        const string diff = """
            diff --git a/src/Widget.cs b/src/Widget.cs
            --- a/src/Widget.cs
            +++ b/src/Widget.cs
            @@ -12,4 +12,0 @@
            -    was here
            """;

        var file = Assert.Single(GitChangeService.ParseUnifiedDiff(diff, @"C:\repo"));

        Assert.Equal([new LineRange(12, 12)], file.Ranges);
    }

    [Fact]
    public void ParseUnifiedDiff_DropsDeletedFilesAndKeepsBinaryOnesWhole()
    {
        const string diff = """
            diff --git a/src/Gone.cs b/src/Gone.cs
            deleted file mode 100644
            --- a/src/Gone.cs
            +++ /dev/null
            @@ -1,5 +0,0 @@
            -everything
            diff --git a/assets/logo.png b/assets/logo.png
            --- a/assets/logo.png
            +++ b/assets/logo.png
            Binary files a/assets/logo.png and b/assets/logo.png differ
            """;

        var files = GitChangeService.ParseUnifiedDiff(diff, @"C:\repo");

        var file = Assert.Single(files);
        Assert.EndsWith("logo.png", file.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(file.WholeFile);
    }

    [Fact]
    public void ChangedFile_WithNoRangesTouchesEveryLine()
    {
        var whole = new ChangedFile(@"C:\repo\New.cs", []);

        Assert.True(whole.WholeFile);
        Assert.True(whole.Touches(1));
        Assert.True(whole.Touches(9999));
    }

    // ---- The map ------------------------------------------------------------------------

    [Fact]
    public void CoveredFile_CollapsesLinesIntoRanges()
    {
        var file = CoveredFile.FromLines(@"C:\repo\Widget.cs", null, [3, 1, 2, 7, 8, 20]);

        Assert.Equal([1, 3, 7, 8, 20, 20], file.Ranges);
        Assert.Equal(6, file.LineCount);
        Assert.True(file.Covers(2));
        Assert.False(file.Covers(5));
        Assert.True(file.IntersectsAny([new LineRange(4, 7)]));
        Assert.False(file.IntersectsAny([new LineRange(9, 19)]));
    }

    [Fact]
    public void EntriesCovering_MatchesLinesAndFallsBackToTheWholeFile()
    {
        string path = @"C:\repo\Widget.cs";
        var map = new TestCoverageMap("sln", DateTime.UtcNow, [
            Entry("Tests.AlphaTests", ["Tests.AlphaTests.One"], path, [10, 11, 12]),
            Entry("Tests.BetaTests", ["Tests.BetaTests.Two"], path, [80, 81]),
        ]);

        var atLine11 = map.EntriesCovering(path, 11);
        Assert.Equal("Tests.AlphaTests", Assert.Single(atLine11).ClassFullName);

        Assert.Empty(map.EntriesCovering(path, 50));

        // No ranges asks "who touches this file at all", which is what a file whose lines have
        // moved since the map was built can honestly be asked.
        Assert.Equal(2, map.EntriesCovering(path, []).Count);
    }

    [Fact]
    public void IsFileStale_IsTrueOnceTheFileNoLongerHashesToWhatWasRecorded()
    {
        string directory = NewTempDirectory();
        string path = Path.Combine(directory, "Widget.cs");
        File.WriteAllText(path, "class Widget { }\n");

        var map = new TestCoverageMap("sln", DateTime.UtcNow, [
            Entry("Tests.AlphaTests", ["Tests.AlphaTests.One"], path, [1], CoverageMapHash.OfFile(path)),
        ]);

        Assert.False(map.IsFileStale(path));

        File.WriteAllText(path, "class Widget { public int X; }\n");
        Assert.True(map.IsFileStale(path));

        // A file the map never saw is stale by definition: there is nothing to trust about it.
        Assert.True(map.IsFileStale(Path.Combine(directory, "Other.cs")));
    }

    [Fact]
    public void Hash_IgnoresLineEndings()
    {
        string directory = NewTempDirectory();
        string crlf = Path.Combine(directory, "Crlf.cs");
        string lf = Path.Combine(directory, "Lf.cs");
        File.WriteAllText(crlf, "one\r\ntwo\r\n");
        File.WriteAllText(lf, "one\ntwo\n");

        Assert.Equal(CoverageMapHash.OfFile(lf), CoverageMapHash.OfFile(crlf));
    }

    [Fact]
    public void Store_RoundTripsThroughDisk()
    {
        string solution = Path.Combine(NewTempDirectory(), "Round.sln");
        File.WriteAllText(solution, "");

        var map = new TestCoverageMap(solution, DateTime.UtcNow, [
            Entry("Tests.AlphaTests", ["Tests.AlphaTests.One", "Tests.AlphaTests.Two"],
                @"C:\repo\Widget.cs", [4, 5, 6]),
        ]);

        try
        {
            TestCoverageMapStore.Save(solution, map);
            TestCoverageMapStore.ResetCache();

            var loaded = TestCoverageMapStore.Load(solution);

            Assert.Equal(2, loaded.TestCount);
            var entry = Assert.Single(loaded.Entries);
            Assert.Equal("Tests.AlphaTests", entry.ClassFullName);
            Assert.Equal([4, 6], Assert.Single(entry.Files).Ranges);
            Assert.Single(loaded.CoveredFiles());
        }
        finally
        {
            TestCoverageMapStore.Clear(solution);
        }
    }

    // ---- The lens -----------------------------------------------------------------------

    [Fact]
    public void CountTests_CountsTestsRatherThanEntries()
    {
        string path = @"C:\repo\Widget.cs";
        var rows = new TestCoverageMap("sln", DateTime.UtcNow, [
            Entry("Tests.AlphaTests", ["A.One", "A.Two"], path, [10, 11]),
            Entry("Tests.BetaTests", ["B.One"], path, [11, 12]),
            Entry("Tests.GammaTests", ["G.One"], path, [90]),
        ]).EntriesForFile(path);

        // Two classes overlap the member, and between them they hold three tests.
        Assert.Equal(3, TestCoverageLenses.CountTests(rows, new LineRange(9, 12)));
        Assert.Equal(1, TestCoverageLenses.CountTests(rows, new LineRange(88, 92)));
        Assert.Equal(0, TestCoverageLenses.CountTests(rows, new LineRange(40, 50)));
    }

    [Fact]
    public void MemberLineRange_FindsTheMemberAPositionSitsIn()
    {
        const string source = """
            class Widget
            {
                public int Small() => 1;

                public int Large()
                {
                    return 2;
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var text = SourceText.From(source);

        // On "Large" (0-based line 4), which runs through its closing brace on line 7.
        var range = TestCoverageLenses.MemberLineRange(root, text, 4, 16);
        Assert.Equal(new LineRange(5, 8), range);

        // On the expression-bodied member above it.
        Assert.Equal(new LineRange(3, 3), TestCoverageLenses.MemberLineRange(root, text, 2, 16));

        // Off the end of the file is not a position at all.
        Assert.Null(TestCoverageLenses.MemberLineRange(root, text, 99, 0));
    }

    // ---- The coverage snapshot behind the coverage view -----------------------------------

    [Fact]
    public void Snapshot_FlattensMethodsAndSkipsCompilerGeneratedOnes()
    {
        string solution = Path.Combine(NewTempDirectory(), "Snap.sln");
        File.WriteAllText(solution, "");

        var data = new CoverageData();
        var file = new FileCoverage { FilePath = @"C:\repo\Widget.cs" };
        file.Classes.Add(new ClassCoverage
        {
            Name = "Widget",
            FullName = "Sample.Widgets.Widget",
            FilePath = file.FilePath,
            Methods =
            [
                Method("Spin", covered: 3, total: 4, line: 10),
                Method("<Spin>b__0", covered: 1, total: 1, line: 14),
                Method("NeverMeasured", covered: 0, total: 0, line: 20),
            ],
        });
        data.Files[file.FilePath] = file;

        try
        {
            CoverageSnapshotStore.Record(solution, data);
            var snapshot = CoverageSnapshotStore.Load(solution);

            var method = Assert.Single(snapshot.Methods);
            Assert.Equal("Spin", method.MethodName);
            Assert.Equal("Sample.Widgets", method.Namespace);
            Assert.Equal(3, method.CoveredStatements);
            Assert.Equal(4, method.TotalStatements);
            Assert.Equal(10, method.Line);
        }
        finally
        {
            CoverageSnapshotStore.Clear(solution);
        }
    }

    [Fact]
    public void Snapshot_MergeKeepsTheBetterMeasurementOfEachMethod()
    {
        string solution = Path.Combine(NewTempDirectory(), "Merge.sln");
        File.WriteAllText(solution, "");

        try
        {
            // One test class's pass reaches half of Spin and none of Turn; another reaches all
            // of Turn. Together they are the aggregate the whole suite would have produced.
            CoverageSnapshotStore.Merge(solution, Data(Method("Spin", 2, 4, 10), Method("Turn", 0, 2, 20)));
            CoverageSnapshotStore.Merge(solution, Data(Method("Spin", 1, 4, 10), Method("Turn", 2, 2, 20)));

            var snapshot = CoverageSnapshotStore.Load(solution);

            Assert.Equal(2, snapshot.Methods.Count);
            Assert.Equal(2, snapshot.Methods.Single(m => m.MethodName == "Spin").CoveredStatements);
            Assert.Equal(2, snapshot.Methods.Single(m => m.MethodName == "Turn").CoveredStatements);
            Assert.Equal(4, snapshot.CoveredStatements);
            Assert.Equal(6, snapshot.TotalStatements);
        }
        finally
        {
            CoverageSnapshotStore.Clear(solution);
        }
    }

    // ---- The two together, over a real repository ----------------------------------------

    [Fact]
    public async Task SelectAsync_PicksOnlyTheTestsCoveringTheChangedLines()
    {
        if (!GitIsAvailable())
            return;

        string repository = NewTempDirectory();
        InitRepository(repository);

        string solution = Path.Combine(repository, "Sample.sln");
        File.WriteAllText(solution, "");

        string widget = Path.Combine(repository, "Widget.cs");
        File.WriteAllLines(widget, Enumerable.Range(1, 40).Select(i => $"// line {i}"));

        string untouched = Path.Combine(repository, "Untouched.cs");
        File.WriteAllLines(untouched, Enumerable.Range(1, 10).Select(i => $"// line {i}"));

        Git(repository, "add -A");
        Git(repository, "commit -m initial");

        var map = new TestCoverageMap(solution, DateTime.UtcNow, [
            Entry("Tests.TopTests", ["Tests.TopTests.One"], widget, [1, 2, 3],
                CoverageMapHash.OfFile(widget)),
            Entry("Tests.BottomTests", ["Tests.BottomTests.Two"], widget, [30, 31],
                CoverageMapHash.OfFile(widget)),
            Entry("Tests.ElsewhereTests", ["Tests.ElsewhereTests.Three"], untouched, [1],
                CoverageMapHash.OfFile(untouched)),
        ]);

        try
        {
            TestCoverageMapStore.Save(solution, map);
            TestCoverageMapStore.ResetCache();

            // Edit line 31 only — in place, so the file's other lines keep their numbers. The
            // hash still moves, which is exactly the case the file-level fallback is for.
            var lines = File.ReadAllLines(widget);
            lines[30] = "// line 31 edited";
            File.WriteAllLines(widget, lines);

            var selection = await TestImpactService.SelectAsync(
                repository, GitChangeScope.Uncommitted, useReferenceWalk: false);

            Assert.Null(selection.Error);

            // The edited file's recorded lines can no longer be trusted, so both of its entries
            // come back — but nothing from the file nobody touched.
            Assert.Equal(
                ["Tests.BottomTests.Two", "Tests.TopTests.One"],
                selection.Tests.Select(t => t.FullyQualifiedName).Order());
            Assert.All(selection.Tests, t => Assert.Equal(ImpactReason.CoveredChangedFile, t.Reason));
            Assert.DoesNotContain(selection.Tests, t => t.ClassFullName == "Tests.ElsewhereTests");
        }
        finally
        {
            TestCoverageMapStore.Clear(solution);
        }
    }

    [Fact]
    public async Task SelectAsync_MatchesLinesWhenTheFileStillHashesToWhatWasMapped()
    {
        if (!GitIsAvailable())
            return;

        string repository = NewTempDirectory();
        InitRepository(repository);

        string solution = Path.Combine(repository, "Sample.sln");
        File.WriteAllText(solution, "");

        string widget = Path.Combine(repository, "Widget.cs");
        File.WriteAllLines(widget, Enumerable.Range(1, 40).Select(i => $"// line {i}"));
        Git(repository, "add -A");
        Git(repository, "commit -m initial");

        // Edit first, then record the map against the edited content: the working tree now
        // differs from HEAD (so the diff has something to say) while hashing to what was mapped.
        var lines = File.ReadAllLines(widget);
        lines[1] = "// line 2 edited";
        File.WriteAllLines(widget, lines);

        var map = new TestCoverageMap(solution, DateTime.UtcNow, [
            Entry("Tests.TopTests", ["Tests.TopTests.One"], widget, [1, 2, 3],
                CoverageMapHash.OfFile(widget)),
            Entry("Tests.BottomTests", ["Tests.BottomTests.Two"], widget, [30, 31],
                CoverageMapHash.OfFile(widget)),
        ]);

        try
        {
            TestCoverageMapStore.Save(solution, map);
            TestCoverageMapStore.ResetCache();

            var selection = await TestImpactService.SelectAsync(
                repository, GitChangeScope.Uncommitted, useReferenceWalk: false);

            Assert.Null(selection.Error);

            var test = Assert.Single(selection.Tests);
            Assert.Equal("Tests.TopTests.One", test.FullyQualifiedName);
            Assert.Equal(ImpactReason.CoveredChangedLines, test.Reason);
        }
        finally
        {
            TestCoverageMapStore.Clear(solution);
        }
    }

    [Fact]
    public async Task SelectAsync_ReportsChangedFilesNothingCovers()
    {
        if (!GitIsAvailable())
            return;

        string repository = NewTempDirectory();
        InitRepository(repository);
        File.WriteAllText(Path.Combine(repository, "Sample.sln"), "");
        File.WriteAllText(Path.Combine(repository, "README.md"), "# sample\n");
        Git(repository, "add -A");
        Git(repository, "commit -m initial");

        // Untracked, so it never appears in a diff — and it is exactly the kind of change a
        // selection must not silently ignore.
        string added = Path.Combine(repository, "BrandNew.cs");
        File.WriteAllText(added, "class BrandNew { }\n");

        var selection = await TestImpactService.SelectAsync(
            repository, GitChangeScope.Uncommitted, useReferenceWalk: false);

        Assert.Null(selection.Error);
        Assert.True(selection.MapWasEmpty);
        Assert.Empty(selection.Tests);
        Assert.Contains(added, selection.UncoveredFiles);
    }

    [Fact]
    public async Task SelectAsync_SaysSoWhenThereIsNoRepository()
    {
        string directory = NewTempDirectory();

        var selection = await TestImpactService.SelectAsync(directory);

        Assert.NotNull(selection.Error);
        Assert.Contains("not inside a git repository", selection.Error);
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static CoverageMapEntry Entry(
        string className, string[] tests, string filePath, int[] lines, string? fileHash = null) =>
        new(className, "TestProject.csproj", tests,
            [CoveredFile.FromLines(filePath, fileHash, lines)]);

    private static MethodCoverage Method(string name, int covered, int total, int line) =>
        new()
        {
            Name = name,
            FullName = $"Sample.Widgets.Widget.{name}",
            FilePath = @"C:\repo\Widget.cs",
            CoveredLines = covered,
            TotalLines = total,
            Lines = total == 0 ? [] : [new LineCoverage { LineNumber = line, Hits = covered }],
        };

    private static CoverageData Data(params MethodCoverage[] methods)
    {
        var data = new CoverageData();
        var file = new FileCoverage { FilePath = @"C:\repo\Widget.cs" };
        file.Classes.Add(new ClassCoverage
        {
            Name = "Widget",
            FullName = "Sample.Widgets.Widget",
            FilePath = file.FilePath,
            Methods = [.. methods],
        });
        data.Files[file.FilePath] = file;
        return data;
    }

    private static string NewTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "roslyn-sense-impact-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void InitRepository(string directory)
    {
        Git(directory, "init");
        // Committing needs an identity, and the machine running the tests may have none.
        Git(directory, "config user.email tests@example.invalid");
        Git(directory, "config user.name Tests");
        Git(directory, "config commit.gpgsign false");
    }

    private static void Git(string workingDirectory, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        process.WaitForExit();
    }

    private static bool GitIsAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            })!;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
