using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Services.ExternalSource;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Picking a .NET Framework snapshot, guessing which file declares a type, and refusing a file
/// that turns out not to.
/// </summary>
public class ReferenceSourceTests
{
    [Fact]
    public void WhenTheFrameworkVersionShippedASnapshotThenItsCommitIsReturned()
    {
        Assert.Equal(
            "3b1eaf5203992df69de44c783a3eda37d3d4cd10",
            ReferenceSourceCommitMap.CommitFor("net472"));
    }

    /// <summary>
    /// 4.8.1 shipped no reference sources of its own, so it reads 4.8's — the same code.
    /// </summary>
    [Fact]
    public void WhenAVersionReusedTheEarlierSnapshotThenBothMapToOneCommit()
    {
        Assert.Equal(
            ReferenceSourceCommitMap.CommitFor("net48"),
            ReferenceSourceCommitMap.CommitFor("net481"));
    }

    /// <summary>
    /// The nearest snapshot is not an acceptable substitute: it would present another release's
    /// code as this one's. Nothing is the honest answer, and it means decompilation.
    /// </summary>
    [Theory]
    [InlineData("net471")]
    [InlineData("net452")]
    [InlineData("net8.0")]
    [InlineData(null)]
    public void WhenNoSnapshotWasPublishedThenNoCommitIsGuessed(string? tfm) =>
        Assert.Null(ReferenceSourceCommitMap.CommitFor(tfm));

    [Fact]
    public void WhenAFrameworkVersionIsWrittenAsADirectoryThenItFoldsToAMoniker() =>
        Assert.Equal("net472", ReferenceSourceCommitMap.Moniker("v4.7.2"));

    [Fact]
    public void WhenAFileIsNamedAfterTheTypeThenItOutranksTheRest()
    {
        string[] paths =
        [
            "System/net/System/Net/WebRequest.cs",
            "System/net/System/Net/_WebClientHelpers.cs",
            "System/net/System/Net/WebClient.cs",
        ];

        Assert.Equal(
            "System/net/System/Net/WebClient.cs",
            ReferenceSourceService.Rank(paths, "WebClient", "System.Net").First());
    }

    /// <summary>
    /// A good deal of <c>System.dll</c> lives in underscore-prefixed implementation files, so
    /// <c>_WebClient.cs</c> has to be reachable even though nothing is named exactly that.
    /// </summary>
    [Fact]
    public void WhenOnlyAnUnderscoredFileMatchesThenItIsStillACandidate()
    {
        string[] paths =
        [
            "System/net/System/Net/_ListenerAsyncResult.cs",
            "System/net/System/Net/_WebClient.cs",
        ];

        Assert.Equal(
            "System/net/System/Net/_WebClient.cs",
            ReferenceSourceService.Rank(paths, "WebClient", "System.Net").First());
    }

    /// <summary>
    /// Two files of the same name in different products: the namespace decides between them.
    /// </summary>
    [Fact]
    public void WhenTwoFilesShareANameThenTheOneUnderTheNamespaceWins()
    {
        string[] paths =
        [
            "System/sys/system/threading/Timer.cs",
            "System/net/System/Net/Timer.cs",
        ];

        Assert.Equal(
            "System/net/System/Net/Timer.cs",
            ReferenceSourceService.Rank(paths, "Timer", "System.Net").First());
    }

    [Fact]
    public void WhenNoFileNameResemblesTheTypeThenThereAreNoCandidates() =>
        Assert.Empty(ReferenceSourceService.Rank(
            ["System/net/System/Net/WebRequest.cs"], "WebClient", "System.Net"));

    /// <summary>
    /// The reference source carries no checksum, so a wrong download has to be caught by reading
    /// it. GitHub's 404 body is HTML and does not parse as C#.
    /// </summary>
    [Fact]
    public void WhenTheDownloadIsNotSourceThenItIsRejected() =>
        Assert.Null(Verify("<html><body>404: Not Found</body></html>", "WebClient", 0, "System.Net"));

    [Fact]
    public void WhenTheFileDeclaresTheNameInAnotherNamespaceThenItIsRejected() =>
        Assert.Null(Verify(
            "namespace System.Threading { public class Timer { } }", "Timer", 0, "System.Net"));

    /// <summary>
    /// A container can declare both <c>Result</c> and <c>Result&lt;T&gt;</c>; landing on the wrong
    /// one is landing on different code.
    /// </summary>
    [Fact]
    public void WhenOnlyTheArityDiffersThenItIsRejected() =>
        Assert.Null(Verify(
            "namespace System.Net { public class Result { } }", "Result", 1, "System.Net"));

    [Fact]
    public void WhenTheFileDeclaresTheTypeThenTheDeclarationIsWhereItLands()
    {
        var accepted = Verify(
            """
            namespace System.Net
            {
                public class WebClient
                {
                }
            }
            """,
            "WebClient",
            0,
            "System.Net");

        Assert.NotNull(accepted);
        Assert.Equal(2, accepted!.Value.Positions[0].Line);
        Assert.False(accepted.Value.MemberFound);
    }

    /// <summary>
    /// A partial type declares its members across several files. The one holding the member is the
    /// answer; a file with only the type declaration is a worse answer, and says so, so the search
    /// can carry on to the next candidate.
    /// </summary>
    [Fact]
    public void WhenTheTypeIsHereButTheMemberIsNotThenTheMatchIsReportedAsPartial()
    {
        var method = MethodSymbol(
            """
            namespace System.Net
            {
                public class WebClient
                {
                    public void DownloadFile() { }
                }
            }
            """,
            "System.Net.WebClient",
            "DownloadFile");

        var accepted = ReferenceSourceService.Verify(
            "namespace System.Net { public partial class WebClient { public void Other() { } } }",
            method,
            "WebClient",
            0,
            "System.Net",
            CancellationToken.None);

        Assert.NotNull(accepted);
        Assert.False(accepted!.Value.MemberFound);
    }

    [Fact]
    public void WhenTheFileDeclaresTheMemberThenThatIsWhereItLands()
    {
        const string Source =
            """
            namespace System.Net
            {
                public partial class WebClient
                {
                    public void DownloadFile() { }
                }
            }
            """;

        var method = MethodSymbol(Source, "System.Net.WebClient", "DownloadFile");

        var accepted = ReferenceSourceService.Verify(
            Source, method, "WebClient", 0, "System.Net", CancellationToken.None);

        Assert.NotNull(accepted);
        Assert.True(accepted!.Value.MemberFound);
        Assert.Equal(5, accepted.Value.Positions[0].Line + 1);
    }

    private static (IReadOnlyList<Microsoft.CodeAnalysis.Text.LinePosition> Positions, bool MemberFound)?
        Verify(string text, string simpleName, int arity, string @namespace) =>
        ReferenceSourceService.Verify(
            text, symbol: null, simpleName, arity, @namespace, CancellationToken.None);

    private static IMethodSymbol MethodSymbol(string source, string typeName, string methodName)
    {
        var compilation = CSharpCompilation.Create(
            "ReferenceSourceTests",
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var type = compilation.GetTypeByMetadataName(typeName);
        Assert.NotNull(type);

        return type!.GetMembers(methodName).OfType<IMethodSymbol>().First();
    }
}
