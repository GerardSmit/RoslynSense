using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public sealed class RenameScopeTests
{
    [Fact]
    public async Task LocalRenameDoesNotWaitForDependentProjectLoading()
    {
        string session = "local-rename-" + Guid.NewGuid().ToString("N");
        string path = FixturePaths.LayeredAppWarehouseModuleFile;
        const string source = "namespace LayeredApp.Warehouse; class Model { int Read() { int counter = 1; return counter; } }";
        var text = SourceText.From(source);
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var binding = WorkspaceService.BindSolutionForTesting(FixturePaths.LayeredAppSolutionFile);
        await WorkspaceService.EvictProjectForTests(FixturePaths.LayeredAppStorefrontProjectFile);
        Microsoft.CodeAnalysis.Workspace? workspace = null;
        string? projectPath = null;
        Task? pending = null;
        Task<WorkspaceEdit?>? rename = null;
        try
        {
            OpenDocumentStore.Open(session, path, text, 1);
            var document = await LspDocumentResolver.ResolveAsync(path, default);
            Assert.NotNull(document);
            workspace = document.Project.Solution.Workspace;
            projectPath = document.Project.FilePath!;
            SearchScopeService.ForgetConsumerLoad(projectPath, workspace);
            pending = SearchScopeService.ConsumerLoadFor(projectPath, workspace, () => load.Task);

            var position = text.Lines.GetLinePosition(source.IndexOf("counter", StringComparison.Ordinal));
            rename = RenameHandler.RenameAsync(new RenameParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(path)),
                new Position(position.Line, position.Character), "count"), default, LanguageSession.Empty);
            var edit = await rename.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(load.Task.IsCompleted);
            Assert.NotNull(edit?.Changes);
            var changes = Assert.Single(edit!.Changes!);
            Assert.Equal(LspConverters.PathToUri(path), changes.Key, ignoreCase: true);
            // Roslyn may express counter -> count as deleting only the trailing "er".
            // Validate the applied edit, independent of that minimal-diff representation.
            var renamedText = text.WithChanges(changes.Value.Select(change => new TextChange(
                TextSpan.FromBounds(LspConverters.ToOffset(text, change.Range.Start),
                    LspConverters.ToOffset(text, change.Range.End)), change.NewText)));
            Assert.Equal(source.Replace("counter", "count", StringComparison.Ordinal), renamedText.ToString());
            var current = await LspDocumentResolver.ResolveAsync(path, default);
            Assert.DoesNotContain(current!.Project.Solution.Projects,
                p => string.Equals(p.FilePath, FixturePaths.LayeredAppStorefrontProjectFile, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            load.TrySetResult();
            try
            {
                if (rename is not null)
                    await rename.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                if (workspace is not null && projectPath is not null)
                    SearchScopeService.ForgetConsumerLoad(projectPath, workspace, pending);
                OpenDocumentStore.Close(session, path);
                await WorkspaceService.ReconcileOpenBufferAsync(path);
            }
        }
    }
}
