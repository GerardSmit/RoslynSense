using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Mediator;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using Xunit;
using LspCodeLens = RoslynMCP.Lsp.Protocol.CodeLens;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Tests;

/// <summary>
/// Shift+F12 on a handler, and the gutter above it. Both answer the question Roslyn cannot: which
/// call sites reach this, given that none of them names it.
/// </summary>
[Collection(SharedState.Name)]
public class MediatorReferenceTests
{
    private static readonly LanguageSession s_session = new([new MediatorLanguage()]);

    /// <summary>Every dispatch that reaches <c>GetOrderQuery</c>'s handler, by the line it is on.</summary>
    private static readonly string[] s_allDispatches =
    [
        "mediatr.Send(new GetOrderQuery(id));",
        "zapto.Send<GetOrderQuery, OrderDto>(new GetOrderQuery(id));",
        "zapto.GetOrderQueryAsync(query);",
        "zapto.GetOrderQueryAsync(id);",
    ];

    private static Position PositionOf(string path, string needle, int offsetIntoNeedle = 0)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");

        var position = SourceText.From(text).Lines.GetLinePosition(index + offsetIntoNeedle);
        return new Position(position.Line, position.Character);
    }

    private static Task<LspLocation[]> ReferencesAsync(
        string path, string needle, int offsetIntoNeedle = 0, LanguageSession? session = null) =>
        NavigationHandlers.ReferencesAsync(
            new ReferenceParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(path)),
                PositionOf(path, needle, offsetIntoNeedle),
                new ReferenceContext(IncludeDeclaration: false)),
            default,
            session ?? s_session);

    private static string LineAt(LspLocation location)
    {
        var text = SourceText.From(File.ReadAllText(LspConverters.UriToPath(location.Uri)));
        return text.Lines[location.Range.Start.Line].ToString().Trim();
    }

    /// <summary>The lines reported inside <c>OrderController.cs</c>, trimmed.</summary>
    private static string[] ControllerLines(LspLocation[] locations) =>
    [
        .. locations
            .Where(l => LspConverters.UriToPath(l.Uri)
                .EndsWith("OrderController.cs", StringComparison.OrdinalIgnoreCase))
            .Select(LineAt)
            .Distinct()
            .Order(StringComparer.Ordinal),
    ];

    [Fact]
    public async Task TheHandleMethodReportsEveryDispatchThatReachesIt()
    {
        var locations = await ReferencesAsync(
            FixturePaths.MediatorOrdersFile, "public ValueTask<OrderDto> Handle(", "public ValueTask<OrderDto> ".Length);

        Assert.Equal(s_allDispatches.Order(StringComparer.Ordinal), ControllerLines(locations));
    }

    [Fact]
    public async Task TheHandlerTypeReportsThemToo()
    {
        var locations = await ReferencesAsync(
            FixturePaths.MediatorOrdersFile, "GetOrderQueryHandler : Zapto.Mediator");

        Assert.Equal(s_allDispatches.Order(StringComparer.Ordinal), ControllerLines(locations));
    }

    [Fact]
    public async Task TheRequestTypeAddsOnlyTheCallsRoslynCannotSee()
    {
        const string needle = "record GetOrderQuery(int Id)";
        int offset = "record ".Length;

        var withPack = await ReferencesAsync(FixturePaths.MediatorOrdersFile, needle, offset);
        var roslynOnly = await ReferencesAsync(
            FixturePaths.MediatorOrdersFile, needle, offset, LanguageSession.Empty);

        // Only the generated extension calls. The Send call sites name the request, so Roslyn
        // already has them, and contributing those too would report each twice with a wider span —
        // the de-duplication downstream is structural over the range, so it would not merge them.
        var added = withPack.Except(roslynOnly).ToArray();

        Assert.Equal(2, added.Length);
        Assert.All(added, l =>
            Assert.Contains("GetOrderQueryAsync", LineAt(l), StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoDispatchIsReportedTwice()
    {
        var locations = await ReferencesAsync(
            FixturePaths.MediatorOrdersFile,
            "public ValueTask<OrderDto> Handle(", "public ValueTask<OrderDto> ".Length);

        // The same call is reached twice from the handler's side — once through the request type
        // and once through the generated method that wraps it — and has to collapse to one.
        Assert.Equal(locations.Distinct().Count(), locations.Length);
    }

    [Fact]
    public async Task AMentionInACommentOrAStringIsNotAUsage()
    {
        var locations = await ReferencesAsync(
            FixturePaths.MediatorOrdersFile, "public ValueTask<OrderDto> Handle(", "public ValueTask<OrderDto> ".Length);

        Assert.DoesNotContain(locations, l =>
            LineAt(l).Contains("return \"GetOrderQuery\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutTheSessionOnlyRoslynsOwnReferencesComeBack()
    {
        var locations = await ReferencesAsync(
            FixturePaths.MediatorOrdersFile, "public ValueTask<OrderDto> Handle(",
            "public ValueTask<OrderDto> ".Length, session: LanguageSession.Empty);

        Assert.Empty(ControllerLines(locations));
    }

    // ---- The gutter -----------------------------------------------------------------------------

    [Fact]
    public async Task AHandleWithNoCSharpReferencesStillCountsItsSenders()
    {
        var lens = await SenderLensAsync("public ValueTask<OrderDto> Handle(", "public ValueTask<OrderDto> ".Length);
        var resolved = await CodeLensHandler.ResolveAsync(lens, default, s_session);

        // The count nobody could get from Roslyn: the method has no C# references at all, and a
        // gutter reading "0 references" over a peek listing four is the disagreement this fixes.
        Assert.Equal($"{s_allDispatches.Length} senders", resolved.Command?.Title);
    }

    [Fact]
    public async Task TheHandlerTypeGetsALensToo()
    {
        var lens = await SenderLensAsync("GetOrderQueryHandler : Zapto.Mediator");
        var resolved = await CodeLensHandler.ResolveAsync(lens, default, s_session);

        Assert.Equal($"{s_allDispatches.Length} senders", resolved.Command?.Title);
    }

    [Fact]
    public async Task NothingThatIsNotAHandlerGetsOne()
    {
        var lenses = await CodeLensHandler.CodeLensAsync(
            new CodeLensParams(new TextDocumentIdentifier(
                LspConverters.PathToUri(FixturePaths.MediatorDecoysFile))),
            default,
            s_session);

        Assert.DoesNotContain(lenses, l => l.Data?.PackId == MediatorLanguage.PackId);
    }

    /// <summary>The pack's own lens at a position in Orders.cs.</summary>
    private static async Task<LspCodeLens> SenderLensAsync(string needle, int offsetIntoNeedle = 0)
    {
        var lenses = await CodeLensHandler.CodeLensAsync(
            new CodeLensParams(new TextDocumentIdentifier(
                LspConverters.PathToUri(FixturePaths.MediatorOrdersFile))),
            default,
            s_session);

        var position = PositionOf(FixturePaths.MediatorOrdersFile, needle, offsetIntoNeedle);

        return Assert.Single(lenses, l =>
            l.Data?.PackId == MediatorLanguage.PackId && l.Data.Line == position.Line);
    }
}
