using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>The Solution Explorer's foundations: solution-folder parsing, MSBuild item-model
/// evaluation, and file nesting.</summary>
[Collection(SharedState.Name)]
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
    [InlineData("Strings.nl.resx", "Strings.resx")]
    [InlineData("Properties.de-DE.resx", "Properties.resx")]
    [InlineData("Global.ascx.de-DE.resx", "Global.ascx.resx")]
    [InlineData("View.ascx.nl-NL.Portal-3.resx", "View.ascx.resx")]
    [InlineData("View.ascx.Host.resx", "View.ascx.resx")]
    public void NestingRulesInferTheExpectedParent(string child, string expectedParent) =>
        Assert.Equal(expectedParent, FileNestingService.InferParentName(child));

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("README.md")]
    [InlineData("appsettings.json")]
    [InlineData("Global.ascx.resx")]
    [InlineData("Properties.resx")]
    // Company is a well-formed language subtag, so ICU hands back a neutral custom culture rather
    // than throwing. Only the segment rules keep this a base file.
    [InlineData("My.Company.Strings.resx")]
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

    /// <summary>
    /// Revealing a nested file has to go through the file it is nested under.
    /// </summary>
    /// <remarks>
    /// With nesting on the folder lists Form1.cs and not Form1.Designer.cs, so a reveal chain
    /// that jumps from the folder to the designer file names a row the tree never draws — which
    /// is how "select the file I am editing" did nothing for every designer, resource and
    /// <c>appsettings.*.json</c> file while working fine for a plain one.
    /// </remarks>
    [Fact]
    public void RevealGoesThroughTheFileANestedFileHangsUnder()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"reveal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string name in new[] { "Form1.cs", "Form1.Designer.cs", "Program.cs" })
                File.WriteAllText(Path.Combine(dir, name), "");

            string project = Path.Combine(dir, "Unevaluated.csproj");
            string designer = Path.Combine(dir, "Form1.Designer.cs");

            Assert.Equal(
                [Path.Combine(dir, "Form1.cs")],
                SolutionTreeHandler.NestingAncestorsOf(project, designer, nesting: true));

            // Nesting off puts every file straight in its folder, so there is nothing in between.
            Assert.Empty(SolutionTreeHandler.NestingAncestorsOf(project, designer, nesting: false));
            Assert.Empty(SolutionTreeHandler.NestingAncestorsOf(
                project, Path.Combine(dir, "Program.cs"), nesting: true));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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

    [Fact]
    public async Task AFolderCreatedFromTheTreeShowsUpImmediately()
    {
        // It starts empty, and "Project items" mode used to require project content in a
        // directory before showing it — so a folder disappeared the moment it was created and
        // looked like the command had failed.
        string dir = Path.Combine(Path.GetTempPath(), $"newfolder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "class Program { }");

        string project = Path.Combine(dir, "App.csproj");
        File.WriteAllText(project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        try
        {
            var created = await SolutionTreeEditHandler.EditAsync(
                new SolutionTreeEditParams(
                    Action: "addFolder",
                    TargetUri: LspConverters.PathToUri(project),
                    Name: "Handlers"),
                default);
            Assert.True(created.Ok, created.Message);

            var nodes = await SolutionTreeHandler.ChildrenAsync(
                new SolutionTreeParams(NodeId: $"project:{project}"), default);

            Assert.Contains(nodes, n =>
                n.Kind == SolutionNodeKind.Folder && n.Label == "Handlers");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AFolderHoldingOnlyExcludedFilesStaysHidden()
    {
        // The counterpart: showing empty folders must not turn into showing every folder.
        string dir = Path.Combine(Path.GetTempPath(), $"excluded-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "Excluded"));
        File.WriteAllText(Path.Combine(dir, "Excluded", "Old.cs"), "class Old { }");
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "class Program { }");

        string project = Path.Combine(dir, "App.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Compile Remove="Excluded\**" />
                <None Remove="Excluded\**" />
              </ItemGroup>
            </Project>
            """);

        try
        {
            // Evaluated first, and the test is meaningless without it. The tree reads the item
            // list cached-only and never blocks on MSBuild — an unevaluated project falls back to
            // showing every file, which is the right answer for responsiveness and the wrong one
            // to assert exclusion against. A real editor reaches this state by having expanded
            // Dependencies, or simply by the project having been opened; a freshly written temp
            // project has nothing cached, so without this the assertion below is checking the
            // fallback rather than the exclusion.
            Assert.NotNull(await ProjectEvaluationService.EvaluateAsync(project, default));

            var nodes = await SolutionTreeHandler.ChildrenAsync(
                new SolutionTreeParams(NodeId: $"project:{project}"), default);
            Assert.DoesNotContain(nodes, n => n.Label == "Excluded");

            var shown = await SolutionTreeHandler.ChildrenAsync(
                new SolutionTreeParams(NodeId: $"project:{project}", ShowAllFiles: true), default);
            Assert.Contains(shown, n => n.Label == "Excluded");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ANewSolutionFolderAppearsUnderTheSolution()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"slnfolder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string solutionPath = Path.Combine(dir, "App.sln");
        File.WriteAllText(solutionPath, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Global
            EndGlobal
            """);

        try
        {
            var created = await SolutionTreeEditHandler.EditAsync(
                new SolutionTreeEditParams(
                    Action: "addSolutionFolder",
                    TargetUri: LspConverters.PathToUri(solutionPath),
                    Name: "Benchmark"),
                default);
            Assert.True(created.Ok, created.Message);

            var children = await SolutionTreeHandler.ChildrenAsync(
                new SolutionTreeParams(NodeId: $"solution:{solutionPath}"), default);

            Assert.Contains(children, n =>
                n.Kind == SolutionNodeKind.SolutionFolder && n.Label == "Benchmark");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
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
            // A folder is identified by its path in both formats, which is what makes the two
            // halves of this test identical apart from the file they run against.
            foreach (string solution in new[] { sln, slnx })
            {
                Assert.True(Add(solution, "Benchmark", parent: null).Ok);
                Assert.True(Add(solution, "Inner", parent: "/Services/").Ok);

                var nodes = SolutionFileService.Read(solution);
                var benchmark = nodes.Single(n => n.Name == "Benchmark");
                Assert.True(benchmark.IsFolder);
                Assert.Null(benchmark.ParentId);
                Assert.Equal("/Services/", nodes.Single(n => n.Name == "Inner").ParentId);
            }
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

    /// <summary>
    /// The two rows that are the project's own furniture come first, in that order. Left to the
    /// alphabet Properties sits between the source folders — LegacyProject has a Models folder,
    /// which sorts ahead of it — and launchSettings.json is then somewhere in the middle of the
    /// tree rather than where Visual Studio and Rider both pin it.
    /// </summary>
    [Fact]
    public async Task DependenciesAndPropertiesLeadTheProject()
    {
        var children = await SolutionTreeHandler.ChildrenAsync(
            new SolutionTreeParams($"project:{FixturePaths.LegacyProjectFile}"), default);

        Assert.Equal(SolutionNodeKind.Dependencies, children[0].Kind);
        Assert.Equal(SolutionNodeKind.Folder, children[1].Kind);
        Assert.Equal("Properties", children[1].Label);

        // And the rest keeps the order it had: pinning two rows must not shuffle the folders
        // behind them.
        var folders = children
            .Skip(2)
            .Where(n => n.Kind == SolutionNodeKind.Folder)
            .Select(n => n.Label)
            .ToList();
        Assert.Equal(folders.OrderBy(label => label, StringComparer.OrdinalIgnoreCase), folders);
    }

    /// <summary>
    /// A project reference points at the project it names instead of growing a second copy of it.
    /// </summary>
    /// <remarks>
    /// The id is the part that matters beyond the leaf-ness: it used to be exactly the id of the
    /// real project row, and the tree keys its items by id — so a referenced project that was also
    /// visible under the solution was one id claimed by two rows, which is a row that fails to
    /// render rather than one that merely looks odd.
    /// </remarks>
    [Fact]
    public async Task AProjectReferenceIsAPointerNotASubtree()
    {
        var references = await SolutionTreeHandler.ChildrenAsync(
            new SolutionTreeParams($"group:projects|{FixturePaths.MultiProjectBFile}"), default);

        var reference = Assert.Single(references);
        Assert.Equal(SolutionNodeKind.ProjectRef, reference.Kind);
        Assert.Equal("ProjectA", reference.Label);
        Assert.False(reference.HasChildren);
        Assert.NotEqual($"project:{FixturePaths.MultiProjectAFile}", reference.Id);

        // And it still says which project it points at, which is what going to it needs.
        Assert.Equal(
            LspConverters.PathToUri(FixturePaths.MultiProjectAFile), reference.ResourceUri);
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

    [Fact]
    public async Task FilteringFromTheRootFindsProjectsAnywhereInTheSolution()
    {
        // The root holds one node — the solution's own name — so narrowing it by label emptied
        // the tree for any term that was not the solution's name. A filter at the root has to
        // mean "what in this solution matches", not "does the solution's name match".
        string? previous = WorkspaceService.BoundSolutionPath;
        try
        {
            WorkspaceService.BindSolution(FixturePaths.MultiSolutionFile);

            var unfiltered = await SolutionTreeHandler.ChildrenAsync(
                new SolutionTreeParams(), default);
            var root = Assert.Single(unfiltered);
            Assert.Equal(SolutionNodeKind.Solution, root.Kind);
            Assert.Equal("MultiSolution", root.Label);

            // "ProjectA" is nowhere in the solution's own name, which is exactly the case that
            // used to return nothing.
            var matches = await SolutionTreeHandler.ChildrenAsync(
                new SolutionTreeParams(NodeId: null, Filter: "ProjectA"), default);

            Assert.NotEmpty(matches);
            Assert.Contains(matches, n => n.Label == "ProjectA");
        }
        finally
        {
            WorkspaceService.BindSolution(previous);
        }
    }

    /// <summary>
    /// Every id the reveal chain names has to be a row the tree actually lists, because the client
    /// walks the chain by listing each level and looking the next id up in it.
    /// </summary>
    /// <remarks>
    /// Including when the URI spells the path differently from the solution file, which on Windows
    /// is always: VS Code lower-cases the drive letter in every URI it sends, and the tree names
    /// its rows after the solution's own path. The two ids are the same path and different
    /// strings, so the walk missed at the first folder and "Focus Current File" reported that the
    /// file was not in the solution — for every file, in every project.
    /// </remarks>
    /// <remarks>
    /// And whichever way the client spelled the URI. <c>UriSpelling</c> covers the three that reach
    /// this server: the one <see cref="LspConverters.PathToUri"/> produces, the one the extension's
    /// <c>code2Protocol</c> converter produces, and VS Code's own default — which percent-encodes
    /// the drive-letter colon, the form that made every reveal come back empty.
    /// </remarks>
    [Theory]
    [InlineData(UriSpelling.Roundtrip)]
    [InlineData(UriSpelling.UpperDrivePlainColon)]
    [InlineData(UriSpelling.LowerDriveEncodedColon)]
    public async Task TheRevealChainNamesRowsTheTreeActuallyLists(UriSpelling spelling)
    {
        string? previous = WorkspaceService.BoundSolutionPath;
        try
        {
            WorkspaceService.BindSolution(FixturePaths.MultiSolutionFile);

            string file = Path.Combine(FixturePaths.MultiSolutionDir, "ProjectA", "Class1.cs");
            var chain = (await SolutionTreeSearchHandler.RevealAsync(
                new SolutionTreeRevealParams(Spell(file, spelling), FileNesting: true),
                default)).Path;

            Assert.NotEmpty(chain);

            string? parent = null;
            foreach (string id in chain)
            {
                var children = await SolutionTreeHandler.ChildrenAsync(
                    new SolutionTreeParams(parent), default);
                Assert.True(
                    children.Any(c => c.Id == id),
                    $"'{id}' is not among the children of '{parent ?? "<roots>"}': " +
                    string.Join(", ", children.Select(c => c.Id)));
                parent = id;
            }
        }
        finally
        {
            WorkspaceService.BindSolution(previous);
        }
    }

    /// <summary>How a client wrote a <c>file:</c> URI for a Windows path.</summary>
    public enum UriSpelling
    {
        /// <summary>What <see cref="LspConverters.PathToUri"/> produces.</summary>
        Roundtrip,

        /// <summary>What the extension's <c>code2Protocol</c> converter produces.</summary>
        UpperDrivePlainColon,

        /// <summary>What VS Code produces on its own — <c>file:///c%3A/…</c>.</summary>
        LowerDriveEncodedColon,
    }

    private static string Spell(string path, UriSpelling spelling)
    {
        if (spelling == UriSpelling.Roundtrip || !Path.IsPathRooted(path) || path.Length < 2
            || path[1] != ':')
        {
            return LspConverters.PathToUri(path);
        }

        string rest = path[2..].Replace('\\', '/');
        return spelling == UriSpelling.UpperDrivePlainColon
            ? $"file:///{char.ToUpperInvariant(path[0])}:{rest}"
            : $"file:///{char.ToLowerInvariant(path[0])}%3A{rest}";
    }

    /// <summary>
    /// A file can be both a solution item and a file of the project that compiles it. The client
    /// keys its tree items by id, and two nodes sharing one id makes the second branch fail to
    /// render — so a solution item is not called what the project's own file is called.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASolutionItemIsNotGivenTheSameIdAsAProjectFile(bool slnx)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"roslyn-sense-slnitem-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        string? previous = WorkspaceService.BoundSolutionPath;
        try
        {
            string solution = Path.Combine(directory, slnx ? "Test.slnx" : "Test.sln");
            File.WriteAllText(solution, slnx
                ? "<Solution />"
                : """
                    Microsoft Visual Studio Solution File, Format Version 12.00
                    Global
                    EndGlobal

                    """);

            string file = Path.Combine(directory, "README.md");
            File.WriteAllText(file, "# Test");

            SolutionFileWriter.AddFolder(solution, "Docs", null);
            string folderId = SolutionFileService.Read(solution).Single(n => n.IsFolder).Id;
            SolutionFileWriter.AddSolutionItem(solution, folderId, file);

            WorkspaceService.BindSolution(solution);
            var children = await SolutionTreeHandler.ChildrenAsync(
                new SolutionTreeParams(NodeId: $"slnfolder:{folderId}"), default);

            var item = Assert.Single(children);
            Assert.Equal(SolutionNodeKind.SolutionItem, item.Kind);
            Assert.NotEqual($"file:{file}", item.Id);

            // The folder that listed it has to be recoverable from the id: detaching the item is
            // the one operation that cannot work out for itself which folder it came from.
            Assert.Contains(folderId, item.Id);
        }
        finally
        {
            WorkspaceService.BindSolution(previous);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
