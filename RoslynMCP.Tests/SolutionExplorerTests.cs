using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>The Solution Explorer's foundations: solution-folder parsing, MSBuild item-model
/// evaluation, and file nesting.</summary>
public class SolutionExplorerTests
{
    // ---- File nesting ------------------------------------------------------------------

    [Theory]
    [InlineData("Form1.Designer.cs", "Form1.cs")]
    [InlineData("Form1.designer.cs", "Form1.cs")]
    [InlineData("Model.g.cs", "Model.cs")]
    [InlineData("Counter.razor.cs", "Counter.razor")]
    [InlineData("Counter.razor.css", "Counter.razor")]
    [InlineData("Index.cshtml.cs", "Index.cshtml")]
    [InlineData("Default.aspx.cs", "Default.aspx")]
    [InlineData("Window.xaml.cs", "Window.xaml")]
    [InlineData("appsettings.Development.json", "appsettings.json")]
    [InlineData("package-lock.json", "package.json")]
    [InlineData("Directory.Build.targets", "Directory.Build.props")]
    public void NestingRulesInferTheExpectedParent(string child, string expectedParent) =>
        Assert.Equal(expectedParent, FileNestingService.InferParentName(child));

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("README.md")]
    [InlineData("appsettings.json")]
    public void StandaloneFilesInferNoParent(string fileName) =>
        Assert.Null(FileNestingService.InferParentName(fileName));

    [Fact]
    public void NestingGroupsChildrenUnderTheirParent()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nest-{Guid.NewGuid():N}");
        string[] files =
        [
            Path.Combine(dir, "Form1.cs"),
            Path.Combine(dir, "Form1.Designer.cs"),
            Path.Combine(dir, "Form1.resx"),
            Path.Combine(dir, "Program.cs"),
        ];

        var nested = FileNestingService.Nest(files,
            dependentUpon: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [files[2]] = "Form1.cs",
            });

        Assert.Equal(2, nested.Count); // Form1.cs and Program.cs
        var form = nested.Single(n => Path.GetFileName(n.FullPath) == "Form1.cs");
        Assert.Equal(2, form.Children.Count);
        Assert.Contains(form.Children, c => Path.GetFileName(c.FullPath) == "Form1.Designer.cs");
        Assert.Contains(form.Children, c => Path.GetFileName(c.FullPath) == "Form1.resx");
    }

    [Fact]
    public void AnOrphanedChildStaysAtTheTopLevel()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nest-{Guid.NewGuid():N}");
        string[] files = [Path.Combine(dir, "Form1.Designer.cs")];

        var nested = FileNestingService.Nest(files);

        // Its parent is not in the set; hiding it entirely would be worse than not nesting it.
        Assert.Single(nested);
        Assert.Equal("Form1.Designer.cs", Path.GetFileName(nested[0].FullPath));
    }

    [Fact]
    public void NestingCanBeTurnedOff()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nest-{Guid.NewGuid():N}");
        string[] files = [Path.Combine(dir, "Form1.cs"), Path.Combine(dir, "Form1.Designer.cs")];

        var nested = FileNestingService.Nest(files, enabled: false);

        Assert.Equal(2, nested.Count);
        Assert.All(nested, n => Assert.Empty(n.Children));
    }

    [Fact]
    public void ExplicitDependentUponBeatsTheRules()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nest-{Guid.NewGuid():N}");
        string designer = Path.Combine(dir, "Form1.Designer.cs");
        string[] files = [Path.Combine(dir, "Form1.cs"), Path.Combine(dir, "Other.cs"), designer];

        // The project says the designer belongs to Other.cs; the naming rule says Form1.cs.
        var nested = FileNestingService.Nest(files,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [designer] = "Other.cs" });

        var other = nested.Single(n => Path.GetFileName(n.FullPath) == "Other.cs");
        Assert.Single(other.Children);
        Assert.Equal(designer, other.Children[0].FullPath);
    }

    [Fact]
    public async Task AProjectWithOverlappingIncludesStillListsItsFiles()
    {
        // An explicit include that overlaps the SDK's default glob evaluates the same file
        // twice. Keying the item map with ToDictionary threw on that, and since the tree
        // request had no error path the client read the failure as an empty project — no
        // files, no folders, no message, and unchanged by Show All Files.
        string dir = Path.Combine(Path.GetTempPath(), $"dupe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "Assets"));
        File.WriteAllText(Path.Combine(dir, "Assets", "logo.png"), "");
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "class Program { static void Main() { } }");

        string project = Path.Combine(dir, "Dupe.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <None Include="Assets\**\*" CopyToOutputDirectory="PreserveNewest" />
              </ItemGroup>
            </Project>
            """);

        try
        {
            var nodes = await SolutionTreeHandler.ChildrenAsync(
                new SolutionTreeParams(NodeId: $"project:{project}"), default);

            Assert.Contains(nodes, n => n.Kind == SolutionNodeKind.Dependencies);
            Assert.Contains(nodes, n => n.Label == "Program.cs");
            Assert.Contains(nodes, n => n.Kind == SolutionNodeKind.Folder && n.Label == "Assets");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---- Solution structure ------------------------------------------------------------

    [Fact]
    public void SolutionFoldersAndTheirNestingAreRead()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sln-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "Lib"));
        File.WriteAllText(Path.Combine(dir, "Lib", "Lib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "solution item");

        string solutionPath = Path.Combine(dir, "Nested.sln");
        File.WriteAllText(solutionPath, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Services", "Services", "{11111111-1111-1111-1111-111111111111}"
            	ProjectSection(SolutionItems) = preProject
            		notes.txt = notes.txt
            	EndProjectSection
            EndProject
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Lib", "Lib\Lib.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Global
            	GlobalSection(NestedProjects) = preSolution
            		{22222222-2222-2222-2222-222222222222} = {11111111-1111-1111-1111-111111111111}
            	EndGlobalSection
            EndGlobal
            """);

        try
        {
            var nodes = SolutionFileService.Read(solutionPath);

            var folder = nodes.Single(n => n.IsFolder);
            Assert.Equal("Services", folder.Name);
            Assert.Null(folder.ParentId);
            Assert.Contains(folder.Files, f => Path.GetFileName(f) == "notes.txt");

            var project = nodes.Single(n => !n.IsFolder);
            Assert.Equal("Lib", project.Name);
            // Roslyn's model has no solution folders at all; this parent link is the whole point.
            Assert.Equal(folder.Id, project.ParentId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SlnxFolderStructureIsRead()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"slnx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "App"));
        string solutionPath = Path.Combine(dir, "App.slnx");
        File.WriteAllText(solutionPath, """
            <Solution>
              <Folder Name="/Services/">
                <Project Path="App/App.csproj" />
              </Folder>
            </Solution>
            """);

        try
        {
            var nodes = SolutionFileService.Read(solutionPath);

            var folder = nodes.Single(n => n.IsFolder);
            Assert.Equal("Services", folder.Name);
            var project = nodes.Single(n => !n.IsFolder);
            Assert.Equal(folder.Id, project.ParentId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ASolutionFolderCanBeAddedToBothFormats()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"addfolder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        string sln = Path.Combine(dir, "App.sln");
        File.WriteAllText(sln, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Services", "Services", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Global
            EndGlobal
            """);

        string slnx = Path.Combine(dir, "App.slnx");
        File.WriteAllText(slnx, """
            <Solution>
              <Folder Name="/Services/" />
            </Solution>
            """);

        try
        {
            // Root level in .sln.
            Assert.True(Add(sln, "Benchmark", parent: null).Ok);
            var slnNodes = SolutionFileService.Read(sln);
            var benchmark = slnNodes.Single(n => n.Name == "Benchmark");
            Assert.True(benchmark.IsFolder);
            Assert.Null(benchmark.ParentId);

            // Nested in .sln, which also needs the NestedProjects section creating.
            Assert.True(Add(sln, "Inner", parent: "{11111111-1111-1111-1111-111111111111}").Ok);
            var nested = SolutionFileService.Read(sln).Single(n => n.Name == "Inner");
            Assert.Equal("{11111111-1111-1111-1111-111111111111}", nested.ParentId);

            // Root level and nested in .slnx.
            Assert.True(Add(slnx, "Benchmark", parent: null).Ok);
            Assert.True(Add(slnx, "Inner", parent: "/Services").Ok);
            var slnxNodes = SolutionFileService.Read(slnx);
            Assert.Null(slnxNodes.Single(n => n.Name == "Benchmark").ParentId);
            Assert.Equal("/Services", slnxNodes.Single(n => n.Name == "Inner").ParentId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        static SolutionTreeEditResult Add(string solution, string name, string? parent) =>
            SolutionTreeEditHandler.EditAsync(
                new SolutionTreeEditParams(
                    Action: "addSolutionFolder",
                    TargetUri: LspConverters.PathToUri(solution),
                    ProjectPath: parent,
                    Name: name),
                default).GetAwaiter().GetResult();
    }

    [Fact]
    public void SlnxSolutionItemsAreRead()
    {
        // The .slnx equivalent of ProjectSection(SolutionItems). Folders were read from this
        // format from the start; the files attached to them were not, so a .slnx solution
        // showed its structure with everything hanging off it missing.
        string dir = Path.Combine(Path.GetTempPath(), $"slnx-items-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "App"));
        string solutionPath = Path.Combine(dir, "App.slnx");
        File.WriteAllText(solutionPath, """
            <Solution>
              <Folder Name="/Solution Items/">
                <File Path="Directory.Build.props" />
                <File Path="docs/README.md" />
              </Folder>
              <Folder Name="/Services/">
                <Project Path="App/App.csproj" />
              </Folder>
            </Solution>
            """);

        try
        {
            var nodes = SolutionFileService.Read(solutionPath);

            var items = nodes.Single(n => n.Name == "Solution Items");
            Assert.Equal(2, items.Files.Count);
            Assert.Contains(items.Files, f => Path.GetFileName(f) == "Directory.Build.props");
            Assert.Contains(items.Files, f => Path.GetFileName(f) == "README.md");
            Assert.All(items.Files, f => Assert.True(Path.IsPathFullyQualified(f)));

            // A folder holding only projects still reports none.
            Assert.Empty(nodes.Single(n => n.Name == "Services").Files);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AMissingOrMalformedSolutionYieldsNothingRatherThanThrowing()
    {
        Assert.Empty(SolutionFileService.Read(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.sln")));

        string broken = Path.Combine(Path.GetTempPath(), $"broken-{Guid.NewGuid():N}.sln");
        File.WriteAllText(broken, "this is not a solution");
        try { Assert.Empty(SolutionFileService.Read(broken)); }
        finally { File.Delete(broken); }
    }

    // ---- Project evaluation ------------------------------------------------------------

    [Fact]
    public async Task EvaluationExposesItemsPackagesAndImports()
    {
        var evaluation = await ProjectEvaluationService.EvaluateAsync(FixturePaths.SampleProjectFile);

        Assert.NotNull(evaluation);
        Assert.NotEmpty(evaluation!.TargetFrameworks);
        Assert.Contains(evaluation.Items, i =>
            Path.GetFileName(i.FullPath).Equals("Calculator.cs", StringComparison.OrdinalIgnoreCase));
        // Imports are what the Dependencies > Imports node lists; every SDK project has some.
        Assert.NotEmpty(evaluation.Imports);
    }

    [Fact]
    public async Task EvaluationReadsDependentUponMetadata()
    {
        var evaluation = await ProjectEvaluationService.EvaluateAsync(FixturePaths.WebFormsSiteFile);
        if (evaluation is null)
            return; // legacy project needs VS MSBuild; skip where that is unavailable

        // The whole reason for evaluating the item model rather than using Roslyn's documents.
        Assert.Contains(evaluation.Items, i => i.DependentUpon is { Length: > 0 });
    }

    [Fact]
    public async Task EvaluationIsCachedUntilTheProjectFileChanges()
    {
        ProjectEvaluationService.Clear();

        var first = await ProjectEvaluationService.EvaluateAsync(FixturePaths.SampleProjectFile);
        var second = await ProjectEvaluationService.EvaluateAsync(FixturePaths.SampleProjectFile);

        Assert.NotNull(first);
        Assert.Same(first, second);

        ProjectEvaluationService.Evict(FixturePaths.SampleProjectFile);
        var third = await ProjectEvaluationService.EvaluateAsync(FixturePaths.SampleProjectFile);
        Assert.NotSame(first, third);
    }

    [Fact]
    public async Task EvaluationOfAMissingProjectReturnsNull() =>
        Assert.Null(await ProjectEvaluationService.EvaluateAsync(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.csproj")));

    // ---- Tree handler ------------------------------------------------------------------

    [Fact]
    public async Task ProjectExpandsToDependenciesAndItsFiles()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        var children = await SolutionTreeHandler.ChildrenAsync(
            new SolutionTreeParams($"project:{FixturePaths.SampleProjectFile}"), default);

        Assert.Contains(children, n => n.Kind == SolutionNodeKind.Dependencies);
        Assert.Contains(children, n =>
            n.Kind == SolutionNodeKind.File &&
            n.Label.Equals("Calculator.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DependenciesExpandToTheGroupsRiderShows()
    {
        var groups = await SolutionTreeHandler.ChildrenAsync(
            new SolutionTreeParams($"{FixturePaths.SampleProjectFile}!deps"), default);

        Assert.Contains(groups, g => g.Kind == SolutionNodeKind.Imports);
        Assert.Contains(groups, g => g.Kind == SolutionNodeKind.Framework);
        Assert.All(groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Label)));
    }

    [Fact]
    public async Task ShowAllFilesRevealsNonProjectFilesDimmed()
    {
        string stray = Path.Combine(FixturePaths.SampleProjectDir, $"stray-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(stray, "not part of the project");
        try
        {
            var hidden = await SolutionTreeHandler.ChildrenAsync(
                new SolutionTreeParams($"project:{FixturePaths.SampleProjectFile}"), default);
            Assert.DoesNotContain(hidden, n => n.Label == Path.GetFileName(stray));

            var shown = await SolutionTreeHandler.ChildrenAsync(
                new SolutionTreeParams($"project:{FixturePaths.SampleProjectFile}", ShowAllFiles: true),
                default);
            var node = shown.SingleOrDefault(n => n.Label == Path.GetFileName(stray));
            Assert.NotNull(node);
            Assert.True(node!.Dimmed);
        }
        finally
        {
            File.Delete(stray);
        }
    }

    [Fact]
    public async Task FilterKeepsOnlyMatchesAndReportsWhereTheyMatched()
    {
        var filtered = await SolutionTreeHandler.ChildrenAsync(
            new SolutionTreeParams($"project:{FixturePaths.SampleProjectFile}", Filter: "calc"),
            default);

        Assert.NotEmpty(filtered);
        Assert.All(filtered, n =>
            Assert.Contains("calc", n.Label, StringComparison.OrdinalIgnoreCase));
        Assert.All(filtered, n => Assert.NotNull(n.Highlights));
    }
}
