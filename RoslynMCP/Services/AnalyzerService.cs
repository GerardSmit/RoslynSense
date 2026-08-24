using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;

namespace RoslynMCP.Services;

/// <summary>
/// Discovers, loads, and executes Roslyn diagnostic analyzers from the project context.
/// Analyzer DLLs are resolved from the <see cref="Project.AnalyzerReferences"/>
/// populated by MSBuildWorkspace, then loaded in an isolated collectible
/// <see cref="AnalyzerLoadContext"/> via <see cref="AnalyzerHost"/>.
/// </summary>
internal static class AnalyzerService
{
    private static readonly AnalyzerHost s_analyzerHost = new();

    private static readonly string[] s_ideAnalyzerAssemblies =
        ["Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features"];
    private static readonly object s_ideAnalyzerLock = new();
    private static ImmutableArray<DiagnosticAnalyzer>? s_ideAnalyzers;

    static AnalyzerService()
    {
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => DisposeHost();
        AssemblyLoadContext.Default.Unloading += static _ => DisposeHost();
    }

    /// <summary>
    /// Discovers analyzer DLL paths from the Roslyn <see cref="Project"/> context.
    /// Uses <see cref="Project.AnalyzerReferences"/> which MSBuildWorkspace populates
    /// from NuGet package analyzer assets and explicit <c>&lt;Analyzer&gt;</c> items.
    /// </summary>
    internal static List<string> DiscoverAnalyzerPathsFromProject(Project project)
    {
        var paths = new List<string>();

        foreach (var reference in project.AnalyzerReferences)
        {
            var fullPath = reference.FullPath;
            if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
                paths.Add(fullPath);
        }

        return paths;
    }

    /// <summary>
    /// Loads analyzers specific to the given <paramref name="project"/> by reading its
    /// resolved analyzer references and loading them in an isolated ALC.
    /// Results are cached by <see cref="AnalyzerHost"/> keyed on project identity
    /// and the resolved DLL set with file metadata.
    /// </summary>
    public static ImmutableArray<DiagnosticAnalyzer> LoadAnalyzersForProject(Project project)
    {
        var analyzerPaths = DiscoverAnalyzerPathsFromProject(project);

        if (analyzerPaths.Count == 0)
            return ImmutableArray<DiagnosticAnalyzer>.Empty;

        string projectKey = project.FilePath ?? project.Name;
        return s_analyzerHost.GetOrLoadAnalyzers(projectKey, analyzerPaths);
    }

    /// <summary>
    /// The analyzer set for a project: its own analyzer references plus, when
    /// <see cref="LspFeatureOptions.CodeStyleDiagnostics"/> is on, Roslyn's built-in IDE
    /// analyzers (the IDE0xxx code-style rules, which ship inside the Features assemblies
    /// rather than as project analyzer references).
    /// </summary>
    public static ImmutableArray<DiagnosticAnalyzer> GetAnalyzersFor(Project project)
    {
        var project0 = LoadAnalyzersForProject(project);
        if (LspFeatureOptions.CodeStyleDiagnostics)
        {
            var ide = LoadIdeAnalyzers();
            project0 = project0.IsDefaultOrEmpty ? ide : ide.IsDefaultOrEmpty ? project0 : project0.AddRange(ide);
        }

        var extra = AdditionalAnalyzersForTesting;
        return extra.IsDefaultOrEmpty ? project0
            : project0.IsDefaultOrEmpty ? extra
            : project0.AddRange(extra);
    }

    /// <summary>Test seam: analyzers added to every project's set, for exercising the cost
    /// buckets without shipping a pathological analyzer in a fixture.</summary>
    internal static ImmutableArray<DiagnosticAnalyzer> AdditionalAnalyzersForTesting { get; set; } =
        ImmutableArray<DiagnosticAnalyzer>.Empty;

