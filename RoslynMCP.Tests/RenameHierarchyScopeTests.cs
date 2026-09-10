using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public sealed class RenameHierarchyScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberRenameIncludesIndependentImplementationsAndInterfaceOnlyCallers(bool inherited)
    {
        string directory = Path.Combine(Path.GetTempPath(), "rename-hierarchy-" + Guid.NewGuid().ToString("N"));
        string solutionPath = Path.Combine(directory, "Hierarchy.slnx");
        string firstProject = Path.Combine(directory, "First", "First.csproj");
        string secondProject = Path.Combine(directory, "Second", "Second.csproj");
        string firstPath = Path.Combine(directory, "First", "FirstRecord.cs");
        string secondPath = Path.Combine(directory, "Second", "SecondRecord.cs");
        string contractPath = Path.Combine(directory, "Contracts", "IRecord.cs");
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [contractPath] = "public interface IRecord { int Name { get; } }",
            [firstPath] = inherited
                ? "public class BaseRecord { public int Name => 1; } public class FirstRecord : BaseRecord, IRecord { }"
                : "public class FirstRecord : IRecord { public int Name => 1; }",
            [secondPath] = "public class SecondRecord : IRecord { public int Name => 2; public int Read(IRecord value) => value.Name; }",
        };

        try
        {
            foreach (string projectName in new[] { "Contracts", "First", "Second" })
            {
                string projectDirectory = Path.Combine(directory, projectName);
                Directory.CreateDirectory(projectDirectory);
                string reference = projectName == "Contracts" ? "" : """
                    <ItemGroup><ProjectReference Include="../Contracts/Contracts.csproj" /></ItemGroup>
                    """;
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, projectName + ".csproj"), $$"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                      {{reference}}
                    </Project>
                    """);
            }
            await File.WriteAllTextAsync(solutionPath, """
                <Solution>
                  <Project Path="Contracts/Contracts.csproj" />
                  <Project Path="First/First.csproj" />
                  <Project Path="Second/Second.csproj" />
                </Solution>
                """);
            foreach (var (path, source) in sources)
                await File.WriteAllTextAsync(path, source);

            using var binding = WorkspaceService.BindSolutionForTesting(solutionPath);
            var origin = await LspDocumentResolver.ResolveAsync(firstPath, default);
            Assert.NotNull(origin);
            Assert.DoesNotContain(origin.Project.Solution.Projects,
                project => string.Equals(project.FilePath, secondProject, StringComparison.OrdinalIgnoreCase));
            var originalCompilation = await origin.Project.GetCompilationAsync();
            Assert.NotNull(originalCompilation);
            Assert.Empty(originalCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

            var text = SourceText.From(sources[firstPath]);
            var position = text.Lines.GetLinePosition(sources[firstPath].IndexOf("Name", StringComparison.Ordinal));
            var edit = await RenameHandler.RenameAsync(new RenameParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(firstPath)),
                new Position(position.Line, position.Character), "RenamedName"), default, LanguageSession.Empty);
            Assert.NotNull(edit?.Changes);
            Assert.Equal(3, edit!.Changes!.Count);

            // Apply Roslyn's minimal edits to all three source snapshots. The second implementation
            // and its interface-typed call do not reference First, so its reverse closure misses both.
            var current = await LspDocumentResolver.ResolveAsync(firstPath, default);
            Assert.NotNull(current);
            var renamed = current.Project.Solution;
            foreach (var (path, source) in sources)
            {
                Assert.True(edit.Changes.TryGetValue(LspConverters.PathToUri(path), out var changes),
                    "Rename omitted " + Path.GetFileName(path));
                var oldText = SourceText.From(source);
                var newText = oldText.WithChanges(changes!.Select(change => new TextChange(
                    TextSpan.FromBounds(LspConverters.ToOffset(oldText, change.Range.Start),
                        LspConverters.ToOffset(oldText, change.Range.End)), change.NewText)));
                Assert.Equal(source.Replace("Name", "RenamedName", StringComparison.Ordinal), newText.ToString());
                var id = Assert.Single(renamed.GetDocumentIdsWithFilePath(path));
                renamed = renamed.WithDocumentText(id, newText);
                Assert.Equal(source, await File.ReadAllTextAsync(path));
            }
            Assert.Equal(3, renamed.ProjectIds.Count);
            foreach (var project in renamed.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                Assert.NotNull(compilation);
                Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            }
        }
        finally
        {
            await WorkspaceService.EvictProjectForTests(firstProject);
            // Pooled build hosts can keep the evaluated project directory open after its Roslyn
            // workspace is evicted. Cleanup must not replace the actual rename assertion failure.
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
