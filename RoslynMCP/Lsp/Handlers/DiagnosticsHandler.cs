using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>Diagnostics for one document — shared by push (<see cref="DiagnosticsPublisher"/>)
/// and pull (textDocument/diagnostic). Compiler diagnostics are cheap and always computed;
/// analyzer diagnostics ride the <see cref="AnalyzerDiagnosticCache"/> so they never block a
/// keystroke or a pull.</summary>
internal static class DiagnosticsHandler
{
    /// <summary>Compiler diagnostics only — the fast pass.</summary>
    public static async Task<Protocol.Diagnostic[]> ComputeAsync(
        string filePath, CancellationToken ct, LanguageSession? languages = null)
    {
        // Decompiled source is a reading aid, not a compilable file: it is a best-effort
        // reconstruction that legitimately references internals and compiler-generated names, and
        // squiggling it reports on the decompiler rather than on the user's code. Visual Studio
        // and Rider do not diagnose it either.
        if (Services.DecompiledSourceService.IsDecompiledPath(filePath))
            return Array.Empty<Protocol.Diagnostic>();

        // A web.config belongs to no project in Roslyn's sense, so it has to be claimed before the
        // document resolve that would otherwise return null and report nothing about it.
        if (BindingRedirectHandler.IsConfigPath(filePath))
            return await BindingRedirectHandler.DiagnosticsAsync(filePath, ct);

        if (LanguageScope.Of(languages).Resolve<ILanguageDiagnosticProvider>(filePath) is { } pack)
            return await pack.DiagnosticsAsync(filePath, ct);

        var document = await LspDocumentResolver.ResolveAsync(filePath, ct);
        if (document is null)
            return Array.Empty<Protocol.Diagnostic>();

        return WithEmbedded(
            ToProtocol(await CompilerDiagnosticsAsync(document, ct)),
            await EmbeddedDiagnosticsAsync(document, ct));
    }

    /// <summary>Compiler plus analyzer diagnostics, computing the analyzer pass if it is not
    /// already cached. The slow pass.</summary>
    public static async Task<Protocol.Diagnostic[]> ComputeWithAnalyzersAsync(
        string filePath, CancellationToken ct, LanguageSession? languages = null)
    {
        // Decompiled source is a reading aid, not a compilable file: it is a best-effort
        // reconstruction that legitimately references internals and compiler-generated names, and
        // squiggling it reports on the decompiler rather than on the user's code. Visual Studio
        // and Rider do not diagnose it either.
        if (Services.DecompiledSourceService.IsDecompiledPath(filePath))
            return Array.Empty<Protocol.Diagnostic>();

        // A web.config belongs to no project in Roslyn's sense, so it has to be claimed before the
        // document resolve that would otherwise return null and report nothing about it.
        if (BindingRedirectHandler.IsConfigPath(filePath))
            return await BindingRedirectHandler.DiagnosticsAsync(filePath, ct);

        if (LanguageScope.Of(languages).Resolve<ILanguageDiagnosticProvider>(filePath) is { } pack)
            return await pack.DiagnosticsAsync(filePath, ct);

        var document = await LspDocumentResolver.ResolveAsync(filePath, ct);
        if (document is null)
            return Array.Empty<Protocol.Diagnostic>();

        var compiler = await CompilerDiagnosticsAsync(document, ct);
        var analyzer = await AnalyzerDiagnosticCache.GetOrComputeAsync(document, ct);
        return WithEmbedded(
            ToProtocol(Merge(compiler, analyzer)),
            await EmbeddedDiagnosticsAsync(document, ct));
    }