    /// <summary>
    /// What a run produced, and whether it actually finished.
    /// </summary>
    /// <remarks>
    /// A timeout returns an empty result so the caller can still show compiler diagnostics, but
    /// empty-because-we-stopped-looking and empty-because-the-file-is-clean are different facts.
    /// Caching the first as the second means the file is never analysed again at that version, and
    /// the cache serves any result whose text checksum matches — so one timeout blanked that file's
    /// analyzer diagnostics until somebody edited it.
    ///
    /// Returned rather than parked in a [ThreadStatic]. The failure is recorded inside a catch that
    /// runs on whatever pool thread resumed the await, and read after another await on whatever
    /// thread resumed that — so a thread-local neither reaches the reader (the timeout is cached as
    /// clean anyway) nor stays put (a stale true on a reused thread discards a later good result).
    /// </remarks>
    /// <param name="SpanLimitedIds">
    /// The ids whose analyzers were run over one member's span rather than the whole file, so the
    /// caller knows which of them it still owes findings for outside that span. Null after a
    /// whole-file pass, where nothing is owed.
    /// </param>
    public readonly record struct AnalyzerRun(
        ImmutableArray<Diagnostic> Diagnostics,
        bool Failed,
        ImmutableHashSet<string>? SpanLimitedIds = null);

    /// <summary>
    /// Runs analyzers for a single document using Roslyn's per-tree entry points, which analyze
    /// only this file rather than the whole compilation — the difference between usable and
    /// unusable on an editor path. Returns an empty set (never throws) when analyzers fail or
    /// exceed their time budget; callers that cache the result want
    /// <see cref="RunDocumentAnalyzersWithStatusAsync"/> instead, so they can tell those two
    /// kinds of empty apart.
    /// </summary>
    public static async Task<ImmutableArray<Diagnostic>> RunDocumentAnalyzersAsync(
        Document document, CancellationToken cancellationToken = default) =>
        (await RunDocumentAnalyzersWithStatusAsync(document, cancellationToken)).Diagnostics;

    /// <summary>As above, and says whether the run finished or gave up.</summary>
    public static Task<AnalyzerRun> RunDocumentAnalyzersWithStatusAsync(
        Document document, CancellationToken cancellationToken = default) =>
        RunAsync(document, memberSpan: null, cancellationToken);

    /// <summary>
    /// The typing-loop pass: span-capable analyzers see only <paramref name="memberSpan"/>, the
    /// rest still see the whole file.
    /// </summary>
    /// <remarks>
    /// Mirrors Roslyn's incremental member-edit analysis. Span capability is
    /// <c>DiagnosticAnalyzerCategory.SemanticSpanAnalysis</c>, which an analyzer only claims by
    /// implementing <see cref="IBuiltInAnalyzer"/> — an unknown third-party analyzer is assumed to
    /// need the whole document, so restricting it would silently lose findings. The returned
    /// <see cref="AnalyzerRun.SpanLimitedIds"/> is how the caller knows which half of the result is
    /// partial and has to be completed from the previous whole-file one.
    /// </remarks>
    internal static Task<AnalyzerRun> RunMemberSpanAnalyzersAsync(
        Document document, TextSpan memberSpan, CancellationToken cancellationToken) =>
        RunAsync(document, memberSpan, cancellationToken);

