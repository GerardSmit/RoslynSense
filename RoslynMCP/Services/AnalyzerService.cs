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
        if (!LspFeatureOptions.CodeStyleDiagnostics)
            return project0;

        var ide = LoadIdeAnalyzers();
        return project0.IsDefaultOrEmpty ? ide : ide.IsDefaultOrEmpty ? project0 : project0.AddRange(ide);
    }

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
    public readonly record struct AnalyzerRun(ImmutableArray<Diagnostic> Diagnostics, bool Failed);

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
    public static async Task<AnalyzerRun> RunDocumentAnalyzersWithStatusAsync(
        Document document, CancellationToken cancellationToken = default)
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
            var window = BudgetFor(text);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(window);

            var syntaxOnly = ImmutableArray<Diagnostic>.Empty;
            try
            {
                var withAnalyzers = DriverFor(compilation, project, analyzers);

                var syntax = await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(model.SyntaxTree, budget.Token);
                syntaxOnly = syntax;
                var semantic = await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, null, budget.Token);

                return new AnalyzerRun(ForThisTree(syntax.AddRange(semantic), model), Failed: false);
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
                    + $"({text.Lines.Count} lines, {analyzers.Length} analyzers); "
                    + "showing compiler and syntax diagnostics only.",
                    key: "analyzer-timeout");
                return new AnalyzerRun(ForThisTree(syntaxOnly, model), Failed: true);
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"Analyzers failed for '{document.Name}': {ex.Message}", key: "analyzer-failure");
                return new AnalyzerRun(ImmutableArray<Diagnostic>.Empty, Failed: true);
            }
        }
        finally
        {
            s_passes.Release();
        }
    }

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