    /// <summary>
    /// Problems reported by the languages that live inside string literals — a malformed route
    /// template, an unparseable embedded query. Roslyn binds nothing inside a literal, so nobody
    /// else has anything to say about one.
    /// </summary>
    /// <remarks>
    /// The gate is the registered set, not the document: with no embedded language registered this
    /// returns before the document is touched, which is what keeps a walk over every token off a
    /// path that also runs on every keystroke. Beyond that gate the walk is the price of the
    /// feature — the detector has to see each literal to know whether anyone claims it.
    /// </remarks>
    internal static async Task<IReadOnlyList<Protocol.Diagnostic>> EmbeddedDiagnosticsAsync(
        Document document, CancellationToken ct)
    {
        var embedded = RoslynEmbeddedLanguages.Current;
        if (embedded.IsEmpty)
            return [];

        var results = new List<Protocol.Diagnostic>();
        foreach (var context in await embedded.DetectAllAsync(document, ct))
        {
            if (context.Language is IEmbeddedDiagnosticProvider provider)
                results.AddRange(await provider.DiagnosticsAsync(context, ct));
        }

        return results;
    }

    private static Protocol.Diagnostic[] WithEmbedded(
        Protocol.Diagnostic[] diagnostics, IReadOnlyList<Protocol.Diagnostic> embedded) =>
        embedded.Count == 0 ? diagnostics : [.. diagnostics, .. embedded];

    private static async Task<ImmutableArray<RoslynDiagnostic>> CompilerDiagnosticsAsync(
        Document document, CancellationToken ct)
    {
        var model = await document.GetSemanticModelAsync(ct);
        return model is null
            ? ImmutableArray<RoslynDiagnostic>.Empty
            : model.GetDiagnostics(cancellationToken: ct);
    }

    /// <summary>Union of both sources, deduplicated on id + span: an analyzer reporting what the
    /// compiler already reported must not draw two squiggles.</summary>
    internal static IEnumerable<RoslynDiagnostic> Merge(
        IEnumerable<RoslynDiagnostic> compiler, IEnumerable<RoslynDiagnostic> analyzer) =>
        compiler.Concat(analyzer)
            .GroupBy(d => (d.Id, d.Location.SourceSpan))
            .Select(g => g.First());

    private static Protocol.Diagnostic[] ToProtocol(IEnumerable<RoslynDiagnostic> diagnostics) =>
        diagnostics
            .Where(d => d.Severity != DiagnosticSeverity.Hidden && d.Location.IsInSource)
            .Select(d => new Protocol.Diagnostic(
                LspConverters.ToRange(d.Location.GetLineSpan().Span),
                LspConverters.ToLspSeverity(d.Severity),
                d.Id,
                "roslyn-sense",
                d.GetMessage()))
            .ToArray();

    /// <summary>Pull with resultId versioning: the id encodes the document text checksum and
    /// the project's dependent-semantic version, so an unchanged world answers "unchanged"
    /// without recomputing diagnostics.
    /// Analyzer diagnostics are served from cache only — a miss returns compiler diagnostics
    /// immediately and computes in the background, then asks the client to re-pull. Blocking a
    /// pull on analyzers would make every first request feel like a hang.</summary>
    public static async Task<object> PullAsync(
        DocumentDiagnosticParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);

        // A pack's diagnostics come from its own parser and are cheap enough to answer in full
        // every time; there is no analyzer phase behind them to version against.
        if (LanguageScope.Of(languages).Resolve<ILanguageDiagnosticProvider>(p.TextDocument.Uri) is { } pack)
            return new FullDocumentDiagnosticReport("full", await pack.DiagnosticsAsync(path, ct));

        var document = await LspDocumentResolver.ResolveAsync(path, ct);
        if (document is null)
            return new FullDocumentDiagnosticReport("full", Array.Empty<Protocol.Diagnostic>());

        string? version = await AnalyzerDiagnosticCache.GetVersionAsync(document, ct);
        var analyzer = AnalyzerDiagnosticCache.TryGet(document, version);
        bool analyzersPending = LspFeatureOptions.AnalyzerDiagnostics &&
            !AnalyzerDiagnosticCache.IsComputed(document, version);

