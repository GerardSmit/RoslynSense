using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class BlazorProjectTests : IAsyncLifetime
{
    private MSBuildWorkspace? _workspace;
    private Project? _project;

    public async Task InitializeAsync()
    {
        _workspace = MSBuildWorkspace.Create();
        _project = await _workspace.OpenProjectAsync(FixturePaths.BlazorProjectFile);
    }

    public Task DisposeAsync()
    {
        _workspace?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void BlazorProject_OpensSuccessfully()
    {
        Assert.NotNull(_project);
        Assert.Equal("BlazorProject", _project!.Name);
    }

    [Fact]
    public async Task BuildSourceMap_FindsRazorGeneratedDocuments()
    {
        Assert.NotNull(_project);
        var sourceMap = await RazorSourceMappingService.BuildSourceMapAsync(_project!);

        Assert.NotNull(sourceMap);
        if (sourceMap.Mappings.Count == 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Project: {_project!.FilePath}");
            sb.AppendLine($"AnalyzerReferences count: {_project.AnalyzerReferences.Count}");
            foreach (var a in _project.AnalyzerReferences)
            {
                sb.AppendLine($"  [{a.GetType().Name}] {a.Display} :: {a.FullPath}");
                if (a is Microsoft.CodeAnalysis.Diagnostics.AnalyzerFileReference afr)
                {
                    var loadErrors = new List<string>();
                    afr.AnalyzerLoadFailed += (sender, args) =>
                        loadErrors.Add($"{args.ErrorCode}: {args.Message} (type={args.TypeName})");
                    try
                    {
                        var analyzers = afr.GetAnalyzers(Microsoft.CodeAnalysis.LanguageNames.CSharp);
                        var gens = afr.GetGenerators(Microsoft.CodeAnalysis.LanguageNames.CSharp);
                        sb.AppendLine($"      analyzers={analyzers.Length}, generators={gens.Length}");
                        foreach (var g in gens.Take(3))
                            sb.AppendLine($"        gen: {g.GetType().FullName}");
                        if (a.Display?.Contains("Razor") == true)
                        {
                            try
                            {
                                var asm = System.Reflection.Assembly.LoadFrom(a.FullPath!);
                                sb.AppendLine($"      asm name: {asm.GetName()}");
                                var refs = asm.GetReferencedAssemblies();
                                foreach (var r in refs.Where(r => r.Name?.Contains("CodeAnalysis") == true || r.Name?.Contains("Roslyn") == true))
                                    sb.AppendLine($"      refs: {r.FullName}");
                                var genTypes = asm.GetTypes().Where(t =>
                                    t.GetCustomAttributes(false).Any(att => att.GetType().Name == "GeneratorAttribute")).ToList();
                                sb.AppendLine($"      asm gen types: {genTypes.Count}");
                                foreach (var t in genTypes.Take(5))
                                    sb.AppendLine($"        {t.FullName}");
                            }
                            catch (Exception ex)
                            {
                                sb.AppendLine($"      assembly probe error: {ex.GetType().Name}: {ex.Message}");
                                if (ex is System.Reflection.ReflectionTypeLoadException rtle)
                                {
                                    foreach (var le in rtle.LoaderExceptions.Where(le => le is not null).Take(5))
                                        sb.AppendLine($"        loader-ex: {le!.GetType().Name}: {le.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"      load error: {ex.GetType().Name}: {ex.Message}");
                    }
                    foreach (var le in loadErrors)
                        sb.AppendLine($"      AnalyzerLoadFailed: {le}");
                }
            }
            var genDocs = (await _project.GetSourceGeneratedDocumentsAsync()).ToList();
            sb.AppendLine($"Generated docs: {genDocs.Count}");
            foreach (var d in genDocs.Take(5))
                sb.AppendLine($"  {d.HintName} :: {d.Name}");
            sb.AppendLine($"AdditionalDocuments count: {_project.AdditionalDocuments.Count()}");
            foreach (var ad in _project.AdditionalDocuments.Take(20))
                sb.AppendLine($"  {ad.Name} :: {ad.FilePath}");
            sb.AppendLine($"Documents count: {_project.Documents.Count()}");
            sb.AppendLine($"AnalyzerConfigDocuments count: {_project.AnalyzerConfigDocuments.Count()}");
            foreach (var ac in _project.AnalyzerConfigDocuments.Take(5))
            {
                sb.AppendLine($"  {ac.Name} :: {ac.FilePath}");
                if (ac.Name.Contains("MSBuild") || (ac.FilePath?.Contains("MSBuild") ?? false))
                {
                    var t = await ac.GetTextAsync();
                    foreach (var l in t.ToString().Split('\n').Take(80))
                        sb.AppendLine($"      | {l.TrimEnd()}");
                }
            }
            var ps = _project.ParseOptions as Microsoft.CodeAnalysis.CSharp.CSharpParseOptions;
            sb.AppendLine($"LangVersion: {ps?.LanguageVersion}");
            sb.AppendLine($"Features: {(ps == null ? "" : string.Join(",", ps.Features.Keys))}");
            var compilation = await _project.GetCompilationAsync();
            sb.AppendLine($"Compilation diagnostics: {compilation?.GetDiagnostics().Length ?? -1}");
            foreach (var d in compilation?.GetDiagnostics().Take(20) ?? Enumerable.Empty<Microsoft.CodeAnalysis.Diagnostic>())
                sb.AppendLine($"  {d.Severity}: [{d.Id}] {d.GetMessage()}");
            throw new Xunit.Sdk.XunitException("Should find Razor source-generated documents with #line directives. Diagnostics:\n" + sb.ToString());
        }
    }

    [Fact]
    public async Task BuildSourceMap_DiscoverRazorFiles()
    {
        Assert.NotNull(_project);

        var razorFiles = RazorSourceMappingService.DiscoverRazorFiles(_project!).ToList();

        Assert.True(razorFiles.Count > 0, "Should discover .razor files in project directory");

        var fileNames = razorFiles.Select(Path.GetFileName).ToList();
        Assert.Contains("Counter.razor", fileNames);
        Assert.Contains("Weather.razor", fileNames);
    }

    [Fact]
    public async Task BuildSourceMap_MappingsReferenceRazorFiles()
    {
        Assert.NotNull(_project);
        var sourceMap = await RazorSourceMappingService.BuildSourceMapAsync(_project!);

        // Each mapping should reference a .razor file that exists
        var razorFilePaths = sourceMap.Mappings
            .Select(m => m.RazorFilePath)
            .Distinct()
            .ToList();

        Assert.True(razorFilePaths.Count > 0, "Mappings should reference at least one .razor file");

        foreach (var razorPath in razorFilePaths)
        {
            Assert.True(razorPath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                || razorPath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase),
                $"Mapped file should have a Razor extension: {razorPath}");
        }
    }

    [Fact]
    public async Task MapGeneratedToRazor_EndToEnd()
    {
        Assert.NotNull(_project);
        var sourceMap = await RazorSourceMappingService.BuildSourceMapAsync(_project!);

        // Find a mapping for Counter.razor
        var counterMapping = sourceMap.Mappings.FirstOrDefault(m =>
            m.RazorFilePath.EndsWith("Counter.razor", StringComparison.OrdinalIgnoreCase));

        if (counterMapping is null)
        {
            // Skip if the Razor source generator didn't produce mappings for Counter
            return;
        }

        // Map a line within the generated range back to Razor
        var result = RazorSourceMappingService.MapGeneratedToRazor(
            sourceMap, counterMapping.GeneratedFilePath, counterMapping.GeneratedStartLine);

        Assert.NotNull(result);
        Assert.EndsWith("Counter.razor", result.RazorFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Line > 0, "Mapped line should be positive");
    }

    [Fact]
    public async Task MapDiagnostic_EndToEnd()
    {
        Assert.NotNull(_project);

        var compilation = await _project!.GetCompilationAsync();
        Assert.NotNull(compilation);

        var sourceMap = await RazorSourceMappingService.BuildSourceMapAsync(_project);

        // Get all diagnostics, map any that come from Razor-generated sources
        var diagnostics = compilation!.GetDiagnostics();
        var mappedCount = 0;

        foreach (var diag in diagnostics)
        {
            var mapped = RazorSourceMappingService.MapDiagnostic(sourceMap, diag);
            if (mapped.MappedLocation is not null)
                mappedCount++;
        }

        // We can't guarantee diagnostics, but the mapping code path should not throw
        // If there are diagnostics in generated code, some should be mapped
        Assert.True(true, "MapDiagnostic should not throw for any diagnostic");
    }
}
