using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Decompiled source used as a module's symbols, which is what lets the engine locate, step and
/// bind inside a dependency that shipped without a PDB rather than give up and let the host patch
/// a file name into the answer afterwards.
/// </summary>
public class DecompiledSymbolTests
{
    private const string Module = @"C:\packages\Thing\lib\Thing.dll";
    private const string File = @"C:\cache\decompiled\Thing.Widget.cs";
    private const int Token = 0x06000123;

    private static DecompiledSymbolSet SetWith(params DecompiledPoint[] points)
    {
        var set = new DecompiledSymbolSet();
        set.Add(new DecompiledSymbolMap
        {
            FilePath = File,
            Methods = { [Token] = points },
        });
        return set;
    }

    private static DecompiledPoint At(int offset, int line) =>
        new(offset, line, 9, line, 30);

    [Fact]
    public void AStatementRunsToTheNextOneRatherThanToItsOwnFirstByte()
    {
        // The reason this exists. A step ranges over the statement it is on; a range of one IL
        // byte is a single instruction, which lands back on the line it started from — which is
        // what stepping in decompiled code did before it had symbols.
        var set = SetWith(At(0, 10), At(7, 11), At(20, 12));

        var found = set.PointAt(Token, 7);

        Assert.NotNull(found);
        Assert.Equal(11, found!.Value.Point.Line);
        Assert.Equal(20, found.Value.NextOffset);
    }

    [Fact]
    public void TheLastStatementRunsToTheEndOfTheMethod()
    {
        // No next point to stop at, so the caller is left to bound it against the IL size, exactly
        // as it does for a PDB.
        var set = SetWith(At(0, 10), At(7, 11));

        Assert.Equal(int.MaxValue, set.PointAt(Token, 8)!.Value.NextOffset);
    }

    [Fact]
    public void PointsSharingAnOffsetDoNotProduceAnEmptyRange()
    {
        // Two points at one offset is ordinary in decompiled output — a compiler-generated
        // construct written back as several statements. Pairing them naively gives a start equal
        // to its end, which is the single-instruction step again by another route.
        var set = SetWith(At(0, 10), At(12, 11), At(12, 12), At(30, 13));

        var found = set.PointAt(Token, 5);

        Assert.Equal(12, found!.Value.NextOffset);
        Assert.Equal(30, set.PointAt(Token, 12)!.Value.NextOffset);
    }

    [Fact]
    public void PointsThatArriveOutOfOrderAreStillReadInOrder()
    {
        // Nothing promises the decompiler emits them ordered, and reading them out of order does
        // not fail — it silently pairs each point with the wrong neighbour.
        var set = SetWith(At(20, 12), At(0, 10), At(7, 11));

        Assert.Equal(11, set.PointAt(Token, 10)!.Value.Point.Line);
        Assert.Equal(20, set.PointAt(Token, 10)!.Value.NextOffset);
    }

    [Fact]
    public void AnOffsetBeforeTheFirstStatementIsNotInAnyOfThem()
    {
        var set = SetWith(At(4, 10));

        Assert.Null(set.PointAt(Token, 0));
    }

    [Fact]
    public void AMethodThatHasNotBeenDecompiledAnswersNothing()
    {
        // Absence means "not decompiled yet", not "no code here": a module holds far more types
        // than a session ever stops in, and only the ones visited are ever built.
        var set = SetWith(At(0, 10));

        Assert.Null(set.PointAt(0x06000999, 0));
        Assert.Equal(string.Empty, set.FileOf(0x06000999));
    }

    [Fact]
    public void DecompilingATypeASecondTimeReplacesItRatherThanDoublingIt()
    {
        var set = SetWith(At(0, 10), At(7, 11));
        set.Add(new DecompiledSymbolMap
        {
            FilePath = File,
            Methods = { [Token] = [At(0, 40)] },
        });

        Assert.Equal(40, set.PointAt(Token, 3)!.Value.Point.Line);
        Assert.Single(set.PointsIn(File));
    }

    [Fact]
    public void ABreakpointOnALineWithCodeBindsToThatLine()
    {
        var set = SetWith(At(0, 10), At(7, 11), At(20, 12));

        var found = set.BestPoint(Token, line: 11, column: 0);

        Assert.Equal(7, found!.Value.Point.Offset);
    }

