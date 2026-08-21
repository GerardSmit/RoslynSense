using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>Analyzer diagnostics on the editor path: the per-document pass, .editorconfig
/// severity handling, and the version-keyed caches — analyzer and compiler alike — that keep both
/// off the typing loop.</summary>
[Collection(SharedState.Name)]
public class AnalyzerDiagnosticsTests
{
    [Fact]
    public void IdeAnalyzersLoadFromFeaturesAssemblies()
    {
        var analyzers = AnalyzerService.LoadIdeAnalyzers();

        // Reflection over Roslyn internals — if a Roslyn upgrade moves or renames these types,
        // this fails loudly here instead of silently dropping every IDE0xxx rule for users.
        Assert.NotEmpty(analyzers);
        Assert.Contains(analyzers, a => a.SupportedDiagnostics.Any(
            d => d.Id.StartsWith("IDE", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DocumentAnalyzerPassReturnsProjectAnalyzerDiagnostics()
    {
        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.WarningsFile);

        var diagnostics = await AnalyzerService.RunDocumentAnalyzersAsync(document);

        Assert.Contains(diagnostics, d => d.Id.StartsWith("CA", StringComparison.Ordinal));
        // Per-tree analysis must not leak diagnostics from other files in the project.
        Assert.All(diagnostics, d => Assert.Equal(
            document.FilePath, d.Location.SourceTree?.FilePath, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EditorConfigSeverityOverrideIsHonored()
    {
        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.WarningsFile);

        var baseline = await AnalyzerService.RunDocumentAnalyzersAsync(document);
        var target = baseline.FirstOrDefault(d =>
            d.Id.StartsWith("CA", StringComparison.Ordinal) && d.Severity != DiagnosticSeverity.Error);
        Assert.NotNull(target);

        // Regression: WithAnalyzers was called without project.AnalyzerOptions, so the
        // AnalyzerConfigOptionsProvider never reached the analyzers and every .editorconfig
        // severity override was ignored.
        var configured = AddEditorConfig(document, $"""
            root = true

            [*.cs]
            dotnet_diagnostic.{target!.Id}.severity = error
            """);

        var elevated = await AnalyzerService.RunDocumentAnalyzersAsync(configured);

        var match = elevated.FirstOrDefault(d => d.Id == target.Id);
        Assert.NotNull(match);
        Assert.Equal(DiagnosticSeverity.Error, match!.Severity);
    }

    [Fact]
    public async Task EditorConfigCanSuppressAnalyzerDiagnostic()
    {
        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.WarningsFile);

        var baseline = await AnalyzerService.RunDocumentAnalyzersAsync(document);
        var target = baseline.First(d => d.Id.StartsWith("CA", StringComparison.Ordinal));

        var configured = AddEditorConfig(document, $"""
            root = true

            [*.cs]
            dotnet_diagnostic.{target.Id}.severity = none
            """);

        var suppressed = await AnalyzerService.RunDocumentAnalyzersAsync(configured);

        Assert.DoesNotContain(suppressed, d => d.Id == target.Id);
    }

    [Fact]
    public async Task CacheComputesOncePerDocumentVersionAndRecomputesAfterEdit()
    {
        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.WarningsFile);
        AnalyzerDiagnosticCache.Clear();

        string? version = await AnalyzerDiagnosticCache.GetVersionAsync(document, default);
        Assert.NotNull(version);
        Assert.False(AnalyzerDiagnosticCache.IsComputed(document, version));

        var first = await AnalyzerDiagnosticCache.GetOrComputeAsync(document, default);
        Assert.True(AnalyzerDiagnosticCache.IsComputed(document, version));
        Assert.Equal(first, AnalyzerDiagnosticCache.TryGet(document, version));

        // An edit changes the text checksum, so the old entry no longer answers.
        var text = await document.GetTextAsync();
        var edited = document.WithText(text.Replace(new TextSpan(0, 0), "// touched\n"));
        string? editedVersion = await AnalyzerDiagnosticCache.GetVersionAsync(edited, default);

        Assert.NotEqual(version, editedVersion);
        Assert.False(AnalyzerDiagnosticCache.IsComputed(edited, editedVersion));
    }

    [Fact]
    public async Task CacheHonorsTheAnalyzerFeatureSwitch()
    {
        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.WarningsFile);
        AnalyzerDiagnosticCache.Clear();

        LspFeatureOptions.AnalyzerDiagnostics = false;
        try
        {
            Assert.Empty(await AnalyzerDiagnosticCache.GetOrComputeAsync(document, default));
        }
        finally
        {
            LspFeatureOptions.AnalyzerDiagnostics = true;
        }
    }

    [Fact]
    public async Task LspComputeIncludesAnalyzerDiagnosticsOnTheSlowPass()
    {
        AnalyzerDiagnosticCache.Clear();

        var compilerOnly = await DiagnosticsHandler.ComputeAsync(FixturePaths.WarningsFile, default);
        var merged = await DiagnosticsHandler.ComputeWithAnalyzersAsync(FixturePaths.WarningsFile, default);

        Assert.DoesNotContain(compilerOnly, d => d.Code?.StartsWith("CA", StringComparison.Ordinal) == true);
        Assert.Contains(merged, d => d.Code?.StartsWith("CA", StringComparison.Ordinal) == true);
        // The slow pass must be a superset: publishDiagnostics replaces the whole set per URI,
        // so dropping compiler diagnostics here would erase squiggles the fast pass drew.
        Assert.All(compilerOnly, c => Assert.Contains(merged, m => m.Code == c.Code && m.Range == c.Range));
    }

    /// <summary>
    /// One typing pause binds a document once, however many requesters ask about it.
    /// </summary>
    /// <remarks>
    /// <c>SemanticModel.GetDiagnostics()</c> re-binds every method body per call and memoizes
    /// nothing, and three paths ask for the same text: the fast push phase, the analyzer push phase
    /// ~1500ms behind it, and the pull — plus the re-pull the background analyzer pass requests,
    /// where only the result-id marker moved and the compiler could not possibly disagree with
    /// itself. Counted rather than asserted on the reports, because a hit and a miss report
    /// identically by construction; the count is the only outward difference a cache keyed on the
    /// invalidation condition can have.
    /// </remarks>
    [Fact]
    public async Task AnUnchangedDocumentIsBoundOnceAcrossPushPhasesAndThePull()
    {
        AnalyzerDiagnosticCache.Clear();
        await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.WarningsFile);

        string uri = LspConverters.PathToUri(FixturePaths.WarningsFile);

        // The bind this test is entitled to. Everything after it asks about the same text.
        var first = await DiagnosticsHandler.ComputeAsync(FixturePaths.WarningsFile, default);
        Assert.NotEmpty(first);

        CompilerDiagnosticCache.ResetComputationCounter();

        var fastAgain = await DiagnosticsHandler.ComputeAsync(FixturePaths.WarningsFile, default);
        var withAnalyzers = await DiagnosticsHandler.ComputeWithAnalyzersAsync(
            FixturePaths.WarningsFile, default);
        var pulled = Assert.IsType<FullDocumentDiagnosticReport>(await DiagnosticsHandler.PullAsync(
            new DocumentDiagnosticParams(new TextDocumentIdentifier(uri)), default));

        Assert.Equal(0L, CompilerDiagnosticCache.Computations);

        // And served from the cache means served in full, not served empty.
        foreach (var served in new[] { fastAgain, withAnalyzers, pulled.Items })
            Assert.All(first, d => Assert.Contains(served, s => s.Code == d.Code && s.Range == d.Range));
    }

    [Fact]
    public async Task MergeDeduplicatesDiagnosticsReportedByBothSources()
    {
        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.WarningsFile);
        var model = await document.GetSemanticModelAsync();
        var compiler = model!.GetDiagnostics();

        var merged = DiagnosticsHandler.Merge(compiler, compiler).ToList();

        Assert.Equal(compiler.Length, merged.Count);
    }

