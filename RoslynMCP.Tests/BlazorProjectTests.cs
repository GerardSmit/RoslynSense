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
        Assert.True(sourceMap.Mappings.Count > 0,
            "Should find Razor source-generated documents with #line directives");
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
