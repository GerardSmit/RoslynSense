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
/// Navigation whose answer lives in a project that only reaches the caret's project by referencing
/// it. The workspace loads lazily by following references, which points away from every answer
/// here: opening the caret's project brings in what <em>it</em> references, and the projects these
/// assertions name are the ones referencing it back.
/// </summary>
/// <remarks>
/// <para>
/// Two fixtures, one per direction of the same hole. MediatorModules dispatches a request from Api,
/// which references only Contracts, while the two handlers sit in the sibling modules Inventory and
/// Billing — so an answer of one handler means "the one project that happened to be loaded", and an
/// answer of none means the search never left the dispatch project's closure. LayeredApp declares
/// an extension method in Warehouse whose only call site is in Storefront, the project referencing
/// it — the exact shape that reads "0 references" on a method the solution calls.
/// </para>
/// <para>
/// Nothing in these tests ever resolves a document from Inventory, Billing or Storefront, so the
/// projects holding the expected answers are never loaded as a side effect of asking the question.
/// That is the point: the search itself has to widen the solution, the way an explicit gesture
/// (Shift+F12, Ctrl+F12) is allowed to.
/// </para>
/// </remarks>
[Collection(SharedState.Name)]
public class CrossProjectNavigationTests
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

    private static bool IsFile(LspLocation location, string path) =>
        string.Equals(
            Path.GetFullPath(LspConverters.UriToPath(location.Uri)),
            Path.GetFullPath(path),
            StringComparison.OrdinalIgnoreCase);

    private static string LineAt(LspLocation location)
    {
        var text = SourceText.From(File.ReadAllText(LspConverters.UriToPath(location.Uri)));
        return text.Lines[location.Range.Start.Line].ToString().Trim();
    }

    // ---- A request handled in two sibling modules ----------------------------------------------

    [Fact]
    public async Task ImplementationOnASendReachesTheHandlersInEveryModule()
    {
        var locations = await NavigationHandlers.ImplementationAsync(
            At(FixturePaths.MediatorModulesEndpointFile,
                "sender.Send(new SyncCustomersCommand(region)", "sender.".Length),
            default, s_session);

        // Both modules, not whichever one was loaded first — and not the dispatch interface the
        // caret binds to, which is what "one result" degrades into when neither module is found.
        AssertHandlerIn(locations, FixturePaths.MediatorModulesInventoryHandlerFile);
        AssertHandlerIn(locations, FixturePaths.MediatorModulesBillingHandlerFile);
    }

    [Fact]
    public async Task DefinitionOnASendReachesTheHandlersInEveryModule()
    {
        var locations = await NavigationHandlers.DefinitionAsync(
            At(FixturePaths.MediatorModulesEndpointFile,
                "sender.Send(new SyncCustomersCommand(region)", "sender.".Length),
            typeDefinition: false, default, s_session);

        AssertHandlerIn(locations, FixturePaths.MediatorModulesInventoryHandlerFile);
        AssertHandlerIn(locations, FixturePaths.MediatorModulesBillingHandlerFile);
    }

    private static void AssertHandlerIn(LspLocation[] locations, string handlerFile)
    {
        var inFile = locations.Where(l => IsFile(l, handlerFile)).ToArray();
        Assert.True(
            inFile.Length > 0,
            $"expected a handler in {Path.GetFileName(handlerFile)}; got " +
            (locations.Length == 0
                ? "nothing"
                : string.Join(", ", locations.Select(l => Path.GetFileName(LspConverters.UriToPath(l.Uri))))));

        Assert.Contains(inFile, l => LineAt(l).Contains("Handle(", StringComparison.Ordinal));
    }

    // ---- An extension method whose only call site is in the referencing project ----------------

    [Fact]
    public async Task FindReferencesOnAnExtensionMethodReachesTheProjectThatCallsIt()
    {
        // The caret is on the declaration, in the project that knows nothing about its callers —
        // the direction lazy loading does not follow. Shift+F12 is the explicit gesture that is
        // allowed to wait for the widened scope, which is what waitForCompleteScope carries.
        var locations = await NavigationHandlers.ReferencesAsync(
            new ReferenceParams(
                At(FixturePaths.LayeredAppWarehouseModuleFile,
                    "AddWarehouse(this IModuleRegistry registry)").TextDocument,
                At(FixturePaths.LayeredAppWarehouseModuleFile,
                    "AddWarehouse(this IModuleRegistry registry)").Position,
                new ReferenceContext(IncludeDeclaration: false)),
            default, LanguageSession.Empty);

        var callSites = locations.Where(l => IsFile(l, FixturePaths.LayeredAppStartupFile)).ToArray();
        Assert.True(
            callSites.Length > 0,
            "expected the call site in Startup.cs; got " +
            (locations.Length == 0
                ? "nothing"
                : string.Join(", ", locations.Select(l => Path.GetFileName(LspConverters.UriToPath(l.Uri))))));

        Assert.Contains(callSites, l => LineAt(l).Contains("registry.AddWarehouse()", StringComparison.Ordinal));
    }
}