    private static async Task<AnalyzerRun> RunAsync(
        Document document, TextSpan? memberSpan, CancellationToken cancellationToken)
    {
        var project = document.Project;
        var analyzers = GetAnalyzersFor(project);
        if (analyzers.IsDefaultOrEmpty)
            return new AnalyzerRun(ImmutableArray<Diagnostic>.Empty, Failed: false);

        var compilation = await project.GetCompilationAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (compilation is null || model is null)
            return new AnalyzerRun(ImmutableArray<Diagnostic>.Empty, Failed: false);

        var text = await document.GetTextAsync(cancellationToken);

        // Waiting for a slot is not analysis, so the budget cannot be ticking during it. Every pass
        // used to start its clock at the top of the method: pull several large files at once and
        // they all spent most of their 15s queued behind each other, then reported a timeout for
        // work that had barely started.
        await s_passes.WaitAsync(cancellationToken);
        try
        {
            var withAnalyzers = DriverFor(compilation, project, analyzers);
            var (fast, slow) = await BucketsAsync(withAnalyzers, analyzers, cancellationToken);
            var spanLimited = memberSpan is null ? null : SpanLimitedIdsOf(analyzers);

            var window = BudgetFor(text);
            var syntaxOnly = ImmutableArray<Diagnostic>.Empty;
            ImmutableArray<Diagnostic> cheap;

            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                budget.CancelAfter(window);
                try
                {
                    // The whole set, not just the fast bucket: a syntax action is by construction
                    // not one of the shapes that puts an analyzer in the slow bucket, so splitting
                    // this half would buy nothing and cost a second driver invocation.
                    syntaxOnly = await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(
                        model.SyntaxTree, null, analyzers, budget.Token);
                    cheap = syntaxOnly.AddRange(
                        await SemanticAsync(withAnalyzers, model, fast, memberSpan, budget.Token));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // The syntax pass is the cheap half and is usually already done when the semantic
                    // half runs out of time. Reporting it beats reporting nothing, and Failed still
                    // keeps the caller from caching this as a clean file.
                    ServiceLog.Warn(
                        $"Analyzers timed out after {window.TotalSeconds:0}s for '{document.Name}' "
                        + $"({text.Lines.Count} lines, {fast.Length} of {analyzers.Length} analyzers); "
                        + "showing compiler and syntax diagnostics only.",
                        key: "analyzer-timeout");
                    return new AnalyzerRun(ForThisTree(syntaxOnly, model), Failed: true);
                }
                catch (Exception ex)
                {
                    // The whole exception: a driver-level failure (as opposed to one analyzer's,
                    // which onAnalyzerException names) has no other record of its frame.
                    ServiceLog.Error($"Analyzers failed for '{document.Name}': {ex}", key: "analyzer-failure");
                    return new AnalyzerRun(ImmutableArray<Diagnostic>.Empty, Failed: true);
                }
            }

            // The expensive half, on its own clock and its own token. Whatever happens to it, the
            // cheap half above has already finished and is a real result: it used to share one
            // budget with this, so a single analyzer registering a SymbolStart or SemanticModel
            // action could time the whole pass out, mark it Failed, block it from being cached,
            // and have every code-style squiggle in the file recomputed and lost again on the next
            // request — for as long as that analyzer stayed in the project.
            var slowFindings = ImmutableArray<Diagnostic>.Empty;
            if (!slow.IsEmpty)
            {
                var slowWindow = SlowBudgetOverrideForTesting ?? BudgetFor(text);
                using var slowBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                slowBudget.CancelAfter(slowWindow);
                try
                {
                    slowFindings = await SemanticAsync(withAnalyzers, model, slow, memberSpan, slowBudget.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // A keystroke cancels both waves. The caller's token is linked into each
                    // budget, so this is the same cancellation the cheap half would have seen.
                    throw;
                }
                catch (OperationCanceledException)
                {
                    ServiceLog.Warn(
                        $"Expensive analyzers timed out after {slowWindow.TotalSeconds:0}s for "
                        + $"'{document.Name}' ({slow.Length} of {analyzers.Length} analyzers); "
                        + "reporting the rest.",
                        key: "analyzer-slow-timeout");
                }
                catch (Exception ex)
                {
                    ServiceLog.Error(
                        $"Expensive analyzers failed for '{document.Name}': {ex}",
                        key: "analyzer-slow-failure");
                }
            }

            return new AnalyzerRun(
                ForThisTree(cheap.AddRange(slowFindings), model), Failed: false, SpanLimitedIds: spanLimited);
        }
        finally
        {
            s_passes.Release();
        }
    }

    private static async Task<ImmutableArray<Diagnostic>> SemanticAsync(
        CompilationWithAnalyzers driver, SemanticModel model, ImmutableArray<DiagnosticAnalyzer> subset,
        TextSpan? memberSpan, CancellationToken ct)
    {
        if (subset.IsDefaultOrEmpty)
            return ImmutableArray<Diagnostic>.Empty;

        if (memberSpan is null)
            return await driver.GetAnalyzerSemanticDiagnosticsAsync(model, null, subset, ct);

        var spanCapable = subset.Where(SupportsSpan).ToImmutableArray();
        var wholeFile = subset.Where(static a => !SupportsSpan(a)).ToImmutableArray();

        var restricted = spanCapable.IsEmpty
            ? ImmutableArray<Diagnostic>.Empty
            : await driver.GetAnalyzerSemanticDiagnosticsAsync(model, memberSpan, spanCapable, ct);
        var complete = wholeFile.IsEmpty
            ? ImmutableArray<Diagnostic>.Empty
            : await driver.GetAnalyzerSemanticDiagnosticsAsync(model, null, wholeFile, ct);

        return restricted.AddRange(complete);
    }