    [Fact]
    public void ABreakpointOnALineWithNoCodeSlidesDownToTheNextThatHasSome()
    {
        // A brace, a blank line, a declaration the decompiler folded away. Refusing would be
        // technically true and useless — real source breakpoints slide, and so should these.
        var set = SetWith(At(0, 10), At(20, 14));

        Assert.Equal(20, set.BestPoint(Token, line: 12, column: 0)!.Value.Point.Offset);
    }

    [Fact]
    public void ABreakpointBelowEveryStatementBindsNowhere()
    {
        var set = SetWith(At(0, 10), At(7, 11));

        Assert.Null(set.BestPoint(Token, line: 90, column: 0));
    }

    [Fact]
    public void OnALineWithSeveralStatementsTheColumnChoosesBetweenThem()
    {
        var set = new DecompiledSymbolSet();
        set.Add(new DecompiledSymbolMap
        {
            FilePath = File,
            Methods =
            {
                [Token] =
                [
                    new DecompiledPoint(0, 10, 9, 10, 20),
                    new DecompiledPoint(6, 10, 25, 10, 40),
                ],
            },
        });

        Assert.Equal(6, set.BestPoint(Token, line: 10, column: 25)!.Value.Point.Offset);
        Assert.Equal(0, set.BestPoint(Token, line: 10, column: 1)!.Value.Point.Offset);
    }

    [Fact]
    public void AModuleAccumulatesTheTypesTheSessionVisits()
    {
        var set = SetWith(At(0, 10));
        set.Add(new DecompiledSymbolMap
        {
            FilePath = @"C:\cache\decompiled\Thing.Gadget.cs",
            Methods = { [0x06000200] = [At(0, 5)] },
        });

        Assert.Equal(2, set.Files.Count());
        Assert.True(set.Describes(@"C:\cache\decompiled\Thing.Gadget.cs"));
        Assert.Equal(Token, Assert.Single(set.MethodsIn(File)));
    }

    [Fact]
    public void AMethodSaysWhichFileItWasDecompiledInto()
    {
        // What keeps a line lookup honest once a module has more than one decompiled type in it.
        // A line number matched against a method that lives in another file would answer for the
        // file that was asked about with an offset from the file that was not — and the caller
        // that does this is Set Next Statement, which moves the instruction pointer.
        var set = SetWith(At(0, 10));
        set.Add(new DecompiledSymbolMap
        {
            FilePath = @"C:\cache\decompiled\Thing.Gadget.cs",
            Methods = { [0x06000200] = [At(0, 10)] },
        });

        Assert.Equal(File, set.FileOf(Token));
        Assert.Equal(@"C:\cache\decompiled\Thing.Gadget.cs", set.FileOf(0x06000200));
    }

    [Fact]
    public void AFileIsRecognisedWhateverItsCase()
    {
        // The path travels from the writer to the engine and back through the editor, and Windows
        // does not promise it keeps its case on the way.
        var set = SetWith(At(0, 10));

        Assert.True(set.Describes(File.ToUpperInvariant()));
        Assert.Single(set.PointsIn(File.ToLowerInvariant()));
    }

    [Fact]
    public void SymbolsSurviveTheTripToAWorkerProcess()
    {
        // The engine may be a separate 32-bit process, so the map is only useful if it arrives
        // intact — including the ends, which are what make a location a statement and not a point.
        var original = new DecompiledSymbolMap
        {
            FilePath = File,
            Methods = { [Token] = [new DecompiledPoint(7, 11, 9, 12, 40)] },
        };

        var parsed = DecompiledSymbolMap.Parse(original.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(File, parsed!.FilePath);
        var point = Assert.Single(parsed.Methods[Token]);
        Assert.Equal(new DecompiledPoint(7, 11, 9, 12, 40), point);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    public void SymbolsThatDoNotArriveAreNotAFailure(string? json)
    {
        // This is the fallback for a module that had no symbols at all, so the worst a bad map can
        // cost is the fallback itself.
        Assert.Null(DecompiledSymbolMap.Parse(json));
    }

    [Fact]
    public void AMapWithNoMethodsIsNoMap()
    {
        Assert.Null(DecompiledSymbolMap.Parse(
            new DecompiledSymbolMap { FilePath = File }.ToJson()));

        var set = new DecompiledSymbolSet();
        set.Add(new DecompiledSymbolMap { FilePath = File });
        Assert.True(set.IsEmpty);
        Assert.Empty(set.Files);
    }
}
