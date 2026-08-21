using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
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

    // ---- Incremental member-edit analysis ----

    /// <summary>Three members, so that widening the compiler's span to the declarations either
    /// side of the edit still leaves one whose findings can only come from the previous pass.</summary>
    private const string ThreeMembers = """
        namespace SampleProject;

        public class Warnings
        {
            public void First()
            {
                int x = 42;
            }

            public int Increment(int value)
            {
                return value + 1;
            }

            public void Last()
            {
                int later = 7;
            }
        }
        """;

    private const string ThreeMembersWithUnusedUsing = "using System.Text;\r\n\r\n" + ThreeMembers;

    /// <summary>A body edit: one line added inside the first member, nothing else touched.</summary>
    private static Document InsertLineAfter(Document document, SourceText text, string anchor, string inserted)
    {
        int at = text.ToString().IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(at >= 0, $"anchor '{anchor}' not found");

        var line = text.Lines.GetLineFromPosition(at);

        // WithChanges, not SourceText.From: the change lineage is what lets Roslyn's differ see a
        // single edit rather than a wholesale replacement — the same distinction the LSP didChange
        // path makes between a ranged content change and a full-document one.
        return document.WithText(
            text.WithChanges(new TextChange(new TextSpan(line.End, 0), Environment.NewLine + inserted)));
    }

    private static async Task<Document> BaselineAsync(string source)
    {
        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.WarningsFile);
        return document.WithText(SourceText.From(source));
    }

    private static (string Id, string Text, int Line)[] Shape(
        IEnumerable<Microsoft.CodeAnalysis.Diagnostic> diagnostics, SourceText text) =>
        [.. diagnostics
            .Where(d => d.Location.IsInSource)
            .Select(d => (
                d.Id,
                Text: text.ToString(d.Location.SourceSpan),
                Line: d.Location.GetLineSpan().StartLinePosition.Line))
            .OrderBy(d => d.Line).ThenBy(d => d.Id, StringComparer.Ordinal)];

    /// <summary>
    /// A one-member edit is analysed as one member, and says exactly what a whole-file pass would.
    /// </summary>
    /// <remarks>
    /// Asserted against the whole-file answer rather than against a hand-written expectation,
    /// because the shortcut's only permitted difference from the long way round is its cost. The
    /// splice counter is what proves the shortcut was the path taken — nothing else about the
    /// result can tell.
    /// </remarks>
    [Fact]
    public async Task ASingleMemberEditSaysWhatTheWholeFilePassWouldHaveSaid()
    {
        var baseline = await BaselineAsync(ThreeMembers);
        var baseText = await baseline.GetTextAsync();
        var edited = InsertLineAfter(baseline, baseText, "int x = 42;", "        int extra = 1;");
        var editedText = await edited.GetTextAsync();

        MemberEditAnalysis.Enabled = false;
        try
        {
            AnalyzerDiagnosticCache.Clear();
            await AnalyzerDiagnosticCache.GetOrComputeAsync(baseline, default);
            await CompilerDiagnosticCache.GetOrComputeAsync(baseline, default);
            var wholeFileAnalyzer = await AnalyzerDiagnosticCache.GetOrComputeAsync(edited, default);
            var wholeFileCompiler = await CompilerDiagnosticCache.GetOrComputeAsync(edited, default);

            MemberEditAnalysis.Enabled = true;
            AnalyzerDiagnosticCache.Clear();
            await AnalyzerDiagnosticCache.GetOrComputeAsync(baseline, default);
            await CompilerDiagnosticCache.GetOrComputeAsync(baseline, default);

            MemberEditAnalysis.ResetCounters();
            var splicedAnalyzer = await AnalyzerDiagnosticCache.GetOrComputeAsync(edited, default);
            var splicedCompiler = await CompilerDiagnosticCache.GetOrComputeAsync(edited, default);

            Assert.True(MemberEditAnalysis.Splices > 0, "the edit did not take the incremental path");
            Assert.Equal(Shape(wholeFileCompiler.Compiler, editedText), Shape(splicedCompiler.Compiler, editedText));
            Assert.True(
                AnalyzerDiagnosticCache.SameFindings(wholeFileAnalyzer, splicedAnalyzer),
                "analyzer findings differ between the incremental and the whole-file pass");
        }
        finally
        {
            MemberEditAnalysis.Enabled = true;
            AnalyzerDiagnosticCache.Clear();
        }
    }

    /// <summary>
    /// A finding below the edited member comes forward from the previous result, on the line the
    /// edit pushed it onto.
    /// </summary>
    /// <remarks>
    /// The whole difficulty of splicing. The carried diagnostics were resolved against the previous
    /// syntax tree, so serving them unmoved draws squiggles a line short of the code they describe;
    /// this is the case that catches a remap that silently does nothing.
    /// </remarks>
    [Fact]
    public async Task ADiagnosticBelowTheEditedMemberMovesDownWithTheText()
    {
        var baseline = await BaselineAsync(ThreeMembers);
        var baseText = await baseline.GetTextAsync();

        AnalyzerDiagnosticCache.Clear();
        var before = await CompilerDiagnosticCache.GetOrComputeAsync(baseline, default);
        int lineBefore = Assert.Single(
            before.Compiler.Where(d => d.Id == "CS0219" && baseText.ToString(d.Location.SourceSpan) == "later"))
            .Location.GetLineSpan().StartLinePosition.Line;

        var edited = InsertLineAfter(baseline, baseText, "int x = 42;", "        int extra = 1;");
        var editedText = await edited.GetTextAsync();

        MemberEditAnalysis.ResetCounters();
        CompilerDiagnosticCache.ResetComputationCounter();
        var after = await CompilerDiagnosticCache.GetOrComputeAsync(edited, default);

        Assert.Equal(1L, CompilerDiagnosticCache.SpanBinds);

        var moved = Assert.Single(
            after.Compiler.Where(d => d.Id == "CS0219" && editedText.ToString(d.Location.SourceSpan) == "later"));
        Assert.Equal(lineBefore + 1, moved.Location.GetLineSpan().StartLinePosition.Line);

        // And the edit's own new finding is there, from the fresh span pass.
        Assert.Contains(
            after.Compiler,
            d => d.Id == "CS0219" && editedText.ToString(d.Location.SourceSpan) == "extra");
    }

    /// <summary>
    /// An unnecessary using stays greyed through a span pass.
    /// </summary>
    /// <remarks>
    /// The compiler does not look at using directives when it is handed a span, so CS8019 cannot be
    /// produced by an incremental pass at all — it can only be carried forward from the last
    /// whole-file bind. Without that carry the fade blinked off on every keystroke inside a method
    /// and back on at the next full analysis.
    /// </remarks>
    [Fact]
    public async Task AnUnnecessaryUsingSurvivesASpanPass()
    {
        var baseline = await BaselineAsync(ThreeMembersWithUnusedUsing);
        var baseText = await baseline.GetTextAsync();

        AnalyzerDiagnosticCache.Clear();
        CompilerDiagnosticCache.ResetComputationCounter();
        var before = await CompilerDiagnosticCache.GetOrComputeAsync(baseline, default);
        var unused = Assert.Single(before.Compiler.Where(d => d.Id == "CS8019"));
        int line = unused.Location.GetLineSpan().StartLinePosition.Line;

        var edited = InsertLineAfter(baseline, baseText, "int x = 42;", "        int extra = 1;");

        MemberEditAnalysis.ResetCounters();
        var after = await CompilerDiagnosticCache.GetOrComputeAsync(edited, default);

        Assert.Equal(1L, CompilerDiagnosticCache.SpanBinds);
        var carried = Assert.Single(after.Compiler.Where(d => d.Id == "CS8019"));

        // Above the edit, so its span does not move — and the message survives the rebuild the
        // carry needs, which is what would break if it were reconstructed from the descriptor's
        // unformatted template.
        Assert.Equal(line, carried.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Equal(unused.GetMessage(), carried.GetMessage());
    }

    /// <summary>
    /// A signature change is analysed whole-file.
    /// </summary>
    /// <remarks>
    /// Two guards refuse it independently, and either alone would be enough: the version's semantic
    /// half moves the moment a declaration does, and Roslyn's differ returns null for a member
    /// whose signature is not equivalent to the one it replaced. The result is the one that
    /// matters — every other file in the project has been invalidated too, and splicing into a
    /// result computed against the old signature would keep reporting against it.
    /// </remarks>
    [Fact]
    public async Task ASignatureEditFallsBackToTheWholeFile()
    {
        var baseline = await BaselineAsync(ThreeMembers);
        var baseText = await baseline.GetTextAsync();

        AnalyzerDiagnosticCache.Clear();
        await CompilerDiagnosticCache.GetOrComputeAsync(baseline, default);
        await AnalyzerDiagnosticCache.GetOrComputeAsync(baseline, default);

        int at = baseText.ToString().IndexOf("public int Increment(int value)", StringComparison.Ordinal);
        var edited = baseline.WithText(baseText.WithChanges(
            new TextChange(new TextSpan(at, "public int Increment(int value)".Length),
                "public int Increment(int value, int unusedParameter)")));
        var editedText = await edited.GetTextAsync();

        MemberEditAnalysis.ResetCounters();
        CompilerDiagnosticCache.ResetComputationCounter();

        string? version = await AnalyzerDiagnosticCache.GetVersionAsync(edited, default);
        Assert.NotNull(version);

        var compiler = await CompilerDiagnosticCache.GetOrComputeAsync(edited, default);

        Assert.Null(await MemberEditAnalysis.TryComputeAsync(edited, version!, default));
        Assert.Equal(0L, MemberEditAnalysis.Splices);
        Assert.Equal(0L, CompilerDiagnosticCache.SpanBinds);

        // And the whole-file answer is intact: the untouched members still report.
        Assert.Contains(
            compiler.Compiler,
            d => d.Id == "CS0219" && editedText.ToString(d.Location.SourceSpan) == "later");
    }

    // ---- Cost buckets ----

    /// <summary>
    /// A pathological analyzer costs the file its own findings and nothing else.
    /// </summary>
    /// <remarks>
    /// One budget for the whole set meant the reverse: the semantic half timing out discarded every
    /// analyzer's semantic results, returned <c>Failed</c>, and so stored nothing — which left the
    /// cheap code-style squiggles missing and the whole pass to be run again, and time out again,
    /// on the next request. The expensive bucket now runs on its own clock, and losing it is a
    /// local loss.
    /// </remarks>
    [Fact]
    public async Task AnExpensiveAnalyzerTimingOutStillPublishesAndCachesTheCheapOnes()
    {
        var baseline = await BaselineAsync(ThreeMembers);
        var blocker = new BlocksUntilCancelledAnalyzer();

        AnalyzerService.AdditionalAnalyzersForTesting = [blocker];
        AnalyzerService.SlowBudgetOverrideForTesting = TimeSpan.FromMilliseconds(250);
        try
        {
            AnalyzerDiagnosticCache.Clear();

            var compilation = await baseline.Project.GetCompilationAsync();
            var analyzers = AnalyzerService.GetAnalyzersFor(baseline.Project);
            var driver = AnalyzerService.DriverForTesting(compilation!, baseline.Project, analyzers);
            var (fast, slow) = await AnalyzerService.BucketsForTesting(driver, analyzers);

            // A SemanticModel action is one of the two shapes that earns the slow bucket.
            Assert.Contains(blocker, slow);
            Assert.DoesNotContain(blocker, fast);
            Assert.Equal(analyzers.Length, fast.Length + slow.Length);

            var run = await AnalyzerService.RunDocumentAnalyzersWithStatusAsync(baseline, default);

            Assert.False(run.Failed);
            Assert.DoesNotContain(run.Diagnostics, d => d.Id == BlocksUntilCancelledAnalyzer.Id);

            // The cheap code-style rules, which used to go down with the expensive one.
            Assert.NotEmpty(run.Diagnostics);
            Assert.Contains(run.Diagnostics, d => d.Id.StartsWith("IDE", StringComparison.Ordinal));

            // Failed = false is what lets it be cached, which is the half of the bug that made the
            // starvation repeat rather than merely happen.
            string? version = await AnalyzerDiagnosticCache.GetVersionAsync(baseline, default);
            await AnalyzerDiagnosticCache.GetOrComputeAsync(baseline, default);
            Assert.True(AnalyzerDiagnosticCache.IsComputed(baseline, version));
        }
        finally
        {
            AnalyzerService.AdditionalAnalyzersForTesting = ImmutableArray<DiagnosticAnalyzer>.Empty;
            AnalyzerService.SlowBudgetOverrideForTesting = null;
            AnalyzerDiagnosticCache.Clear();
        }
    }

    /// <summary>The buckets partition the set, and the ordinary rules are all in the cheap one.</summary>
    [Fact]
    public async Task TheAnalyzerSetIsPartitionedIntoCostBuckets()
    {
        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.WarningsFile);
        var compilation = await document.Project.GetCompilationAsync();
        var analyzers = AnalyzerService.GetAnalyzersFor(document.Project);
        Assert.NotEmpty(analyzers);

        var driver = AnalyzerService.DriverForTesting(compilation!, document.Project, analyzers);
        var (fast, slow) = await AnalyzerService.BucketsForTesting(driver, analyzers);

        Assert.Equal(analyzers.Length, fast.Length + slow.Length);
        Assert.Empty(fast.Intersect(slow));
        Assert.NotEmpty(fast);

        // The expensive shapes are a small minority of a real analyzer set — 25 of 358 for this
        // fixture, which is the whole point: one of them must not be able to cost the other 333
        // their results.
        Assert.True(slow.Length < fast.Length, $"{slow.Length} slow of {analyzers.Length}");

        // Classification is cached on the analyzer instance, so asking twice must agree.
        var (fastAgain, slowAgain) = await AnalyzerService.BucketsForTesting(driver, analyzers);
        Assert.Equal(Names(fast), Names(fastAgain));
        Assert.Equal(Names(slow), Names(slowAgain));

        // The compiler analyzer is never de-prioritised, however it registers.
        Assert.DoesNotContain(slow, AnalyzerService.IsCompilerAnalyzerForTesting);

        static string Names(ImmutableArray<DiagnosticAnalyzer> a) =>
            string.Join(", ", a.Select(x => x.GetType().Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Registers the one action shape that guarantees a slot in the expensive bucket, and holds it
    /// until its budget cancels it.
    /// </summary>
    /// <remarks>
    /// Waits on the cancellation token's handle rather than for a duration: it finishes the instant
    /// the slow budget fires and not a moment later, so the test measures the budget rather than
    /// racing it.
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class BlocksUntilCancelledAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "RSTEST001";

        private static readonly DiagnosticDescriptor s_rule = new(
            Id, "Never finishes", "Never finishes", "Test", DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [s_rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSemanticModelAction(static c =>
            {
                c.CancellationToken.WaitHandle.WaitOne();
                c.CancellationToken.ThrowIfCancellationRequested();
                c.ReportDiagnostic(Microsoft.CodeAnalysis.Diagnostic.Create(
                    s_rule, c.SemanticModel.SyntaxTree.GetRoot().GetLocation()));
            });
        }
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
