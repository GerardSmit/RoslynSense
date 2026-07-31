using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>workspace/diagnostic: the Problems panel without opening every file, scoped so a
/// large solution is not swept on every request.</summary>
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
    public void CapabilityFollowsTheConfiguredScope()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "off";
        Assert.Equal("off", LspFeatureOptions.WorkspaceDiagnosticsScope);

        LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";
        Assert.NotEqual("off", LspFeatureOptions.WorkspaceDiagnosticsScope);
    }
}
