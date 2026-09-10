using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;
using Xunit;

namespace RoslynMCP.Tests;

public class SolutionProjectIndexTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindsLinkedFilesWithCanonicalOrEquivalentSpelling(bool dotSegments)
    {
        using var workspace = new AdhocWorkspace();
        string root = Path.Combine(Path.GetTempPath(), "ownership");
        string projectPath = Path.Combine(root, "Library", "Library.csproj");
        string linkedPath = Path.Combine(root, "Shared", "Linked.cs");
        string storedPath = dotSegments
            ? Path.Combine(root, "Shared", "..", "Shared", "Linked.cs")
            : linkedPath;
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), "Library", "Library",
            LanguageNames.CSharp, filePath: projectPath));
        var solution = project.Solution.AddDocument(DocumentId.CreateNewId(project.Id),
            "Linked.cs", SourceText.From("class Linked { }"), filePath: storedPath);

        Assert.Equal(projectPath,
            SolutionProjectIndex.LoadedProjectForFile(solution, PathHelper.NormalizePath(linkedPath)));
    }

    [Fact]
    public void AChangedSolutionCannotReturnThePreviousDocumentOwner()
    {
        using var workspace = new AdhocWorkspace();
        string root = Path.Combine(Path.GetTempPath(), "ownership");
        string path = Path.Combine(root, "Shared.cs");
        var first = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), "First", "First",
            LanguageNames.CSharp, filePath: Path.Combine(root, "First.csproj")));
        var second = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), "Second", "Second",
            LanguageNames.CSharp, filePath: Path.Combine(root, "Second.csproj")));
        var firstDocument = DocumentId.CreateNewId(first.Id);
        var original = workspace.CurrentSolution.AddDocument(firstDocument,
            "Shared.cs", SourceText.From("class Shared { }"), filePath: path);
        Assert.Equal(first.FilePath, SolutionProjectIndex.LoadedProjectForFile(original, path));

        var removed = original.RemoveDocument(firstDocument);
        Assert.Null(SolutionProjectIndex.LoadedProjectForFile(removed, path));
        var moved = removed.AddDocument(DocumentId.CreateNewId(second.Id),
            "Shared.cs", SourceText.From("class Shared { }"), filePath: path);
        Assert.Equal(second.FilePath, SolutionProjectIndex.LoadedProjectForFile(moved, path));
    }
}
