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

    /// <summary>
    /// An open document selects its project for the sweep, and the sweep reports the whole project
    /// — the open file included.
    /// </summary>
    /// <remarks>
    /// Open files were once left out, because the sweep read analyzer results from cache only and
    /// would overwrite the document pull's richer answer with the compiler-only subset. That is no
    /// longer how it behaves: the sweep serves the same cached analyzer results the pull serves and
    /// queues a recompute when they are stale, so neither report downgrades the other.
    ///
    /// Skipping them was also unsound. Whether a file is open is process-wide, but the editors
    /// sharing this daemon each hold their own result ids — so a file open in one window was
    /// skipped for every window, and only the window that had it open ever discarded its
    /// diagnostics. The others were told "unchanged" about a file they had nothing for.
    /// </remarks>
    [Fact]
    public async Task OpenProjectsScopeReportsTheWholeProjectIncludingTheOpenDocument()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "openProjects";
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.BrokenProjectFile);

        string path = FixturePaths.BrokenSemanticFile;
        OpenDocumentStore.Open(_session, path, SourceText.From(await File.ReadAllTextAsync(path)), 1);

        var report = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        Assert.NotEmpty(report.Items);
        var full = report.Items.OfType<WorkspaceFullDocumentDiagnosticReport>().ToList();

        // The closed sibling is what the sweep is for: nobody opened it, so nothing else would
        // ever report it.
        Assert.Contains(full, r => r.Uri.EndsWith("BrokenSyntax.cs", StringComparison.OrdinalIgnoreCase));

        // And the open one is reported too, rather than being silently nobody's job.
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

    /// <summary>
    /// A multi-targeted project reports each of its files once, not once per framework.
    /// </summary>
    /// <remarks>
    /// Several <see cref="Microsoft.CodeAnalysis.Project"/>s share one project file and one
    /// document set, and the editor keeps a single result id per file. Reporting per framework
    /// meant it stored whichever id arrived last — the order is whatever the parallel sweep
    /// happened to finish in — so the other framework's id mismatched on the next sweep, that file
    /// was re-bound and re-reported for the rest of the session, and its diagnostics alternated
    /// between the two. Merging under one id built from all of them is what settles it.
    /// </remarks>
    [Fact]
    public async Task AMultiTargetedProjectReportsEachFileOnce()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.CpmMultiTfmProjectFile);

        var report = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        var uris = report.Items
            .Select(item => item switch
            {
                WorkspaceFullDocumentDiagnosticReport full => full.Uri,
                WorkspaceUnchangedDocumentDiagnosticReport unchanged => unchanged.Uri,
                _ => null,
            })
            .Where(uri => uri is not null)
            .ToList();

        Assert.NotEmpty(uris);
        Assert.Equal(uris.Count, uris.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// A sweep that changes nothing answers "unchanged" and never touches a compilation.
    /// </summary>
    /// <remarks>
    /// This is the economy the whole sweep rests on: the editor re-pulls after anything that could
    /// reach another file, so almost every sweep has nothing to say. Computing versions first and
    /// binding only what moved is what makes that free — a merge that collected diagnostics before
    /// comparing ids did the most expensive thing a compilation offers and then discarded it.
    /// </remarks>
    [Fact]
    public async Task ASecondSweepOfAMultiTargetedProjectAnswersUnchanged()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.CpmMultiTfmProjectFile);

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

        // Every id handed back is honoured. One framework's id going unrecognised is what made a
        // multi-targeted project re-bind on every sweep forever.
        foreach (var id in previous)
        {
            Assert.Contains(
                second.Items.OfType<WorkspaceUnchangedDocumentDiagnosticReport>(),
                r => string.Equals(r.Uri, id.Uri, StringComparison.OrdinalIgnoreCase));
        }
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
