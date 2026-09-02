using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Services;
using RoslynMCP.Services.Testing;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The Changed Members view's mapping: which member declarations own the lines a diff touched,
/// and where inside each member a click should land. Everything here is syntax-only, so the
/// tests parse source directly and hand the service line ranges as git would report them.
/// </summary>
public class ChangedMemberServiceTests
{
    private const string Source = """
        using System;

        namespace App.Orders
        {
            public class OrderService
            {
                private readonly int _limit = 10;

                public string Name { get; set; } = "";

                public OrderService(int limit)
                {
                    _limit = limit;
                }

                public int Total(int count)
                {
                    return count * _limit;
                }
            }
        }
        """;

    private static IReadOnlyList<ChangedMember> Collect(params LineRange[] ranges)
    {
        var root = CSharpSyntaxTree.ParseText(Source).GetRoot();
        return ChangedMemberService.CollectMembers(
            root, new ChangedFile(@"C:\repo\OrderService.cs", ranges));
    }

    [Fact]
    public void CollectMembers_FindsTheMethodOwningAChangedBodyLine()
    {
        // Line 18 is inside Total's body.
        var member = Assert.Single(Collect(new LineRange(18, 18)));

        Assert.Equal("Total", member.Name);
        Assert.Equal("method", member.Kind);
        Assert.Equal("OrderService", member.ContainerType);
        Assert.Equal("App.Orders", member.Namespace);
    }

    [Fact]
    public void CollectMembers_LandsOnTheFirstChangedLineInsideTheMember()
    {
        // The range runs past Total's last line (19) into the class brace; the count clips.
        var member = Assert.Single(Collect(new LineRange(18, 20)));

        Assert.Equal(18, member.FirstChangedLine);
        Assert.Equal(2, member.ChangedLineCount);
    }

    [Fact]
    public void CollectMembers_ClipsARangeSpanningTwoMembersToEach()
    {
        // Lines 13-19 run from the constructor into Total.
        var members = Collect(new LineRange(13, 19));

        Assert.Equal(["OrderService", "Total"], members.Select(m => m.Name));
        Assert.Equal("constructor", members[0].Kind);
        Assert.Equal(13, members[0].FirstChangedLine);
        Assert.Equal(16, members[1].FirstChangedLine);
    }

    [Fact]
    public void CollectMembers_ReportsFieldsAndPropertiesByName()
    {
        var members = Collect(new LineRange(7, 9));

        Assert.Equal(["_limit", "Name"], members.Select(m => m.Name));
        Assert.Equal(["field", "property"], members.Select(m => m.Kind));
    }

    [Fact]
    public void CollectMembers_ListsEachChangedBlockClippedAndPreviewed()
    {
        // Two separate edits inside Total: its signature (16) and its body (18), with the body
        // range running past the member's last line so the block has to clip.
        var member = Assert.Single(Collect(new LineRange(16, 16), new LineRange(18, 20)));

        Assert.Equal(2, member.Blocks.Count);
        Assert.Equal((16, 16), (member.Blocks[0].StartLine, member.Blocks[0].EndLine));
        Assert.Equal((18, 19), (member.Blocks[1].StartLine, member.Blocks[1].EndLine));
        Assert.Equal("public int Total(int count)", member.Blocks[0].Preview);
        Assert.Equal("return count * _limit;", member.Blocks[1].Preview);
    }

    [Fact]
    public void CollectMembers_WholeFileHasNoBlocks()
    {
        Assert.All(Collect(), m => Assert.Empty(m.Blocks));
    }

    [Fact]
    public void CollectMembers_IgnoresChangesOutsideAnyMember()
    {
        // Line 1 is a using directive; line 5 is the class header.
        Assert.Empty(Collect(new LineRange(1, 1), new LineRange(5, 5)));
    }

    [Fact]
    public void CollectMembers_WholeFileListsEveryMemberAtItsOwnName()
    {
        var members = Collect();

        Assert.Equal(4, members.Count);
        var total = members.Single(m => m.Name == "Total");
        // The click lands on the method's name line, not the first line of the file.
        Assert.Equal(16, total.FirstChangedLine);
    }

