using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Languages;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>workspace/diagnostic: the Problems panel without opening every file, scoped so a
/// large solution is not swept on every request.</summary>
[Collection(SharedState.Name)]
public class WorkspaceDiagnosticsTests : IDisposable
{
    private readonly string _scope = LspFeatureOptions.WorkspaceDiagnosticsScope;
    private readonly string _session = $"wsdiag-{Guid.NewGuid():N}";

    public void Dispose()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = _scope;
        OpenDocumentStore.CloseSession(_session);
    }

    [Fact]
    public async Task ScopeOffReturnsNothing()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "off";

        var report = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        Assert.Empty(report.Items);
    }

    [Fact]
    public async Task OpenProjectsScopeReturnsNothingWhenNoDocumentIsOpen()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "openProjects";
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.BrokenProjectFile);

        var report = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        // Nothing is open, so there is nothing the user is working on to report about.
        Assert.Empty(report.Items);
    }

    [Fact]
    public async Task OpenProjectsScopeReportsTheProjectOwningAnOpenDocument()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "openProjects";
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.BrokenProjectFile);

        string path = FixturePaths.BrokenSemanticFile;
        OpenDocumentStore.Open(_session, path, SourceText.From(await File.ReadAllTextAsync(path)), 1);

        var report = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        Assert.NotEmpty(report.Items);
        var full = report.Items.OfType<WorkspaceFullDocumentDiagnosticReport>().ToList();
        Assert.Contains(full, r => r.Uri.EndsWith("BrokenSemantic.cs", StringComparison.OrdinalIgnoreCase));
        // The fixture is deliberately broken, so the sweep must actually find something.
        Assert.Contains(full, r => r.Items.Length > 0);
    }

    [Fact]
    public async Task UnchangedDocumentsAnswerUnchangedOnASecondSweep()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "openProjects";
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.BrokenProjectFile);

        string path = FixturePaths.BrokenSemanticFile;
        OpenDocumentStore.Open(_session, path, SourceText.From(await File.ReadAllTextAsync(path)), 1);

        var first = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        var previous = first.Items
            .OfType<WorkspaceFullDocumentDiagnosticReport>()
            .Where(r => r.ResultId is not null)
            .Select(r => new PreviousResultId(r.Uri, r.ResultId!))
            .ToArray();
        Assert.NotEmpty(previous);

        var second = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(previous), default);

        // Re-reporting an unchanged world on every sweep is what makes this feature unusable.
        Assert.Contains(second.Items, item => item is WorkspaceUnchangedDocumentDiagnosticReport);
    }

    [Fact]
    public async Task MarkupIsSweptWithoutAnyDocumentBeingOpen()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";
        await UseWebFormsAsync();

        var report = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        // The point of the sweep: a markup file is not a Roslyn document, so an OnClick= naming a
        // handler that does not exist reached Problems only while the page was open.
        Assert.False(OpenDocumentStore.IsOpen(FixturePaths.EventWiringAspxFile));

        var markup = Assert.Single(
            report.Items.OfType<WorkspaceFullDocumentDiagnosticReport>(),
            r => r.Uri.EndsWith("EventWiring.aspx", StringComparison.OrdinalIgnoreCase));

        var diagnostic = Assert.Single(markup.Items, d => d.Code == "WFC0008");
        Assert.Contains("MissingHandler", diagnostic.Message);
    }

    [Fact]
    public async Task UnchangedMarkupAnswersUnchangedOnASecondSweep()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";
        await UseWebFormsAsync();

        var first = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        var previous = first.Items
            .OfType<WorkspaceFullDocumentDiagnosticReport>()
            .Where(r => r.ResultId is not null && r.Uri.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
            .Select(r => new PreviousResultId(r.Uri, r.ResultId!))
            .ToArray();
        Assert.NotEmpty(previous);

        var second = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(previous), default);

        // Re-parsing every page in a site on every sweep is what makes the feature unusable, so
        // the result id has to survive a round trip through the client.
        Assert.Contains(
            second.Items.OfType<WorkspaceUnchangedDocumentDiagnosticReport>(),
            r => r.Uri.EndsWith("EventWiring.aspx", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The sweep asks the registered packs. Calling the handler directly means no host has built a
    /// registry, so this stands in for one.
    /// </summary>
    private static async Task UseWebFormsAsync()
    {
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.AspxProjectFile);
    }

    [Fact]
    public void CapabilityFollowsTheConfiguredScope()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "off";
        Assert.Equal("off", LspFeatureOptions.WorkspaceDiagnosticsScope);

        LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";
        Assert.NotEqual("off", LspFeatureOptions.WorkspaceDiagnosticsScope);
    }
}