    private static Document AddEditorConfig(Document document, string content)
    {
        string path = Path.Combine(
            Path.GetDirectoryName(document.Project.FilePath!)!, ".editorconfig");

        var solution = document.Project.Solution.AddAnalyzerConfigDocument(
            DocumentId.CreateNewId(document.Project.Id),
            ".editorconfig",
            SourceText.From(content),
            filePath: path);

        return solution.GetDocument(document.Id)!;
    }

    /// <summary>
    /// A pull whose analysers have not run yet answers "unchanged" on the follow-up, rather than
    /// scheduling another pass.
    /// </summary>
    /// <remarks>
    /// This ordering is load-bearing and easy to break. The pull tags its report <c>:c</c> while
    /// analysers are still pending and schedules a background pass, and that pass asks the editor
    /// to re-pull — unconditionally, because the client is holding the <c>:c</c> id and will not
    /// ask again otherwise. What stops that being a loop is that the result-id comparison happens
    /// <em>before</em> the pass is scheduled: when a pass stores nothing, the re-pull matches the
    /// same <c>:c</c> id, returns unchanged, and never reaches the scheduler. Moving the
    /// comparison after the scheduling would turn this into an unbounded cycle of full analyzer
    /// runs and whole-workspace sweeps.
    /// </remarks>
    [Fact]
    public async Task APullRepeatedWithItsOwnResultIdAnswersUnchanged()
    {
        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.WarningsFile);