    [Fact]
    public void CollectMembers_FileScopedNamespaceIsReported()
    {
        var root = CSharpSyntaxTree.ParseText("""
            namespace App.Billing;

            public class Invoice
            {
                public decimal Amount => 0m;
            }
            """).GetRoot();

        var member = Assert.Single(ChangedMemberService.CollectMembers(
            root, new ChangedFile(@"C:\repo\Invoice.cs", [new LineRange(5, 5)])));

        Assert.Equal("App.Billing", member.Namespace);
        Assert.Equal("Invoice", member.ContainerType);
    }

    [Fact]
    public void CollectMembers_NestedTypesJoinTheContainerName()
    {
        var root = CSharpSyntaxTree.ParseText("""
            class Outer
            {
                class Inner
                {
                    void Run() { }
                }
            }
            """).GetRoot();

        var member = Assert.Single(ChangedMemberService.CollectMembers(
            root, new ChangedFile(@"C:\repo\Outer.cs", [new LineRange(5, 5)])));

        Assert.Equal("Outer.Inner", member.ContainerType);
        Assert.Equal("", member.Namespace);
    }

    /// <summary>Two changed runs in Total: lines 17 and 18 of <see cref="Source"/>.</summary>
    private static IReadOnlyList<ChangedMember> CollectWithUnstaged(
        LineRange[] ranges, LineRange[] unstaged) =>
        ChangedMemberService.CollectMembers(
            CSharpSyntaxTree.ParseText(Source).GetRoot(),
            new ChangedFile(@"C:\repo\OrderService.cs", ranges, unstaged));

    [Fact]
    public void CollectMembers_WithoutAnUnstagedAnswer_NothingCountsAsStaged()
    {
        var member = Assert.Single(Collect(new LineRange(18, 18)));

        Assert.False(member.Staged);
        Assert.False(Assert.Single(member.Blocks).Staged);
    }

    [Fact]
    public void CollectMembers_AMemberWithNothingLeftDirtyIsStaged()
    {
        var member = Assert.Single(CollectWithUnstaged([new LineRange(18, 18)], []));

        Assert.True(member.Staged);
        Assert.True(Assert.Single(member.Blocks).Staged);
    }

    [Fact]
    public void CollectMembers_AStillDirtyMemberIsNotStaged()
    {
        var member = Assert.Single(
            CollectWithUnstaged([new LineRange(18, 18)], [new LineRange(18, 18)]));

        Assert.False(member.Staged);
        Assert.False(Assert.Single(member.Blocks).Staged);
    }

    [Fact]
    public void CollectMembers_HalfStagedMemberKeepsTheStagedBlockApart()
    {
        // Two runs inside Total; only the second is still dirty.
        var member = Assert.Single(CollectWithUnstaged(
            [new LineRange(17, 17), new LineRange(18, 18)],
            [new LineRange(18, 18)]));

        Assert.False(member.Staged);
        Assert.Equal([true, false], member.Blocks.Select(b => b.Staged));
    }

    [Fact]
    public void CollectMembers_APartlyDirtyRunIsNotStaged()
    {
        // The run covers 17-18 and only 18 is dirty: no part of it is finished business.
        var block = Assert.Single(
            Assert.Single(CollectWithUnstaged([new LineRange(17, 18)], [new LineRange(18, 18)]))
                .Blocks);

        Assert.False(block.Staged);
    }

    // ---- Removed members: named from the diff base, since the new file no longer has them ----

