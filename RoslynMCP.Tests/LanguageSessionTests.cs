using System.Text.Json;
using RoslynMCP.Config;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Mediator;
using RoslynMCP.Languages.Proto;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The activation gate: <c>roslynSense.languages.*</c> from one editor's initialization options
/// switches packs off for that connection only.
/// </summary>
/// <remarks>
/// The point of these is what they do <em>not</em> touch. One daemon serves several editor
/// windows and every MCP client from a single container, so a language toggle held anywhere
/// process-wide would let one window strip a pack from another window and remove the matching
/// tools from an AI session. Nothing here writes a static, which is the property being asserted.
/// </remarks>
[Collection(SharedState.Name)]
public class LanguageSessionTests : IDisposable
{
    private readonly string _scope = LspFeatureOptions.WorkspaceDiagnosticsScope;

    public void Dispose() => LspFeatureOptions.WorkspaceDiagnosticsScope = _scope;


    [Fact]
    public void TwoSessionsWithDifferentLanguageSettingsDoNotInterfere()
    {
        var registry = Registry();

        var withWebForms = Session(registry, """
            { "languages": { "webforms": true } }
            """);
        var withoutWebForms = Session(registry, """
            { "languages": { "webforms": false } }
            """);

        Assert.IsType<WebFormsLanguage>(withWebForms.Resolve("C:/site/Default.aspx"));
        Assert.Null(withoutWebForms.Resolve("C:/site/Default.aspx"));

        // Order of construction must not matter either: the second session narrowing its own set
        // is the failure mode a process-wide switch would produce.
        Assert.IsType<WebFormsLanguage>(withWebForms.Resolve("C:/site/Controls/Menu.ascx"));
        Assert.True(withWebForms.IsEnabled("webforms"));
        Assert.False(withoutWebForms.IsEnabled("webforms"));
    }

    [Fact]
    public void ADisabledPackLeavesItsFilesToTheCSharpHandlers()
    {
        var session = Session(Registry(), """
            { "languages": { "webforms": false } }
            """);

        // Null from Resolve is how LspServer.Route spells "the C# handler answers this".
        Assert.Null(session.Resolve("C:/site/Default.aspx"));
        Assert.Empty(session.Packs);
        Assert.False(session.IsProjectionPath("C:/site/Default.aspx-inline.g.cs"));

        // C# documents were never the pack's, so the answer for them is unchanged.
        Assert.Null(session.Resolve("C:/site/Default.aspx.cs"));
    }

    [Fact]
    public void DisablingProtoRoutesProtoFilesToTheCSharpFallback()
    {
        var registry = new LanguageRegistry(
            [new WebFormsLanguage(new MarkdownFormatter()), new ProtoLanguage(new MarkdownFormatter())]);

        var on = Session(registry, """{ "languages": { "proto": true } }""");
        var off = Session(registry, """{ "languages": { "proto": false } }""");

        Assert.IsType<ProtoLanguage>(on.Resolve("C:/contracts/widgets.proto"));
        Assert.Null(off.Resolve("C:/contracts/widgets.proto"));
        Assert.False(off.IsEnabled("proto"));

        // One window switching proto off must not take another pack with it, in either direction.
        Assert.IsType<WebFormsLanguage>(off.Resolve("C:/site/Default.aspx"));
        Assert.True(off.IsEnabled("webforms"));
        Assert.IsType<ProtoLanguage>(
            Session(registry, """{ "languages": { "webforms": false } }""")
                .Resolve("C:/contracts/widgets.proto"));
    }

    [Fact]
    public void TheProtoPackClaimsProtoFilesAndProjectsNothing()
    {
        var session = new LanguageSession([new ProtoLanguage(new MarkdownFormatter())]);

        Assert.IsType<ProtoLanguage>(session.Resolve("C:/contracts/widgets.proto"));
        Assert.IsType<ProtoLanguage>(session.Resolve("C:/contracts/WIDGETS.PROTO"));

        Assert.Null(session.Resolve("C:/contracts/Widgets.cs"));
        Assert.Null(session.Resolve("C:/site/Default.aspx"));
        Assert.Null(session.Resolve("C:/contracts/protobuf"));
        Assert.Null(session.Resolve(null));

        // Grpc.Tools writes real .cs into obj and MSBuild hands them to Roslyn as ordinary Compile
        // items, so the C# behind a .proto is already in the compilation and there is nothing to
        // project. A pack claiming one of those documents would take hover, rename and diagnostics
        // away from Roslyn and hand them to a parser that has never seen C#.
        Assert.False(session.IsProjectionPath("C:/contracts/obj/Debug/net10.0/Widgets.cs"));
        Assert.False(session.IsProjectionPath(FixturePaths.WidgetsGeneratedFile));
        Assert.False(session.IsProjectionPath("C:/contracts/widgets.proto"));
        Assert.False(session.IsProjectionPath(null));
    }

    [Fact]
    public void EditorSettingsDoNotReachTheMcpToolSurface()
    {
        var registry = Registry();

        _ = Session(registry, """
            { "languages": { "webforms": false } }
            """);

        // The registry is the only gate on the tools, and roslynsense.json is the only thing
        // that closes it. An editor window's preference must not reach an AI session here.
        Assert.IsType<WebFormsLanguage>(Assert.Single(registry.Packs));
        Assert.Single(registry.GoToDefinitionHandlers);
        Assert.Single(registry.FindUsagesHandlers);
        Assert.Single(registry.OutlineHandlers);
        Assert.Single(registry.RenameHandlers);
        Assert.Single(registry.DiagnosticsHandlers);
        Assert.IsType<WebFormsLanguage>(registry.Resolve("C:/site/Default.aspx"));
    }

