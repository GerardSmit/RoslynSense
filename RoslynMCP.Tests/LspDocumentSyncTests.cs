using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public sealed class LspDocumentSyncTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "roslyn-sense-tests", Guid.NewGuid().ToString("N"), "Buffer.cs");
    private readonly List<LspServer> _servers = [];

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void BatchedRangesUseThePreviousChangesTextAndUtf16Columns(string newline)
    {
        var server = Open($"head{newline}A\U0001F600BC{newline}end");

        Change(server, 2,
            Edit(1, 0, 1, 0, "inserted" + newline),
            Edit(2, 1, 2, 3, "Z"),
            Edit(2, 2, 2, 4, "tail"));

        AssertBuffer($"head{newline}inserted{newline}AZtail{newline}end");
    }

    [Fact]
    public void FullReplacementCanBeFollowedByARangedChangeInTheSameBatch()
    {
        var server = Open("old");

        Change(server, 2,
            new TextDocumentContentChangeEvent(null, "new\nvalue"),
            Edit(1, 0, 1, 5, "updated"));

        AssertBuffer("new\nupdated");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LateOrDuplicateChangesPreserveTheNewerBuffer(int staleVersion)
    {
        var server = Open("old");
        Change(server, 2, new TextDocumentContentChangeEvent(null, "new"));

        // Even an invalid range must never be evaluated for an obsolete notification.
        Change(server, staleVersion, Edit(50, 0, 50, 1, "stale"));
        AssertBuffer("new");

        Change(server, 3, Edit(0, 3, 0, 3, "!"));
        AssertBuffer("new!");
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 0, 50, 0)]
    [InlineData(0, 2, 0, 1)]
    public void InvalidRangesReleaseTheDivergedBufferAndAllowReopening(
        int startLine, int startCharacter, int endLine, int endCharacter)
    {
        var server = Open("original");

        Change(server, 2,
            Edit(startLine, startCharacter, endLine, endCharacter, "invalid"));

        Assert.False(OpenDocumentStore.IsOpen(_path));
        Assert.False(OpenDocumentStore.TryGet(_path, out _));

        server.DidOpen(new DidOpenTextDocumentParams(
            new TextDocumentItem(Uri, "csharp", 1, "reopened")));
        Change(server, 2, Edit(0, 8, 0, 8, "!"));
        AssertBuffer("reopened!");
    }

    [Fact]
    public void AcceptedUnchangedTextStillAdvancesTheVersion()
    {
        var server = Open("original");
        Change(server, 3, Edit(0, 0, 0, 0, ""));

        Change(server, 2, new TextDocumentContentChangeEvent(null, "obsolete"));
        AssertBuffer("original");

        Change(server, 4, Edit(0, 8, 0, 8, "!"));
        AssertBuffer("original!");
    }

    [Fact]
    public void InvalidBatchPreservesTheOtherWindowsTextAndVersion()
    {
        var first = Open("original", version: 4);
        var second = Open("original", version: 4);

        // The first window is further along in its independent version sequence. No part
        // of a rejected batch, including its version, may become the second window's state.
        Change(first, 100,
            new TextDocumentContentChangeEvent(null, "replacement"),
            Edit(0, 0, 0, 0, "prefix "),
            Edit(50, 0, 50, 1, "invalid"));

        AssertBuffer("original");
        Change(second, 5, Edit(0, 8, 0, 8, "!"));
        AssertBuffer("original!");

        // The diverged window has relinquished ownership, so closing the survivor removes it.
        second.DidClose(new DidCloseTextDocumentParams(new TextDocumentIdentifier(Uri)));
        Assert.False(OpenDocumentStore.IsOpen(_path));
    }

    [Fact]
    public void IndependentWindowVersionsAndDisconnectsPreserveTheRemainingBuffer()
    {
        var first = Open("original", version: 40);
        var second = Open("original", version: 1);
        Change(first, 41, new TextDocumentContentChangeEvent(null, "first window"));
        Change(second, 2, new TextDocumentContentChangeEvent(null, "second window"));
        AssertBuffer("second window");

        first.Dispose();
        _servers.Remove(first);
        AssertBuffer("second window");

        Change(second, 3, Edit(0, 13, 0, 13, "!"));
        AssertBuffer("second window!");
        second.DidClose(new DidCloseTextDocumentParams(new TextDocumentIdentifier(Uri)));
        Assert.False(OpenDocumentStore.IsOpen(_path));
    }

    private string Uri => LspConverters.PathToUri(_path);

    private LspServer Open(string text, int version = 1)
    {
        var server = new LspServer(new EmptyServices());
        _servers.Add(server);
        server.DidOpen(new DidOpenTextDocumentParams(new TextDocumentItem(Uri, "csharp", version, text)));
        return server;
    }

    private void Change(LspServer server, int version, params TextDocumentContentChangeEvent[] changes) =>
        server.DidChange(new DidChangeTextDocumentParams(
            new VersionedTextDocumentIdentifier(Uri, version), changes));

    private static TextDocumentContentChangeEvent Edit(
        int startLine, int startCharacter, int endLine, int endCharacter, string text) =>
        new(new Range(new Position(startLine, startCharacter), new Position(endLine, endCharacter)), text);

    private void AssertBuffer(string expected)
    {
        Assert.True(OpenDocumentStore.TryGet(_path, out var text));
        Assert.Equal(expected, text.ToString());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var server in _servers)
            server.Dispose();
        await ImportCompletionWarmer.DrainForTestsAsync();
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
