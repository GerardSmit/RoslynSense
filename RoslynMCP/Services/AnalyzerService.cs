using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
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
    /// Runs analyzers for a single document using Roslyn's per-tree entry points, which
    /// analyze only this file rather than the whole compilation — the difference between
    /// usable and unusable on an editor path.
    /// Returns an empty set (never throws) when analyzers fail or exceed their time budget.
    /// </summary>
    public static async Task<ImmutableArray<Diagnostic>> RunDocumentAnalyzersAsync(
        Document document, CancellationToken cancellationToken = default)
    {
        var project = document.Project;
        var analyzers = GetAnalyzersFor(project);
        if (analyzers.IsDefaultOrEmpty)
            return ImmutableArray<Diagnostic>.Empty;

        var compilation = await project.GetCompilationAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (compilation is null || model is null)
            return ImmutableArray<Diagnostic>.Empty;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(LspFeatureOptions.AnalyzerTimeout);

        try
        {
            var withAnalyzers = compilation.WithAnalyzers(analyzers, CreateOptions(project));
            var syntax = await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(model.SyntaxTree, budget.Token);
            var semantic = await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, null, budget.Token);

            return syntax.AddRange(semantic)
                .Where(d => d.Location.SourceTree == model.SyntaxTree)
                .ToImmutableArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            ServiceLog.Warn(
                $"Analyzers timed out after {LspFeatureOptions.AnalyzerTimeout.TotalSeconds:0}s " +
                $"for '{document.Name}'; showing compiler diagnostics only.",
                key: "analyzer-timeout");
            return ImmutableArray<Diagnostic>.Empty;
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"Analyzers failed for '{document.Name}': {ex.Message}", key: "analyzer-failure");
            return ImmutableArray<Diagnostic>.Empty;
        }
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