    [Fact]
    public void CollectRemovedMembers_NamesTheDeletedProperty()
    {
        var oldRoot = CSharpSyntaxTree.ParseText("""
            class Widget
            {
                public int Count { get; set; }

                public int Kept() => 1;
            }
            """).GetRoot();
        var newRoot = CSharpSyntaxTree.ParseText("""
            class Widget
            {
                public int Kept() => 1;
            }
            """).GetRoot();

        // The property and its trailing blank line were old lines 3-4; the deletion is visible
        // at line 2 of the file as it is now.
        var file = new ChangedFile(@"C:\repo\Widget.cs", [new LineRange(2, 2)],
            RemovedRanges: [new RemovedRange(3, 4, 2)]);

        var member = Assert.Single(
            ChangedMemberService.CollectRemovedMembers(oldRoot, newRoot, file));

        Assert.Equal("Count", member.Name);
        Assert.Equal("property", member.Kind);
        Assert.Equal("Widget", member.ContainerType);
        Assert.True(member.Removed);
        Assert.Equal(2, member.FirstChangedLine);
        Assert.Empty(member.Blocks);
    }

    [Fact]
    public void CollectRemovedMembers_AMemberStillPresentIsNotRemoved()
    {
        // Total's body was replaced, not deleted: the old lines are gone, the member is not.
        var oldRoot = CSharpSyntaxTree.ParseText(Source).GetRoot();
        var newRoot = CSharpSyntaxTree.ParseText(Source.Replace("count * _limit", "count")).GetRoot();

        var file = new ChangedFile(@"C:\repo\OrderService.cs", [new LineRange(18, 18)],
            RemovedRanges: [new RemovedRange(18, 18, 18)]);

        Assert.Empty(ChangedMemberService.CollectRemovedMembers(oldRoot, newRoot, file));
    }

    [Fact]
    public void CollectRemovedMembers_ADeletedTypeIsOneRowForEverythingInIt()
    {
        var oldRoot = CSharpSyntaxTree.ParseText("""
            class Kept { }

            class Extra
            {
                public int Count { get; set; }

                void Run() { }
            }
            """).GetRoot();
        var newRoot = CSharpSyntaxTree.ParseText("class Kept { }").GetRoot();

        var file = new ChangedFile(@"C:\repo\Types.cs", [new LineRange(1, 1)],
            RemovedRanges: [new RemovedRange(2, 8, 1)]);

        var member = Assert.Single(
            ChangedMemberService.CollectRemovedMembers(oldRoot, newRoot, file));

        Assert.Equal("Extra", member.Name);
        Assert.Equal("class", member.Kind);
        Assert.True(member.Removed);
    }

    [Fact]
    public void CollectRemovedMembers_ADeletedFileKeepsBaseRevisionLines()
    {
        // The whole file is gone: there is no new-side line to land on, so rows keep the lines
        // they had in the base revision, the only version left to open.
        var oldRoot = CSharpSyntaxTree.ParseText("""
            namespace App;

            class Gone
            {
                void Run() { }
            }
            """).GetRoot();

        var file = new ChangedFile(@"C:\repo\Gone.cs", [],
            RemovedRanges: [new RemovedRange(1, 6, 1)], Deleted: true);

        var member = Assert.Single(
            ChangedMemberService.CollectRemovedMembers(oldRoot, null, file));

        Assert.Equal("Gone", member.Name);
        Assert.Equal("class", member.Kind);
        Assert.Equal("App", member.Namespace);
        Assert.Equal(3, member.FirstChangedLine);
    }

    [Fact]
    public void CollectRemovedMembers_ASingleDeletedFieldVariableIsNamedAlone()
    {
        var oldRoot = CSharpSyntaxTree.ParseText("""
            class C
            {
                int a, b;
            }
            """).GetRoot();
        var newRoot = CSharpSyntaxTree.ParseText("""
            class C
            {
                int b;
            }
            """).GetRoot();

        var file = new ChangedFile(@"C:\repo\C.cs", [new LineRange(3, 3)],
            RemovedRanges: [new RemovedRange(3, 3, 3)]);

        var member = Assert.Single(
            ChangedMemberService.CollectRemovedMembers(oldRoot, newRoot, file));

        Assert.Equal("a", member.Name);
        Assert.Equal("field", member.Kind);
    }
}