        // Nothing for this exact version yet: show the last analysis of this document rather than
        // dropping its squiggles on the floor. A keystroke moves the project's semantic version, so
        // this miss happens in every other open file in the project on every edit — reporting an
        // empty set is what made every warning in the window blink out and come back.
        if (analyzersPending && analyzer.IsEmpty)
            analyzer = AnalyzerDiagnosticCache.TryGetAnyVersion(document, version);

        // The resultId distinguishes "compiler only" from "compiler + analyzers" for the same
        // text; without that, the follow-up pull after the background pass answers "unchanged"
        // and the analyzer squiggles never appear.
        string? resultId = version is null ? null : $"{version}:{(analyzersPending ? "c" : "a")}";
        if (resultId is not null && p.PreviousResultId == resultId)
            return new UnchangedDocumentDiagnosticReport("unchanged", resultId);

        var compiler = await CompilerDiagnosticsAsync(document, ct);

        // Only when there is a version to cache against. A null-version pass bypasses the cache
        // entirely, so it can never satisfy the next request — it would recompute, ask for a
        // refresh, be re-pulled, and recompute again, forever, delivering nothing.
        if (analyzersPending && version is not null)
            ComputeAnalyzersInBackground(document);

        return new FullDocumentDiagnosticReport(
            "full",
            WithEmbedded(
                ToProtocol(Merge(compiler, analyzer)),
                await EmbeddedDiagnosticsAsync(document, ct)))
        {
            ResultId = resultId,
        };
    }

    /// <summary>At most one pass per document, and only so many at once.</summary>
    /// <remarks>
    /// Restoring a session of forty tabs fires forty of these at once, each a full analyzer run on
    /// the same thread pool the message loop uses. The contention makes the analyzer timeout more
    /// likely to trip, and every timeout costs another wasted round trip — so the cost feeds
    /// itself. The sweep's own recompute has been bounded this way for several rounds; this is the
    /// same bound on the path that actually runs when a folder is opened.
    /// </remarks>
    private static readonly SemaphoreSlim s_backgroundSlots =
        new(Math.Max(1, Environment.ProcessorCount / 4));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<DocumentId, byte> s_backgroundRuns = new();

    private static void ComputeAnalyzersInBackground(Document document)
    {
        if (!s_backgroundRuns.TryAdd(document.Id, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await s_backgroundSlots.WaitAsync();
                try
                {
                    await AnalyzerDiagnosticCache.GetOrComputeAsync(document, CancellationToken.None);
                }
                finally
                {
                    s_backgroundSlots.Release();
                }

                // Refresh whenever the pass stored a result, and never when it did not.
                //
                // Stored: the pull that scheduled this published a report tagged "c" — compiler
                // only — precisely so the follow-up would not be answered "unchanged", and the
                // client is holding that id with no reason to ask again unless told. So the
                // refresh goes out whether or not the findings themselves moved; gating on that
                // lost diagnostics, because a keystroke leaving an unrelated warning's span
                // untouched blanks it on the "c" report and then decides there is nothing to say.
                //
                // Not stored — a timeout, or a run overtaken by a newer version: the result id
                // cannot have moved, so the re-pull could only answer "unchanged", at the price of
                // a full workspace sweep.
                //
                // Coalesced rather than immediate, which is what bounds the cost: a refresh buys a
                // re-pull of every open document plus a sweep, and this fires at most once per
                // document per version.
                string? version = await AnalyzerDiagnosticCache.GetVersionAsync(
                    document, CancellationToken.None);

                if (AnalyzerDiagnosticCache.LastComputeStored(document, version))
                    LspSessionRegistry.ScheduleRefresh(RefreshKind.Diagnostics);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Lsp] Background analyzers for '{document.Name}' failed: {ex.Message}");
            }
            finally
            {
                s_backgroundRuns.TryRemove(document.Id, out _);
            }
        });
    }
}