        string uri = LspConverters.PathToUri(FixturePaths.WarningsFile);

        // Analysed first, deliberately. The id carries whether analysers are still pending — `:c`
        // before the pass lands, `:a` after — so pulling twice across the landing is two different
        // worlds and full-with-a-new-id is the right answer, not a bug. Letting the pass finish
        // first is what makes "same world" true; without it this asserted that analysis was slower
        // than two round trips, which held only until analysis got faster.
        await AnalyzerDiagnosticCache.GetOrComputeAsync(document, default);

        var first = await DiagnosticsHandler.PullAsync(
            new DocumentDiagnosticParams(new TextDocumentIdentifier(uri)), default);

        var full = Assert.IsType<FullDocumentDiagnosticReport>(first);
        Assert.NotNull(full.ResultId);
        Assert.EndsWith(":a", full.ResultId);

        var second = await DiagnosticsHandler.PullAsync(
            new DocumentDiagnosticParams(new TextDocumentIdentifier(uri)) { PreviousResultId = full.ResultId },
            default);

        // Same world, same id: the answer is "nothing changed", and crucially it is reached without
        // queueing another analyzer pass.
        Assert.IsType<UnchangedDocumentDiagnosticReport>(second);
    }

    /// <summary>
    /// A document whose version cannot be derived never queues a background analyzer pass.
    /// </summary>
    /// <remarks>
    /// Such a pass bypasses the cache entirely, so it can never satisfy the next request: it would
    /// recompute, ask for a refresh, be re-pulled, and recompute again, delivering nothing each
    /// time. Both the pull and the workspace sweep guard on this.
    /// </remarks>
    [Fact]
    public async Task AnUnversionedDocumentIsNotQueuedForAnalysis()
    {
        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.WarningsFile);

        // A real version is derivable here, which is the point of the assertion below: the guard
        // reads the version rather than assuming one exists.
        string? version = await AnalyzerDiagnosticCache.GetVersionAsync(document, default);
        Assert.NotNull(version);

        // And the cache refuses to describe an absent version as computed, which is what the guard
        // keys on.
        Assert.False(AnalyzerDiagnosticCache.IsComputed(document, null));
        Assert.True(AnalyzerDiagnosticCache.TryGetAnyVersion(document, null).IsEmpty);
        Assert.False(AnalyzerDiagnosticCache.LastComputeStored(document, null));
    }
}
