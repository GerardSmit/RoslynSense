using Microsoft.CodeAnalysis;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

internal static class RoslynTestHelpers
{
    public static async Task<Project> OpenProjectAsync(
        string projectPath,
        string? targetFilePath = null,
        CancellationToken cancellationToken = default)
    {
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath,
            targetFilePath: targetFilePath,
            cancellationToken: cancellationToken);
        return project;
    }

    /// <summary>
    /// <see cref="WatchedFilesHandler.ProcessAsync"/>, then the import-completion warm-up it
    /// leaves running. The warm-up resolves each applied file's document in the background, and
    /// for a file no loaded project holds any more — the test's own probe, deleted and evicted in
    /// its teardown — that is a walk up the directory tree that opens every project it passes.
    /// Left to itself it landed the fixture's sibling project and then this repository's own
    /// solution in the cache while a later test was counting entries or sweeping the most
    /// recently used solution. Waiting keeps it inside the test that caused it.
    /// </summary>
    public static async Task<WatchedFilesHandler.Outcome> ProcessWatchedFilesAsync(
        IReadOnlyList<FileEvent> changes, CancellationToken cancellationToken = default)
    {
        var outcome = await WatchedFilesHandler.ProcessAsync(changes, cancellationToken);
        await ImportCompletionWarmer.DrainForTestsAsync();
        return outcome;
    }

    public static async Task<(Workspace Workspace, Document Document)> OpenDocumentAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        string? projectPath = await WorkspaceService.FindContainingProjectAsync(filePath, cancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(projectPath));

        var (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath!,
            targetFilePath: filePath,
            cancellationToken: cancellationToken);
        var document = WorkspaceService.FindDocumentInProject(project, filePath);
        Assert.NotNull(document);

        return (workspace, document!);
    }

    public static async Task<ISymbol> ResolveSymbolAsync(
        string filePath,
        string markupSnippet,
        CancellationToken cancellationToken = default)
    {
        var result = await MarkupSymbolResolver.ResolveFromFileAsync(
            filePath,
            MarkupString.Parse(markupSnippet),
            cancellationToken);

        Assert.Equal(MarkupResolutionResult.ResultKind.Resolved, result.Kind);
        Assert.NotNull(result.Symbol);
        return result.Symbol!;
    }

    public static async Task<INamedTypeSymbol> GetNamedTypeAsync(
        string projectPath,
        string metadataName,
        CancellationToken cancellationToken = default)
    {
        var project = await OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
        var compilation = await project.GetCompilationAsync(cancellationToken);
        Assert.NotNull(compilation);

        var type = compilation!.GetTypeByMetadataName(metadataName);
        Assert.NotNull(type);
        return type!;
    }
}
