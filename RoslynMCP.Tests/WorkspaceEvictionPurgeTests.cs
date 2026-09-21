using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Lsp;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// A workspace that is evicted takes everything keyed by its DocumentIds with it, and background
/// work queued against it runs on what the workspace holds now — or not at all.
/// </summary>
/// <remarks>
/// Both came from one daemon: two days and twenty solution reloads in, it held 900 compilations
/// and 14 GB. The diagnostic caches kept every reload's entries (findings hold symbols, symbols
/// hold their compilation), and the sweep's recompute queue held 5,000 Document snapshots waiting
/// for a handful of slots.
/// </remarks>
[Collection(SharedState.Name)]
public sealed class WorkspaceEvictionPurgeTests : IDisposable
{
    private readonly AdhocWorkspace _workspace = new();
    private readonly bool _analyzers = LspFeatureOptions.AnalyzerDiagnostics;
    private readonly bool _codeStyle = LspFeatureOptions.CodeStyleDiagnostics;

    public WorkspaceEvictionPurgeTests()
    {
        LspFeatureOptions.AnalyzerDiagnostics = true;
        LspFeatureOptions.CodeStyleDiagnostics = false;
        AnalyzerDiagnosticCache.Clear();
    }

    public void Dispose()
    {
        AnalyzerDiagnosticCache.Clear();
        LspFeatureOptions.AnalyzerDiagnostics = _analyzers;
        LspFeatureOptions.CodeStyleDiagnostics = _codeStyle;
        _workspace.Dispose();
    }

    [Fact]
    public async Task EvictingProjectsDropsBothCachesForTheirDocumentsOnly()
    {
        var evicted = CreateDocument("Evicted");
        var kept = CreateDocument("Kept");
        foreach (var document in new[] { evicted, kept })
        {
            await CompilerDiagnosticCache.GetOrComputeAsync(document, default);
            await AnalyzerDiagnosticCache.GetOrComputeAsync(document, default);
        }
        long compilerBinds = CompilerDiagnosticCache.Computations;
        var version = await AnalyzerDiagnosticCache.GetVersionAsync(kept, default);
        Assert.True(AnalyzerDiagnosticCache.HasStoredFindings(kept, version!));

        AnalyzerDiagnosticCache.EvictProjects([evicted.Project.Id]);

        // The evicted document misses both caches; the other project's document still hits.
        Assert.False(AnalyzerDiagnosticCache.HasStoredFindings(
            evicted, (await AnalyzerDiagnosticCache.GetVersionAsync(evicted, default))!));
        Assert.True(AnalyzerDiagnosticCache.HasStoredFindings(kept, version!));
        await CompilerDiagnosticCache.GetOrComputeAsync(kept, default);
        Assert.Equal(compilerBinds, CompilerDiagnosticCache.Computations);
        await CompilerDiagnosticCache.GetOrComputeAsync(evicted, default);
        Assert.Equal(compilerBinds + 1, CompilerDiagnosticCache.Computations);
    }

    [Fact]
    public async Task EvictingProjectsDropsTheProjectWideCacheToo()
    {
        var document = CreateDocument("Wide");
        var project = _workspace.CurrentSolution.GetProject(document.Project.Id)!;
        await ProjectWideDiagnosticCache.RefreshAsync(project, default);
        var version = await ProjectWideDiagnosticCache.GetVersionAsync(project, default);
        Assert.True(ProjectWideDiagnosticCache.IsComputed(project, version));

        ProjectWideDiagnosticCache.EvictProjects([project.Id]);

        Assert.False(ProjectWideDiagnosticCache.IsComputed(project, version));
    }

    [Fact]
    public async Task QueuedRecomputeResolvesTheDocumentTheWorkspaceHoldsNow()
    {
        var queued = CreateDocument("Live");
        var edited = SourceText.From("class C { private int stillUnused; }");
        Assert.True(_workspace.TryApplyChanges(queued.WithText(edited).Project.Solution));

        var current = LiveSnapshot.Document(_workspace, queued.Id);

        Assert.NotNull(current);
        Assert.NotSame(queued, current);
        Assert.True((await current!.GetTextAsync()).ContentEquals(edited));
    }

    [Fact]
    public void QueuedRecomputeSkipsADocumentThatLeftTheWorkspace()
    {
        var queued = CreateDocument("Removed");
        Assert.True(_workspace.TryApplyChanges(queued.Project.RemoveDocument(queued.Id).Solution));

        Assert.Null(LiveSnapshot.Document(_workspace, queued.Id));
        Assert.NotNull(LiveSnapshot.Project(_workspace, queued.Project.Id));
    }

    [Fact]
    public void QueuedRecomputeSkipsAnEvictedWorkspaceEvenThoughItStillAnswers()
    {
        var queued = CreateDocument("Stale");
        WorkspaceService.MarkEvictedForTesting(_workspace);

        // The workspace still holds the document — that is the trap, not a precondition.
        Assert.NotNull(_workspace.CurrentSolution.GetDocument(queued.Id));
        Assert.Null(LiveSnapshot.Document(_workspace, queued.Id));
        Assert.Null(LiveSnapshot.Project(_workspace, queued.Project.Id));
    }

    /// <summary>Applied to the workspace, not forked from it: the live-document tests look the
    /// document up through <c>CurrentSolution</c>.</summary>
    private Document CreateDocument(string projectName)
    {
        var project = _workspace.AddProject(projectName, LanguageNames.CSharp);
        Assert.True(_workspace.TryApplyChanges(project
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .Solution));
        return _workspace.AddDocument(project.Id, $"{projectName}.cs", SourceText.From("class C { private int unused; }"));
    }
}
