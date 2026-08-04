using RoslynMCP.Languages;
using RoslynMCP.Languages.Mediator;
using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The MCP side of the pack: the same two answers an AI session gets, through a different door.
/// </summary>
/// <remarks>
/// <para>
/// The tools do not go through <c>LanguageSession</c> — an MCP session belongs to no editor window
/// — so the only switch in front of them is the registration one, and each test publishes the
/// registry it wants the way the WebForms and protobuf tool tests do.
/// </para>
/// <para>
/// A caret is named by quoting the line here rather than by position, which is the one thing that
/// genuinely differs between the two front-ends.
/// </para>
/// </remarks>
[Collection(SharedState.Name)]
public class MediatorToolsTests
{
    private static void PublishMediatorPack() =>
        new LanguageRegistry([new MediatorLanguage()]).Publish();

    private static void PublishNothing() =>
        new LanguageRegistry([]).Publish();

    [Fact]
    public async Task FindUsagesReportsTheDispatchesAsTheirOwnSection()
    {
        PublishMediatorPack();

        string result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.MediatorOrdersFile,
            markupSnippet: "public ValueTask<OrderDto> [|Handle|](",
            fmt: new MarkdownFormatter());

        // Its own section rather than folded into the C# references, because the handler's name is
        // on none of these lines and a report that hid why would be one nobody could check.
        Assert.Contains("Mediator Dispatches", result);
        Assert.Contains("OrderController.cs", result);
        Assert.Contains("GeneratedExtension", result);
    }

    [Fact]
    public async Task FindUsagesCountsTheDispatchesInTheSummary()
    {
        PublishMediatorPack();

        string result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.MediatorOrdersFile,
            markupSnippet: "public ValueTask<OrderDto> [|Handle|](",
            fmt: new MarkdownFormatter());

        Assert.Contains("4 mediator dispatch(es)", result);
    }

    [Fact]
    public async Task FindUsagesWithoutTheRegistryReportsNone()
    {
        PublishNothing();

        string result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.MediatorOrdersFile,
            markupSnippet: "public ValueTask<OrderDto> [|Handle|](",
            fmt: new MarkdownFormatter());

        // roslynsense.json and --no-mediator are the only switches that reach an MCP session, so
        // this is what turning the pack off has to look like from here.
        Assert.DoesNotContain("Mediator Dispatches", result);
    }

    [Fact]
    public async Task GoToDefinitionLandsOnTheHandlerRatherThanTheInterface()
    {
        PublishMediatorPack();

        string result = await GoToDefinitionSnippetTool.GoToDefinitionSnippet(
            filePath: FixturePaths.MediatorControllerFile,
            markupSnippet: "mediatr.[|Send|](new GetOrderQuery(id))",
            fmt: new MarkdownFormatter());

        Assert.Contains("GetOrderQueryHandler", result);
        Assert.DoesNotContain("MediatR.ISender", result);
    }

    [Fact]
    public async Task GoToDefinitionOnAPublishListsEveryHandler()
    {
        PublishMediatorPack();

        string result = await GoToDefinitionSnippetTool.GoToDefinitionSnippet(
            filePath: FixturePaths.MediatorControllerFile,
            markupSnippet: "mediatr.[|Publish|](new OrderPlacedNotification(orderId))",
            fmt: new MarkdownFormatter());

        Assert.Contains("2 handlers", result);
        Assert.Contains("SendReceipt", result);
        Assert.Contains("UpdateStock", result);
    }

    [Fact]
    public async Task GoToDefinitionWithoutTheRegistryStillAnswersRoslyns()
    {
        PublishNothing();

        string result = await GoToDefinitionSnippetTool.GoToDefinitionSnippet(
            filePath: FixturePaths.MediatorControllerFile,
            markupSnippet: "mediatr.[|Send|](new GetOrderQuery(id))",
            fmt: new MarkdownFormatter());

        Assert.DoesNotContain("GetOrderQueryHandler", result);
    }

    [Fact]
    public async Task CallHierarchyReportsTheDispatchesAsCallers()
    {
        PublishMediatorPack();

        string result = await CallHierarchyTool.GetCallHierarchy(
            filePath: FixturePaths.MediatorOrdersFile,
            markupSnippet: "public ValueTask<OrderDto> [|Handle|](",
            fmt: new MarkdownFormatter(),
            direction: "callers");

        // The hierarchy and find-references have to agree about one caret, which is the whole
        // reason the contributor seam exists on both.
        Assert.Contains("Mediator Dispatches", result);
        Assert.Contains("GetViaExtensionArguments", result);
    }

    [Fact]
    public async Task AnAspxReportIsUnchanged()
    {
        PublishMediatorPack();

        string result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.EventWiringCodeBehindFile,
            markupSnippet: "protected int [|Total|]() => 42;",
            fmt: new MarkdownFormatter());

        // The regression guard on appending the new parameter rather than inserting it: the other
        // caller passes the first arguments positionally.
        Assert.Contains("ASPX References", result);
        Assert.DoesNotContain("Mediator Dispatches", result);
    }
}