    private static ImmutableHashSet<string> SpanLimitedIdsOf(ImmutableArray<DiagnosticAnalyzer> analyzers)
    {
        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var analyzer in analyzers)
        {
            if (!SupportsSpan(analyzer))
                continue;
            try
            {
                foreach (var descriptor in analyzer.SupportedDiagnostics)
                    builder.Add(descriptor.Id);
            }
            catch { /* an analyzer that cannot describe itself is not one we can restrict */ }
        }

        return builder.ToImmutable();
    }

    /// <summary>Whether an analyzer promises that a span-restricted semantic pass is complete for
    /// that span. Unknown analyzers do not, which is the safe answer.</summary>
    internal static bool SupportsSpan(DiagnosticAnalyzer analyzer)
    {
        try { return analyzer.SupportsSpanBasedSemanticDiagnosticAnalysis(); }
        catch { return false; }
    }

    /// <summary>
    /// Which analyzers get the ordinary budget and which get their own.
    /// </summary>
    /// <remarks>
    /// The shapes Roslyn de-prioritises: a SymbolStart/End pair holds per-symbol state across the
    /// whole compilation, and a SemanticModel action asks for the file to be fully bound. The
    /// compiler analyzer is never de-prioritised however it registers. Classification is one
    /// <c>GetAnalyzerTelemetryInfoAsync</c> call per analyzer — it forces the analyzer's
    /// <c>Initialize</c>, which the driver is about to do anyway — and is cached on the analyzer
    /// instance, which is itself cached per project by <see cref="AnalyzerHost"/>.
    /// </remarks>
    private static async Task<(ImmutableArray<DiagnosticAnalyzer> Fast, ImmutableArray<DiagnosticAnalyzer> Slow)>
        BucketsAsync(
            CompilationWithAnalyzers driver, ImmutableArray<DiagnosticAnalyzer> analyzers,
            CancellationToken ct)
    {
        var fast = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        var slow = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();

        foreach (var analyzer in analyzers)
            (await IsExpensiveAsync(driver, analyzer, ct) ? slow : fast).Add(analyzer);

        return (fast.ToImmutable(), slow.ToImmutable());
    }

    private static readonly ConditionalWeakTable<DiagnosticAnalyzer, StrongBox<bool>> s_cost = new();

    private static async ValueTask<bool> IsExpensiveAsync(
        CompilationWithAnalyzers driver, DiagnosticAnalyzer analyzer, CancellationToken ct)
    {
        if (s_cost.TryGetValue(analyzer, out var known))
            return known.Value;

        bool expensive;
        try
        {
            if (analyzer.IsCompilerAnalyzer())
            {
                expensive = false;
            }
            else
            {
                var telemetry = await driver.GetAnalyzerTelemetryInfoAsync(analyzer, ct);
                expensive = telemetry.SymbolStartActionsCount > 0
                    || telemetry.SymbolEndActionsCount > 0
                    || telemetry.SemanticModelActionsCount > 0;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Not an answer, and caching it would fix a misclassification for the process lifetime.
            throw;
        }
        catch
        {
            // An analyzer that cannot be interrogated keeps the behaviour it had before there were
            // buckets: the ordinary budget.
            expensive = false;
        }

        s_cost.AddOrUpdate(analyzer, new StrongBox<bool>(expensive));
        return expensive;
    }

    internal static Task<(ImmutableArray<DiagnosticAnalyzer> Fast, ImmutableArray<DiagnosticAnalyzer> Slow)>
        BucketsForTesting(
            CompilationWithAnalyzers driver, ImmutableArray<DiagnosticAnalyzer> analyzers,
            CancellationToken ct = default) =>
        BucketsAsync(driver, analyzers, ct);

    internal static bool IsCompilerAnalyzerForTesting(DiagnosticAnalyzer analyzer) =>
        analyzer.IsCompilerAnalyzer();

    /// <summary>Test seam: the expensive bucket's own window, so a pathological analyzer can be
    /// timed out without waiting out the real budget.</summary>
    internal static TimeSpan? SlowBudgetOverrideForTesting { get; set; }

    private static ImmutableArray<Diagnostic> ForThisTree(
        ImmutableArray<Diagnostic> diagnostics, SemanticModel model) =>
        [.. diagnostics.Where(d => d.Location.SourceTree == model.SyntaxTree)];

    /// <summary>
    /// How many analyzer passes may run at once.
    /// </summary>
    /// <remarks>
    /// A pass already parallelises across analyzers internally (<c>concurrentAnalysis</c>), so
    /// running many at once does not finish more work — it divides one machine's cores among them
    /// and makes every one of them slower, which with a per-pass deadline means they miss it
    /// together rather than completing one after another. Two, so a keystroke in the open file is
    /// not stuck behind a workspace sweep of some other project.
    /// </remarks>
    private static readonly SemaphoreSlim s_passes = new(2, 2);

    /// <summary>
    /// The time budget for one file. <see cref="LspFeatureOptions.AnalyzerTimeout"/> is what an
    /// ordinary file gets; past a few thousand lines the work is roughly linear in size, and a flat
    /// deadline is one that only large files can miss — so they get proportionally more, up to a
    /// ceiling that keeps a pathological file from occupying a slot indefinitely.
    /// </summary>
    private static TimeSpan BudgetFor(SourceText text)
    {
        var baseline = LspFeatureOptions.AnalyzerTimeout;
        int lines = text.Lines.Count;
        if (lines <= OrdinaryFileLines)
            return baseline;

        var scaled = baseline + TimeSpan.FromMilliseconds((lines - OrdinaryFileLines) * MillisecondsPerLine);
        var ceiling = baseline * MaxBudgetMultiple;
        return scaled < ceiling ? scaled : ceiling;
    }

    private const int OrdinaryFileLines = 2_000;
    private const double MillisecondsPerLine = 1.5;
    private const int MaxBudgetMultiple = 4;

    internal static TimeSpan BudgetForTesting(SourceText text) => BudgetFor(text);

    internal static CompilationWithAnalyzers DriverForTesting(
        Compilation compilation, Project project, ImmutableArray<DiagnosticAnalyzer> analyzers) =>
        DriverFor(compilation, project, analyzers);

    /// <summary>
    /// One analyzer driver per compilation, instead of one per request.
    /// </summary>
    /// <remarks>
    /// A <see cref="CompilationWithAnalyzers"/> carries the per-analyzer state Roslyn builds on
    /// first use — every analyzer's <c>Initialize</c> and every compilation-start action — and
    /// throwing it away after one file makes the next file rebuild all of it. Keyed on the
    /// compilation by reference and held weakly: compilations are immutable and a new one appears
    /// on every edit, so an entry becomes garbage exactly when the edit that replaced it does, and
    /// nothing has to decide when to evict.
    /// </remarks>
    private static readonly ConditionalWeakTable<Compilation, DriverEntry> s_drivers = new();

    private sealed record DriverEntry(ImmutableArray<DiagnosticAnalyzer> Analyzers, CompilationWithAnalyzers Driver);

    private static CompilationWithAnalyzers DriverFor(
        Compilation compilation, Project project, ImmutableArray<DiagnosticAnalyzer> analyzers)
    {
        if (s_drivers.TryGetValue(compilation, out var entry) && entry.Analyzers.SequenceEqual(analyzers))
            return entry.Driver;

        // Rebuilt rather than reused when the analyzer set changed under us — toggling code-style
        // diagnostics, or a project whose analyzer references were reloaded.
        var driver = compilation.WithAnalyzers(analyzers, CreateOptions(project));
        s_drivers.AddOrUpdate(compilation, new DriverEntry(analyzers, driver));
        return driver;
    }

    /// <summary>
    /// Runs project-specific analyzers against a compilation and returns diagnostics
    /// filtered to the specified <paramref name="filePath"/>, or all of them when
    /// <paramref name="filePath"/> is null.
    /// Analyzer DLLs are discovered from the <paramref name="project"/>'s analyzer
    /// references rather than a global NuGet directory.
    /// Progress and errors are written to <paramref name="writer"/>.
    /// </summary>
    public static async Task<IEnumerable<Diagnostic>> RunAnalyzersAsync(
        Project project, Compilation compilation, string? filePath, TextWriter writer,
        CancellationToken cancellationToken = default)
    {
        writer.WriteLine("\nRunning code analyzers...");

        try
        {
            var analyzers = GetAnalyzersFor(project);

            if (analyzers.Length > 0)
            {
                writer.WriteLine(
                    $"Found {analyzers.Length} analyzer(s) from {project.AnalyzerReferences.Count} project analyzer reference(s)");

                // AnalyzerOptions carries the AnalyzerConfigOptionsProvider — without it,
                // .editorconfig/.globalconfig severity overrides are silently ignored.
                var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers, CreateOptions(project));

                var allDiagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken);

                if (filePath is null)
                    return allDiagnostics.Where(d => d.Location.SourceTree is not null);

                return allDiagnostics.Where(d =>
                    d.Location.SourceTree != null &&
                    string.Equals(d.Location.SourceTree.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                writer.WriteLine(
                    "No analyzer references found in project. " +
                    "Analyzers are discovered from project-level NuGet packages and <Analyzer> items.");
                return Array.Empty<Diagnostic>();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            writer.WriteLine($"Error running analyzers: {ex.Message}");
            return Array.Empty<Diagnostic>();
        }
    }

    private static CompilationWithAnalyzersOptions CreateOptions(Project project) =>
        new(project.AnalyzerOptions,
            onAnalyzerException: static (ex, analyzer, _) =>
                Console.Error.WriteLine($"[Analyzers] {analyzer.GetType().Name} threw: {ex.Message}"),
            concurrentAnalysis: true,
            logAnalyzerExecutionTime: false,
            reportSuppressedDiagnostics: false);

    /// <summary>
    /// Roslyn's built-in IDE analyzers (IDE0xxx code style). They live inside the Features
    /// assemblies and are never present in <see cref="Project.AnalyzerReferences"/>, so they
    /// must be reflected out and instantiated directly. Failure is non-fatal: third-party
    /// analyzers still run.
    /// </summary>
    public static ImmutableArray<DiagnosticAnalyzer> LoadIdeAnalyzers()
    {
        if (s_ideAnalyzers is { } cached)
            return cached;

        lock (s_ideAnalyzerLock)
        {
            if (s_ideAnalyzers is { } raced)
                return raced;

            var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
            foreach (var assemblyName in s_ideAnalyzerAssemblies)
            {
                try
                {
                    CollectIdeAnalyzers(Assembly.Load(new AssemblyName(assemblyName)), builder);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[Analyzers] Could not load IDE analyzers from '{assemblyName}': {ex.Message}");
                }
            }

            return (s_ideAnalyzers = builder.ToImmutable()).Value;
        }
    }

    private static void CollectIdeAnalyzers(
        Assembly assembly, ImmutableArray<DiagnosticAnalyzer>.Builder builder)
    {
        Type?[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }

        foreach (var type in types)
        {
            if (type is null || type.IsAbstract || !typeof(DiagnosticAnalyzer).IsAssignableFrom(type))
                continue;
            if (type.GetCustomAttribute<DiagnosticAnalyzerAttribute>() is not { } attribute ||
                !attribute.Languages.Contains(LanguageNames.CSharp))
                continue;
            // The compiler analyzer would duplicate everything SemanticModel.GetDiagnostics
            // already reports; DocumentDiagnosticAnalyzer is an IDE-host shape that
            // CompilationWithAnalyzers cannot drive.
            if (type.Name.Contains("CompilerDiagnosticAnalyzer", StringComparison.Ordinal) ||
                DerivesFromDocumentAnalyzer(type))
                continue;
            if (type.GetConstructor(Type.EmptyTypes) is null)
                continue;

            try { builder.Add((DiagnosticAnalyzer)Activator.CreateInstance(type)!); }
            catch { /* analyzer refused to construct without an IDE host — skip it */ }
        }
    }

    private static bool DerivesFromDocumentAnalyzer(Type type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.Name is "DocumentDiagnosticAnalyzer")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Evicts cached analyzer contexts for a specific project path,
    /// unloading the associated collectible <see cref="AnalyzerLoadContext"/>.
    /// </summary>
    public static void EvictAnalyzersForProject(string projectPath) =>
        s_analyzerHost.EvictForProject(projectPath);

    /// <summary>
    /// Unloads all cached analyzer contexts, forcing a fresh load on next use.
    /// </summary>
    public static void UnloadAnalyzers() => s_analyzerHost.UnloadAll();

    public static void DisposeHost() => s_analyzerHost.Dispose();
}
