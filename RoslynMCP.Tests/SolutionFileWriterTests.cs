using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.ProjectModel;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Writing the solution file.
/// </summary>
/// <remarks>
/// Every test writes and then reads back through <see cref="SolutionFileService"/> rather than
/// asserting on the text, because the text is not the contract — what Visual Studio and MSBuild
/// make of it is. Each runs against both formats: the two halves of every operation are written
/// separately, so a test that only covers one of them proves half of nothing.
/// </remarks>
public sealed class SolutionFileWriterTests : IDisposable
{
    private const string EmptySln = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Zebra", "Zebra\Zebra.csproj", "{11111111-1111-1111-1111-111111111111}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Alpha", "Alpha\Alpha.csproj", "{22222222-2222-2222-2222-222222222222}"
        EndProject
        Global
        	GlobalSection(SolutionProperties) = preSolution
        		HideSolutionNode = FALSE
        	EndGlobalSection
        EndGlobal

        """;

    private const string EmptySlnx = """
        <Solution>
          <Project Path="Zebra\Zebra.csproj" />
          <Project Path="Alpha\Alpha.csproj" />
        </Solution>
        """;

    private const string MinimalProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"roslyn-sense-slnwriter-{Guid.NewGuid():N}");

    public SolutionFileWriterTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    /// <summary>Writes a two-project solution and returns its path.</summary>
    private string Solution(bool slnx)
    {
        foreach (string name in new[] { "Zebra", "Alpha" })
        {
            Directory.CreateDirectory(Path.Combine(_directory, name));
            File.WriteAllText(Path.Combine(_directory, name, $"{name}.csproj"), MinimalProject);
        }

        string path = Path.Combine(_directory, slnx ? "Test.slnx" : "Test.sln");
        File.WriteAllText(path, slnx ? EmptySlnx : EmptySln);
        return path;
    }

    private string ProjectPath(string name) => Path.Combine(_directory, name, $"{name}.csproj");

    private static SolutionNode Folder(string solution, string name) =>
        SolutionFileService.Read(solution).Single(n => n.IsFolder && n.Name == name);

    private static SolutionNode Project(string solution, string name) =>
        SolutionFileService.Read(solution).Single(n => !n.IsFolder && n.Name == name);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AProjectMovesIntoASolutionFolderAndStaysWhereItIsOnDisk(bool slnx)
    {
        string solution = Solution(slnx);
        SolutionFileWriter.AddFolder(solution, "Shared", null);

        string folderId = Folder(solution, "Shared").Id;
        SolutionFileWriter.MoveProject(solution, ProjectPath("Alpha"), folderId);

        Assert.Equal(folderId, Project(solution, "Alpha").ParentId);
        Assert.Null(Project(solution, "Zebra").ParentId);

        // The whole point of a solution folder: it is not a directory.
        Assert.True(File.Exists(ProjectPath("Alpha")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AProjectMovesBackOutToTheSolutionRoot(bool slnx)
    {
        string solution = Solution(slnx);
        SolutionFileWriter.AddFolder(solution, "Shared", null);
        SolutionFileWriter.MoveProject(solution, ProjectPath("Alpha"), Folder(solution, "Shared").Id);

        SolutionFileWriter.MoveProject(solution, ProjectPath("Alpha"), null);

        Assert.Null(Project(solution, "Alpha").ParentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AProjectMovesStraightFromOneFolderToAnother(bool slnx)
    {
        string solution = Solution(slnx);
        SolutionFileWriter.AddFolder(solution, "First", null);
        SolutionFileWriter.AddFolder(solution, "Second", null);
        SolutionFileWriter.MoveProject(solution, ProjectPath("Alpha"), Folder(solution, "First").Id);

        string second = Folder(solution, "Second").Id;
        SolutionFileWriter.MoveProject(solution, ProjectPath("Alpha"), second);

        // The old parent link has to go, not just be joined by a second one.
        Assert.Equal(second, Project(solution, "Alpha").ParentId);
        Assert.Single(SolutionFileService.Read(solution), n => n.Name == "Alpha");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheTreeShowsSolutionFoldersFirstAndThenProjectsAlphabetically(bool slnx)
    {
        // Written deliberately out of order — the fixture lists Zebra before Alpha, and the
        // folders are added last, so file order and the expected order share nothing.
        string solution = Solution(slnx);
        SolutionFileWriter.AddFolder(solution, "Zulu", null);
        SolutionFileWriter.AddFolder(solution, "Beta", null);

        var children = await SolutionTreeHandler.ChildrenAsync(
            new SolutionTreeParams(NodeId: $"solution:{solution}"), default);

        Assert.Equal(["Beta", "Zulu", "Alpha", "Zebra"], children.Select(c => c.Label));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFileAttachedToASolutionFolderIsThereWhenTheSolutionIsReadBack(bool slnx)
    {
        string solution = Solution(slnx);
        string file = Path.Combine(_directory, "README.md");
        File.WriteAllText(file, "# Test");

        SolutionFileWriter.AddFolder(solution, "Docs", null);
        SolutionFileWriter.AddSolutionItem(solution, Folder(solution, "Docs").Id, file);

        Assert.Equal([file], Folder(solution, "Docs").Files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AttachingTheSameFileTwiceLeavesOneEntry(bool slnx)
    {
        string solution = Solution(slnx);
        string file = Path.Combine(_directory, "README.md");
        File.WriteAllText(file, "# Test");

        SolutionFileWriter.AddFolder(solution, "Docs", null);
        string folderId = Folder(solution, "Docs").Id;
        SolutionFileWriter.AddSolutionItem(solution, folderId, file);
        SolutionFileWriter.AddSolutionItem(solution, folderId, file);

        Assert.Single(Folder(solution, "Docs").Files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetachingASolutionItemLeavesTheFileOnDisk(bool slnx)
    {
        string solution = Solution(slnx);
        string file = Path.Combine(_directory, "README.md");
        File.WriteAllText(file, "# Test");

        SolutionFileWriter.AddFolder(solution, "Docs", null);
        string folderId = Folder(solution, "Docs").Id;
        SolutionFileWriter.AddSolutionItem(solution, folderId, file);
        SolutionFileWriter.RemoveSolutionItem(solution, folderId, file);

        Assert.Empty(Folder(solution, "Docs").Files);
        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenamingASolutionFolderKeepsWhatIsInsideIt(bool slnx)
    {
        string solution = Solution(slnx);
        SolutionFileWriter.AddFolder(solution, "Shared", null);
        SolutionFileWriter.MoveProject(solution, ProjectPath("Alpha"), Folder(solution, "Shared").Id);

        SolutionFileWriter.RenameFolder(solution, Folder(solution, "Shared").Id, "Common");

        var renamed = Folder(solution, "Common");
        Assert.Equal(renamed.Id, Project(solution, "Alpha").ParentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingASolutionFolderMovesWhatWasInsideItUpALevel(bool slnx)
    {
        string solution = Solution(slnx);
        SolutionFileWriter.AddFolder(solution, "Outer", null);
        SolutionFileWriter.AddFolder(solution, "Inner", Folder(solution, "Outer").Id);
        SolutionFileWriter.MoveProject(solution, ProjectPath("Alpha"), Folder(solution, "Inner").Id);

        SolutionFileWriter.RemoveFolder(solution, Folder(solution, "Inner").Id);

        // Removing a grouping is not a reason to lose what was grouped.
        Assert.Equal(Folder(solution, "Outer").Id, Project(solution, "Alpha").ParentId);
        Assert.DoesNotContain(SolutionFileService.Read(solution), n => n.Name == "Inner");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingATopLevelFolderLeavesItsProjectsAtTheRoot(bool slnx)
    {
        string solution = Solution(slnx);
        SolutionFileWriter.AddFolder(solution, "Shared", null);
        SolutionFileWriter.MoveProject(solution, ProjectPath("Alpha"), Folder(solution, "Shared").Id);

        SolutionFileWriter.RemoveFolder(solution, Folder(solution, "Shared").Id);

        Assert.Null(Project(solution, "Alpha").ParentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANestedFolderIsWrittenUnderItsParent(bool slnx)
    {
        string solution = Solution(slnx);
        SolutionFileWriter.AddFolder(solution, "Outer", null);
        SolutionFileWriter.AddFolder(solution, "Inner", Folder(solution, "Outer").Id);

        Assert.Equal(Folder(solution, "Outer").Id, Folder(solution, "Inner").ParentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingAFolderTakesItsProjectsAndItsOwnChildrenWithIt(bool slnx)
    {
        string solution = Solution(slnx);
        SolutionFileWriter.AddFolder(solution, "Shared", null);
        SolutionFileWriter.AddFolder(solution, "Utilities", Folder(solution, "Shared").Id);
        SolutionFileWriter.MoveProject(solution, ProjectPath("Alpha"), Folder(solution, "Utilities").Id);
        SolutionFileWriter.AddFolder(solution, "Source", null);

        SolutionFileWriter.MoveFolder(
            solution, Folder(solution, "Shared").Id, Folder(solution, "Source").Id);

        Assert.Equal(Folder(solution, "Source").Id, Folder(solution, "Shared").ParentId);
        Assert.Equal(Folder(solution, "Shared").Id, Folder(solution, "Utilities").ParentId);
        Assert.Equal(Folder(solution, "Utilities").Id, Project(solution, "Alpha").ParentId);
    }

    /// <summary>
    /// Paths are matched without regard to case, which makes a rename that only changes the case
    /// look like a rename to where the folder already is.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFolderCanBeRenamedToADifferentCaseOfItsOwnName(bool slnx)
    {
        string solution = Solution(slnx);
        SolutionFileWriter.AddFolder(solution, "shared", null);
        SolutionFileWriter.MoveProject(solution, ProjectPath("Alpha"), Folder(solution, "shared").Id);

        SolutionFileWriter.RenameFolder(solution, Folder(solution, "shared").Id, "Shared");

        var folder = SolutionFileService.Read(solution).Single(n => n.IsFolder);
        Assert.Equal("Shared", folder.Name);
        Assert.Equal(folder.Id, Project(solution, "Alpha").ParentId);
    }

    /// <summary>
    /// Reparenting onto an existing folder merges the two, which is a fine thing for a move to do
    /// and never what a rename meant.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenamingAFolderOntoASiblingThatAlreadyExistsIsRefused(bool slnx)
    {
        string solution = Solution(slnx);
        SolutionFileWriter.AddFolder(solution, "Docs", null);
        SolutionFileWriter.AddFolder(solution, "Guides", null);

        Assert.Throws<InvalidOperationException>(
            () => SolutionFileWriter.RenameFolder(solution, Folder(solution, "Guides").Id, "Docs"));

        Assert.Equal(2, SolutionFileService.Read(solution).Count(n => n.IsFolder));
    }

    /// <summary>
    /// A solution item in a top-level folder has nowhere to go when that folder is removed — the
    /// format cannot list a file outside a folder. The count is how the caller knows to say so
    /// instead of promising that nothing was lost.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingATopLevelFolderReportsTheSolutionItemsItCouldNotKeep(bool slnx)
    {
        string solution = Solution(slnx);
        string file = Path.Combine(_directory, "README.md");
        File.WriteAllText(file, "# Test");

        SolutionFileWriter.AddFolder(solution, "Docs", null);
        SolutionFileWriter.AddSolutionItem(solution, Folder(solution, "Docs").Id, file);

        int detached = SolutionFileWriter.RemoveFolder(solution, Folder(solution, "Docs").Id);

        Assert.Equal(1, detached);
        Assert.True(File.Exists(file));
    }

    /// <summary>A nested folder's items do have somewhere to go, and go there.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingANestedFolderMovesItsSolutionItemsUpALevel(bool slnx)
    {
        string solution = Solution(slnx);
        string file = Path.Combine(_directory, "README.md");
        File.WriteAllText(file, "# Test");

        SolutionFileWriter.AddFolder(solution, "Outer", null);
        SolutionFileWriter.AddFolder(solution, "Inner", Folder(solution, "Outer").Id);
        SolutionFileWriter.AddSolutionItem(solution, Folder(solution, "Inner").Id, file);

        int detached = SolutionFileWriter.RemoveFolder(solution, Folder(solution, "Inner").Id);

        Assert.Equal(0, detached);
        Assert.Equal([file], Folder(solution, "Outer").Files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingAFolderIntoItselfIsRefused(bool slnx)
    {
        string solution = Solution(slnx);
        SolutionFileWriter.AddFolder(solution, "Shared", null);
        string folderId = Folder(solution, "Shared").Id;

        Assert.Throws<InvalidOperationException>(
            () => SolutionFileWriter.MoveFolder(solution, folderId, folderId));
    }

    [Fact]
    public void MovingAProjectThatIsNotInTheSolutionSaysSo()
    {
        string solution = Solution(slnx: false);
        SolutionFileWriter.AddFolder(solution, "Shared", null);

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            SolutionFileWriter.MoveProject(
                solution, Path.Combine(_directory, "Ghost", "Ghost.csproj"),
                Folder(solution, "Shared").Id));

        Assert.Contains("not in the solution", thrown.Message);
    }
}
