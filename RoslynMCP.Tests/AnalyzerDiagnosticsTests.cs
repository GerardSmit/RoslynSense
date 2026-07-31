using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>Analyzer diagnostics on the editor path: the per-document pass, .editorconfig
/// severity handling, and the version-keyed cache that keeps it off the typing loop.</summary>
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
}
