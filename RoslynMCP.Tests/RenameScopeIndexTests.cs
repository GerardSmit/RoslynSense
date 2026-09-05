using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public sealed class RenameScopeIndexTests
{
    [Theory]
    [InlineData("public sealed class Example { public int Value { get; set; } }", true)]
    [InlineData("public class Example { public int Value { get; set; } }", false)]
    [InlineData("public interface IExample { int Value { get; } } public sealed class Example : IExample { public int Value { get; } }", false)]
    [InlineData("public class Base { public virtual int Value { get; } } public sealed class Example : Base { public override int Value { get; } }", false)]
    public void NarrowingRequiresProofThatTheMemberCannotCascade(string source, bool expected)
    {
        var compilation = CSharpCompilation.Create("test", [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var symbol = compilation.GetTypeByMetadataName("Example")!.GetMembers("Value").Single();
        Assert.Equal(expected, RenameScopeIndex.CanNarrow(symbol));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvaluatedConsumerScopeExcludesUnrelatedProjectsAndRejectsStaleGraphs(bool importedReference)
    {
        string root = Path.Combine(Path.GetTempPath(), "rename-scope-index-" + Guid.NewGuid().ToString("N"));
        string sln = Path.Combine(root, "Scope.slnx");
        string originProject = Path.Combine(root, "Origin", "Origin.csproj");
        string originFile = Path.Combine(root, "Origin", "Code.cs");
        string consumerFile = Path.Combine(root, "Consumer", "Code.cs");
        string unrelatedProject = Path.Combine(root, "Unrelated", "Unrelated.csproj");
        var paths = new[] { "Origin", "Consumer", "Unrelated" }
            .Select(name => Path.Combine(root, name, name + ".csproj")).ToArray();
        try
        {
            foreach (string path in paths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string reference = !importedReference && path.Contains("Consumer", StringComparison.Ordinal)
                    ? "<ItemGroup><ProjectReference Include=\"../Origin/Origin.csproj\" /></ItemGroup>" : "";
                await File.WriteAllTextAsync(path, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                    + "<TargetFramework>net10.0</TargetFramework></PropertyGroup>" + reference + "</Project>");
            }
            if (importedReference)
                await File.WriteAllTextAsync(Path.Combine(root, "Directory.Build.props"), """
                    <Project><ItemGroup Condition="'$(MSBuildProjectName)' == 'Consumer'">
                    <ProjectReference Include="$(MSBuildThisFileDirectory)Origin/Origin.csproj" />
                    </ItemGroup></Project>
                    """);
            await File.WriteAllTextAsync(sln, "<Solution>" + string.Concat(paths.Select(p =>
                "<Project Path=\"" + Path.GetRelativePath(root, p).Replace('\\', '/') + "\" />")) + "</Solution>");
            await File.WriteAllTextAsync(originFile, "public sealed class Record { public int Value => 1; }");
            await File.WriteAllTextAsync(consumerFile, "public class Caller { public int Read(Record item) => item.Value; }");
            await File.WriteAllTextAsync(Path.Combine(root, "Unrelated", "Code.cs"), "public class Other { public int Value => 2; }");
            using var binding = WorkspaceService.BindSolutionForTesting(sln);
            await WorkspaceService.EnsureProjectsLoadedAsync(paths);
            await EvaluationCache.WhenStoresIdleAsync();
            await WorkspaceService.EvictProjectForTests(originProject);
            var document = await LspDocumentResolver.ResolveAsync(originFile, default);
            Assert.NotNull(document);
            var compilation = await document.Project.GetCompilationAsync();
            var symbol = compilation!.GetTypeByMetadataName("Record")!.GetMembers("Value").Single();
            var scope = RenameScopeIndex.TryNarrow(document.Project, symbol, sln, paths, default);
            Assert.NotNull(scope);
            Assert.Equal(2, scope.Count);
            Assert.DoesNotContain(unrelatedProject, scope);

            var text = await document.GetTextAsync();
            var line = text.Lines.GetLinePosition(text.ToString().IndexOf("Value", StringComparison.Ordinal));
            var edit = await RenameHandler.RenameAsync(new RenameParams(new(LspConverters.PathToUri(originFile)),
                new(line.Line, line.Character), "Number"), default, LanguageSession.Empty);
            Assert.NotNull(edit);
            Assert.Equal(2, edit.Changes.Count);
            Assert.Contains("item.Number", ProtoRenameTests.Apply(consumerFile, edit));
            var current = await LspDocumentResolver.ResolveAsync(originFile, default);
            Assert.DoesNotContain(current!.Project.Solution.Projects, p => p.FilePath == unrelatedProject);

            await File.AppendAllTextAsync(unrelatedProject, "\n<!-- graph changed -->");
            Assert.Null(RenameScopeIndex.TryNarrow(document.Project, symbol, sln, paths, default));
        }
        finally
        {
            await WorkspaceService.EvictProjectForTests(originProject);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
