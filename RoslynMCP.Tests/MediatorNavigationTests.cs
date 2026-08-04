using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Mediator;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using Xunit;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Tests;

/// <summary>
/// F12 and Ctrl+F12 on a dispatch, reaching the handler that runs rather than the interface member
/// every dispatch in the solution binds to.
/// </summary>
/// <remarks>
/// Driven through <see cref="NavigationHandlers"/> rather than by calling the pack, because a
/// redirect only means anything in place of Roslyn's answer — and replacing it, rather than being
/// offered beside it, is half of what is under test.
/// </remarks>
[Collection(SharedState.Name)]
public class MediatorNavigationTests
{
    private static readonly LanguageSession s_session = new([new MediatorLanguage()]);

    private static TextDocumentPositionParams At(string path, string needle, int offsetIntoNeedle = 0)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");

        var position = SourceText.From(text).Lines.GetLinePosition(index + offsetIntoNeedle);
        return new TextDocumentPositionParams(
            new TextDocumentIdentifier(LspConverters.PathToUri(path)),
            new Position(position.Line, position.Character));
    }

    private static Task<LspLocation[]> DefinitionAsync(
        string path, string needle, int offsetIntoNeedle = 0, bool typeDefinition = false,
        LanguageSession? session = null) =>
        NavigationHandlers.DefinitionAsync(
            At(path, needle, offsetIntoNeedle), typeDefinition, default, session ?? s_session);

    private static bool IsFile(LspLocation location, string path) =>
        string.Equals(
            Path.GetFullPath(LspConverters.UriToPath(location.Uri)),
            Path.GetFullPath(path),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The source line a location points at, which is what says it reached the member and
    /// not merely the right file.</summary>
    private static string LineAt(LspLocation location)
    {
        var text = SourceText.From(File.ReadAllText(LspConverters.UriToPath(location.Uri)));
        return text.Lines[location.Range.Start.Line].ToString().Trim();
    }

    private static void AssertLandsOn(LspLocation[] locations, string file, string expectedLine)
    {
        var location = Assert.Single(locations);
        Assert.True(IsFile(location, file), $"expected {Path.GetFileName(file)}, got {location.Uri}");
        Assert.Contains(expectedLine, LineAt(location), StringComparison.Ordinal);
    }

    // ---- Requests -------------------------------------------------------------------------------

    [Fact]
    public async Task DefinitionOnAMediatRSendLandsOnTheHandler()
    {
        var locations = await DefinitionAsync(
            FixturePaths.MediatorControllerFile, "mediatr.Send(new GetOrderQuery(id))", "mediatr.".Length);

        // MediatR's usual overload takes the response as its only type argument, so anything reading
        // position zero as the message would answer OrderDto here.
        AssertLandsOn(locations, FixturePaths.MediatorOrdersFile, "public ValueTask<OrderDto> Handle(");

        // The interface member Roslyn binds to is replaced, not merely joined.
        Assert.DoesNotContain(locations, l => IsFile(l, FixturePaths.MediatRStubsFile));
    }

    [Fact]
    public async Task DefinitionOnAZaptoSendLandsOnTheHandler()
    {
        var locations = await DefinitionAsync(
            FixturePaths.MediatorControllerFile,
            "zapto.Send<GetOrderQuery, OrderDto>", "zapto.".Length);

        AssertLandsOn(locations, FixturePaths.MediatorOrdersFile, "public ValueTask<OrderDto> Handle(");
        Assert.DoesNotContain(locations, l => IsFile(l, FixturePaths.ZaptoStubsFile));
    }

    [Fact]
    public async Task DefinitionOnAGeneratedExtensionMethodLandsOnTheHandler()
    {
        var locations = await DefinitionAsync(
            FixturePaths.MediatorControllerFile, "zapto.GetOrderQueryAsync(query)", "zapto.".Length);

        AssertLandsOn(locations, FixturePaths.MediatorOrdersFile, "public ValueTask<OrderDto> Handle(");
    }

    [Fact]
    public async Task DefinitionOnTheConstructorArgumentOverloadLandsOnTheHandler()
    {
        var locations = await DefinitionAsync(
            FixturePaths.MediatorControllerFile, "zapto.GetOrderQueryAsync(id)", "zapto.".Length);

        // This call site names neither the request type nor anything of its shape — the message is
        // recoverable only by reading the body the generator emitted.
        AssertLandsOn(locations, FixturePaths.MediatorOrdersFile, "public ValueTask<OrderDto> Handle(");
    }

    [Fact]
    public async Task DefinitionOnAHandlerReachedThroughItsBaseLandsOnTheOverride()
    {
        var locations = await DefinitionAsync(
            FixturePaths.MediatorControllerFile,
            "zapto.Send<ArchiveOrderRequest, bool>", "zapto.".Length);

        // Not the interface's Handle, which resolves to the base's explicit implementation in the
        // library and would land the caret outside the user's code entirely.
        AssertLandsOn(
            locations, FixturePaths.MediatorOrdersFile,
            "protected override bool Handle(IServiceProvider provider, ArchiveOrderRequest request)");
    }

    // ---- Notifications --------------------------------------------------------------------------

    [Fact]
    public async Task DefinitionOnAPublishReturnsEveryHandler()
    {
        var locations = await DefinitionAsync(
            FixturePaths.MediatorControllerFile,
            "mediatr.Publish(new OrderPlacedNotification(orderId))", "mediatr.".Length);

        // Several handlers is what a notification means, not an ambiguity to choose between.
        Assert.Equal(2, locations.Length);
        Assert.All(locations, l => Assert.True(IsFile(l, FixturePaths.MediatorNotificationsFile)));
    }

    // ---- Declining ------------------------------------------------------------------------------

    [Fact]
    public async Task ASendOnSomethingElseStillGetsRoslynsAnswer()
    {
        var locations = await DefinitionAsync(
            FixturePaths.MediatorDecoysFile, "_transport.Send(payload)", "_transport.".Length);

        AssertLandsOn(locations, FixturePaths.MediatorDecoysFile, "public void Send(byte[] payload)");
    }

    [Fact]
    public async Task ARequestTheCallerBuiltElsewhereIsNotGuessedAt()
    {
        var locations = await DefinitionAsync(
            FixturePaths.MediatorControllerFile, "mediatr.Send(built)", "mediatr.".Length);

        // Typed only as the marker, so which handler runs is not a static fact. Roslyn's own answer
        // is the honest one.
        Assert.True(IsFile(Assert.Single(locations), FixturePaths.MediatRStubsFile));
    }

    [Fact]
    public async Task WithoutTheSessionTheAnswerIsRoslynsAgain()
    {
        var locations = await DefinitionAsync(
            FixturePaths.MediatorControllerFile, "mediatr.Send(new GetOrderQuery(id))",
            "mediatr.".Length, session: LanguageSession.Empty);

        Assert.True(IsFile(Assert.Single(locations), FixturePaths.MediatRStubsFile));
    }

    // ---- The other two verbs --------------------------------------------------------------------

    [Fact]
    public async Task TypeDefinitionOnASendLandsOnTheHandlerType()
    {
        var locations = await DefinitionAsync(
            FixturePaths.MediatorControllerFile, "mediatr.Send(new GetOrderQuery(id))",
            "mediatr.".Length, typeDefinition: true);

        AssertLandsOn(locations, FixturePaths.MediatorOrdersFile, "public sealed class GetOrderQueryHandler");
    }

    [Fact]
    public async Task ImplementationOnASendMatchesDefinition()
    {
        var caret = At(FixturePaths.MediatorControllerFile,
            "mediatr.Send(new GetOrderQuery(id))", "mediatr.".Length);

        var definition = await NavigationHandlers.DefinitionAsync(
            caret, typeDefinition: false, default, s_session);
        var implementation = await NavigationHandlers.ImplementationAsync(caret, default, s_session);

        // Without the seam Ctrl+F12 here falls through every arm and lands back on the caret it
        // started from, which would have the two verbs disagreeing about one line.
        Assert.Equal(definition, implementation);
    }
}