    [Fact]
    public void APackTheClientNeverMentionsStaysOn()
    {
        var registry = Registry();

        // No section at all — an older client, or one that contributes no such setting.
        Assert.IsType<WebFormsLanguage>(
            Session(registry, """{ "analyzerDiagnostics": true }""").Resolve("C:/site/Default.aspx"));

        // A section that names other packs, which is what a client one language behind sends.
        Assert.IsType<WebFormsLanguage>(
            Session(registry, """{ "languages": { "graphql": false } }""")
                .Resolve("C:/site/Default.aspx"));

        // Nothing sent at all, which is what a directly constructed server sees.
        Assert.True(ConfigurationHandler.ReadLanguages(null).IsEnabled("webforms"));
    }

    [Fact]
    public void TheSettingIsReadFromTheClientsOwnSectionToo()
    {
        var activation = ConfigurationHandler.ReadLanguages(Settings("""
            {
              "roslynSense": {
                "languages": { "webforms": false }
              }
            }
            """));

        Assert.False(activation.IsEnabled("webforms"));
        Assert.True(activation.IsEnabled("razor"));
    }

    [Fact]
    public async Task ADisabledPackContributesNothingToWorkspaceSymbol()
    {
        var registry = Published();
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.AspxProjectFile);

        // lblHeading exists only as a control ID in the markup. Roslyn's declaration search cannot
        // see it, so a hit for it is the pack's and nobody else's.
        var query = new WorkspaceSymbolParams("lblHeading");

        var enabled = await SymbolHandlers.WorkspaceSymbolsAsync(query, default, With(registry, true));
        Assert.Contains(enabled, s => IsMarkup(s.Location.Uri));

        var disabled = await SymbolHandlers.WorkspaceSymbolsAsync(query, default, With(registry, false));
        Assert.DoesNotContain(disabled, s => IsMarkup(s.Location.Uri));
    }

    [Fact]
    public async Task ADisabledPackIsLeftOutOfTheWorkspaceSweep()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";

        var registry = Published();
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.AspxProjectFile);

        var enabled = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default, With(registry, true));
        Assert.Contains(MarkupReports(enabled), r => r.Items.Length > 0);

        var disabled = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default, With(registry, false));

        // The sweep runs outside any document request, which is exactly where the toggle used to
        // stop applying: the pack was asked through the process-wide registry, so this window kept
        // getting markup problems it had switched off.
        Assert.Empty(MarkupReports(disabled));
    }

    /// <summary>Every registered pack, as the daemon's own settings left it.</summary>
    [Fact]
    public void APackWithNoExtensionsNeverResolvesByExtensionButStillContributes()
    {
        var registry = new LanguageRegistry([new MediatorLanguage()]);

        var on = Session(registry, """{ "languages": { "mediator": true } }""");
        var off = Session(registry, """{ "languages": { "mediator": false } }""");

        // The assumption the whole pack rests on. It owns no file type, so the extension routing
        // can never reach it — which is why it implements no provider interface — but the
        // contributor lookup does not consult extensions and finds it anyway.
        Assert.Null(on.Resolve("C:/app/OrderController.cs"));
        Assert.Null(on.Resolve("C:/app/anything.mediator"));
        Assert.Single(on.Contributors<ILanguageDefinitionRedirector>());
        Assert.Single(on.Contributors<ILanguageReferenceContributor>());
        Assert.Single(on.Contributors<ILanguageCodeLensContributor>());

        // And the per-window switch still reaches it, which is the only reason it has an id at all.
        Assert.False(off.IsEnabled(MediatorLanguage.PackId));
        Assert.Empty(off.Contributors<ILanguageDefinitionRedirector>());
    }

    private static LanguageRegistry Registry() =>
        new([new WebFormsLanguage(new MarkdownFormatter())]);

    /// <summary>
    /// The same, published — which is what the static handlers fall back to when they are called
    /// from outside a connection. Publishing is what gives the negative assertions their meaning:
    /// the pack is registered and would answer, and the session alone is what silences it.
    /// </summary>
    private static LanguageRegistry Published() => Registry().Publish();

    private static LanguageSession With(LanguageRegistry registry, bool webForms) =>
        Session(registry, $$"""{ "languages": { "webforms": {{(webForms ? "true" : "false")}} } }""");

    private static IEnumerable<WorkspaceFullDocumentDiagnosticReport> MarkupReports(
        WorkspaceDiagnosticReport report) =>
        report.Items
            .OfType<WorkspaceFullDocumentDiagnosticReport>()
            .Where(r => IsMarkup(r.Uri));

    private static bool IsMarkup(string uri) =>
        Uri.UnescapeDataString(uri).EndsWith(".aspx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What <c>LspServer.Initialize</c> does: read the connection's options, then build that
    /// connection's session over the registry the whole process shares.
    /// </summary>
    private static LanguageSession Session(LanguageRegistry registry, string json)
    {
        var activation = ConfigurationHandler.ReadLanguages(Settings(json));
        return new LanguageSession(registry.Packs, pack => activation.IsEnabled(pack.Id));
    }

    private static JsonElement Settings(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
