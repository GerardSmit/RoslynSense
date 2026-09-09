using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public class ResolveLifetimeSafetyTests
{
    [Fact]
    public async Task ChangeDuringResolveRejectsTheOldActionEvenWhenItEditsAnotherFile()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Resolve", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Test.cs", SourceText.From("class C {}"));
        using var cache = new LspResolveCache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var action = Microsoft.CodeAnalysis.CodeActions.CodeAction.Create("Change", async ct =>
        {
            entered.SetResult();
            await finish.Task.WaitAsync(ct);
            return document.WithText(SourceText.From("class Renamed {}"));
        });
        var id = cache.StoreAction(action, document.Project.Solution);
        var resolve = CodeActionHandler.ResolveAsync(new("Change", "refactor", null)
            { Data = new CodeActionData(id) }, cache, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cache.DocumentChanged("Other.cs");
        finish.SetResult();
        var error = await Assert.ThrowsAsync<StreamJsonRpc.LocalRpcException>(() => resolve);
        Assert.Equal(-32801, error.ErrorCode);
    }
}
