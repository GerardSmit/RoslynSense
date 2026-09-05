using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public sealed class WorkspaceLoadOverlayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningBuffersBeforeLoadsKeepsTheirFirstCompilationInTheLiveWorkspace(bool edited)
    {
        string root = Path.Combine(Path.GetTempPath(), "RoslynSense-LoadOverlay-" + Guid.NewGuid().ToString("N"));
        string session = Guid.NewGuid().ToString("N");
        var projects = new List<string>();
        var files = new List<string>();
        var texts = new List<string>();
        Directory.CreateDirectory(root);
        try
        {
            foreach (string name in new[] { "A", "B", "C", "D" })
            {
                string directory = Path.Combine(root, name);
                Directory.CreateDirectory(directory);
                string project = Path.Combine(directory, name + ".csproj");
                string reference = name == "A" ? "" : "<ItemGroup><ProjectReference Include=\"../A/A.csproj\" /></ItemGroup>";
                await File.WriteAllTextAsync(project,
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework>" +
                    "</PropertyGroup>" + reference + "</Project>");
                string file = Path.Combine(directory, name + ".cs");
                string source = $"public class {name} {{ public int Value {{ get; set; }} }}\n";
                await File.WriteAllTextAsync(file, source);
                projects.Add(project);
                files.Add(file);
                texts.Add(source + (edited ? "// unsaved editor text\n" : ""));
            }
            await File.WriteAllTextAsync(Path.Combine(root, "App.slnx"),
                "<Solution>" + string.Concat(new[] { "A", "B", "C", "D" }.Select(
                    name => $"<Project Path=\"{name}/{name}.csproj\" />")) + "</Solution>");

            // didOpen precedes the cold seed load. No explicit reconcile or text read may
            // prepare the workspace first: that hid the unnecessary first-completion fork.
            OpenDocumentStore.Open(session, files[0], SourceText.From(texts[0]), version: 7);
            var (workspace, initial) = await WorkspaceService.GetOrOpenProjectAsync(
                projects[0], targetFilePath: files[0]);
            Assert.Same(workspace.CurrentSolution.GetProject(initial.Id)!.State, initial.State);
            var initialDocument = WorkspaceService.FindDocumentInProject(initial, files[0])!;
            Assert.Equal(texts[0], (await initialDocument.GetTextAsync()).ToString());
            var textVersion = await initialDocument.GetTextVersionAsync();
            var compilation = await initial.GetCompilationAsync();
            Assert.NotNull(compilation);

            // A single new project exercises the incremental loader; two more exercise the batch.
            OpenDocumentStore.Open(session, files[1], SourceText.From(texts[1]), version: 7);
            var (incrementalWorkspace, incremental) = await WorkspaceService.GetOrOpenProjectAsync(
                projects[1], targetFilePath: files[1]);
            Assert.Same(workspace, incrementalWorkspace);
            Assert.Same(workspace.CurrentSolution.GetProject(incremental.Id)!.State, incremental.State);
            Assert.Equal(texts[1], (await WorkspaceService.FindDocumentInProject(incremental, files[1])!.GetTextAsync()).ToString());
            Assert.Same(compilation, await workspace.CurrentSolution.GetProject(initial.Id)!.GetCompilationAsync());

            OpenDocumentStore.Open(session, files[2], SourceText.From(texts[2]), version: 7);
            await WorkspaceService.EnsureProjectsLoadedAsync(projects);
            Assert.Equal(4, workspace.CurrentSolution.ProjectIds.Count);
            // Batch preparation has returned; its temporary disk texts must not be needed to
            // recognize unchanged open buffers when their first request arrives later.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            var (_, batch) = await WorkspaceService.GetOrOpenProjectAsync(projects[2], targetFilePath: files[2]);
            Assert.Same(workspace.CurrentSolution.GetProject(batch.Id)!.State, batch.State);
            Assert.Equal(texts[2], (await WorkspaceService.FindDocumentInProject(batch, files[2])!.GetTextAsync()).ToString());

            var (_, after) = await WorkspaceService.GetOrOpenProjectAsync(projects[0], targetFilePath: files[0]);
            Assert.Same(compilation, await after.GetCompilationAsync());
            Assert.Equal(textVersion, await WorkspaceService.FindDocumentInProject(after, files[0])!.GetTextVersionAsync());

            // An equality proof belongs to both the exact buffer object and document state.
            // It must not conceal a subsequent editor change, or survive a different live text.
            string nextText = texts[2].Replace("int Value", "int LatestValue", StringComparison.Ordinal);
            OpenDocumentStore.Change(files[2], version: 8, _ => SourceText.From(nextText));
            var (_, changed) = await WorkspaceService.GetOrOpenProjectAsync(projects[2], targetFilePath: files[2]);
            var changedDocument = WorkspaceService.FindDocumentInProject(changed, files[2])!;
            Assert.Equal(nextText, (await changedDocument.GetTextAsync()).ToString());

            workspace.OnDocumentTextChanged(changedDocument.Id, SourceText.From("public class Other { }"),
                PreservationMode.PreserveIdentity);
            var (_, restored) = await WorkspaceService.GetOrOpenProjectAsync(projects[2], targetFilePath: files[2]);
            Assert.Equal(nextText,
                (await WorkspaceService.FindDocumentInProject(restored, files[2])!.GetTextAsync()).ToString());
        }
        finally
        {
            OpenDocumentStore.CloseSession(session);
            if (projects.Count > 0)
                await WorkspaceService.EvictProjectForTests(projects[0]);
            // A shared MSBuild host can briefly retain its working directory after evaluation.
            // Cleanup must not conceal the regression assertion if that handle is still closing.
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
