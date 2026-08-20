using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.ExternalSource;
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
        // A dependency's source is a reading aid, not a compilable file. A decompilation
        // legitimately references internals and compiler-generated names; real framework source
        // needs partials and preprocessor symbols no single file carries. Either way the squiggles
        // would report on how the file was obtained rather than on the user's code, and neither
        // Visual Studio nor Rider diagnoses it.
        if (ExternalSourceCache.IsExternalSourcePath(filePath))
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
        // A dependency's source is a reading aid, not a compilable file. A decompilation
        // legitimately references internals and compiler-generated names; real framework source
        // needs partials and preprocessor symbols no single file carries. Either way the squiggles
        // would report on how the file was obtained rather than on the user's code, and neither
        // Visual Studio nor Rider diagnoses it.
        if (ExternalSourceCache.IsExternalSourcePath(filePath))
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

    /// <summary>
    /// The Roslyn-to-LSP shape, shared by the document pull and the workspace sweep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Internal rather than private because the sweep reported the same diagnostics through a
    /// hand-copied twin of this method, and the two drifted: whichever ran last won the URI in the
    /// editor, so a file could gain and lose its faded spans depending on whether the pull or the
    /// sweep answered for it most recently.
    /// </para>
    /// <para>
    /// Hidden diagnostics survive the filter when they carry a tag. That is the whole mechanism
    /// behind a greyed-out unused <c>using</c>: Roslyn reports IDE0005 and the unnecessary-code
    /// spans at Hidden severity, <see cref="LspConverters.ToLspSeverity"/> maps Hidden to 4 (Hint),
    /// and a Hint never enters the Problems panel — so this adds the fade without adding noise.
    /// An untagged Hidden diagnostic still has nothing to draw and is still dropped.
    /// </para>
    /// </remarks>
    internal static Protocol.Diagnostic[] ToProtocol(IEnumerable<RoslynDiagnostic> diagnostics) =>
        diagnostics
            .Where(d => d.Location.IsInSource)
            .Select(d => (Diagnostic: d, Tags: TagsFor(d)))
            .Where(pair => pair.Diagnostic.Severity != DiagnosticSeverity.Hidden || pair.Tags is not null)
            .Select(pair => new Protocol.Diagnostic(
                LspConverters.ToRange(pair.Diagnostic.Location.GetLineSpan().Span),
                LspConverters.ToLspSeverity(pair.Diagnostic.Severity),
                pair.Diagnostic.Id,
                "roslyn-sense",
                pair.Diagnostic.GetMessage())
            {
                Tags = pair.Tags,
            })
            .ToArray();

    /// <summary>
    /// The LSP tags a Roslyn diagnostic earns, or null when it earns none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived from the descriptor and the id rather than carried alongside the diagnostic, so it
    /// is deterministic for a given id and cannot disturb the <c>(Id, SourceSpan)</c> dedup in
    /// <see cref="Merge"/> or the equality <see cref="AnalyzerDiagnosticCache"/> compares findings
    /// with.
    /// </para>
    /// <para>
    /// The descriptor lookup finds the IDE analyzers, which set <c>Unnecessary</c> themselves —
    /// IDE0005 for a redundant using, IDE0051 for an unread private member. The id list finds the
    /// compiler, which does not: every compiler diagnostic carries exactly
    /// <c>[Compiler, Telemetry]</c>, because the tag is a convention of Roslyn's IDE layer and the
    /// compiler layer beneath it has never heard of it. That is not a detail worth inheriting —
    /// CS8019 <em>is</em> "unnecessary using directive", and reading its meaning off its id is how
    /// the fade works with the analyzers switched off, which is the default.
    /// </para>
    /// <para>
    /// Deliberately short, and only ids whose whole span is the thing to grey. A diagnostic that
    /// merely mentions unused code — CS1717's self-assignment, say — points at a mistake to fix
    /// rather than text to remove, and fading it would say the opposite.
    /// </para>
    /// </remarks>
    private static int[]? TagsFor(RoslynDiagnostic diagnostic)
    {
        bool unnecessary = s_unnecessaryIds.Contains(diagnostic.Id)
            || diagnostic.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary);
        bool deprecated = s_deprecatedIds.Contains(diagnostic.Id);

        if (unnecessary && deprecated)
            return [LspDiagnosticTag.Unnecessary, LspDiagnosticTag.Deprecated];

        if (unnecessary)
            return s_unnecessary;

        return deprecated ? s_deprecated : null;
    }

    /// <summary>Compiler diagnostics whose span is code that can be deleted.</summary>
    private static readonly FrozenSet<string> s_unnecessaryIds = new[]
    {
        "CS0162", // Unreachable code detected
        "CS0168", // Variable is declared but never used
        "CS0219", // Variable is assigned but its value is never used
        "CS8019", // Unnecessary using directive
        "CS8321", // Local function is declared but never used
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The obsolete family. No custom tag on any of them either.</summary>
    private static readonly FrozenSet<string> s_deprecatedIds = new[]
    {
        "CS0612", // Member is obsolete
        "CS0618", // Member is obsolete, with a message
        "CS0619", // Member is obsolete, reported as an error
    }.ToFrozenSet(StringComparer.Ordinal);

    // Shared instances: every faded span in a file would otherwise allocate its own one-element
    // array, and the arrays are only ever read.
    private static readonly int[] s_unnecessary = [LspDiagnosticTag.Unnecessary];
    private static readonly int[] s_deprecated = [LspDiagnosticTag.Deprecated];

    /// <summary>
    /// The analyzer half of a diagnostic result id: which sources the report it stamps was built
    /// from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared by the document pull and the workspace sweep because the client mixes the two. Its
    /// <c>getAllResultIds</c> overwrites the sweep's stored id with the document pull's for any URI
    /// it is tracking, and hands that back as the sweep's <c>previousResultId</c> — so the two must
    /// compose the marker identically or the comparison can never succeed and the URI is re-bound
    /// on every sweep, forever. The sweep and the pull previously disagreed whenever analyzers were
    /// switched off: the sweep read <see cref="AnalyzerDiagnosticCache.IsComputed"/>, which is
    /// permanently false because nothing ever stores an entry, and said "c"; the pull gated on the
    /// option first and said "a".
    /// </para>
    /// <para>
    /// Three-state rather than two. Collapsing "analyzers off" onto "analyzers ran" would leave the
    /// id unmoved when the setting is toggled, and
    /// <see cref="ConfigurationHandler"/> relies on the id moving to re-send a full report and wipe
    /// the squiggles the analyzers that were just disabled had drawn.
    /// </para>
    /// </remarks>
    internal static string AnalyzerMarker(Document document, string version) =>
        !LspFeatureOptions.AnalyzerDiagnostics ? "n"
        : AnalyzerDiagnosticCache.IsComputed(document, version) ? "a"
        : "c";

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

        // A web.config belongs to no project in Roslyn's sense, so it has to be claimed before the
        // document resolve below returns null and reports nothing about it. Both push entry points
        // have had this branch all along, but LspServer gates every Schedule call on the client not
        // supporting pull — so for VS Code, which does, those branches are unreachable and opening
        // a web.config silently cleared its binding-redirect squiggles for as long as it stayed
        // open. No ResultId: the report is a function of bin and packages, which nothing here
        // versions, and the client tolerates its absence by treating every report as fresh.
        if (BindingRedirectHandler.IsConfigPath(path))
            return new FullDocumentDiagnosticReport(
                "full", await BindingRedirectHandler.CachedDiagnosticsAsync(path, ct));

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
        // dropping its squiggles on the floor. A declaration change moves the project's semantic
        // version, so this miss happens in every other open file in the project at once — reporting
        // an empty set is what made every warning in the window blink out and come back.
        if (analyzersPending && analyzer.IsEmpty)
            analyzer = AnalyzerDiagnosticCache.TryGetAnyVersion(document, version);

        // The resultId distinguishes "compiler only" from "compiler + analyzers" for the same
        // text; without that, the follow-up pull after the background pass answers "unchanged"
        // and the analyzer squiggles never appear.
        string? resultId = version is null ? null : $"{version}:{AnalyzerMarker(document, version)}";
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
