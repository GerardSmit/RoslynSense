using RoslynMCP.Services.ProjectModel;
using RoslynMCP.Services.Testing;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Structural edits and the test-run history — the operations the AI otherwise performs by
/// shelling out, behind the daemon's back.
/// </summary>
[Collection(SharedState.Name)]
public class ProjectMutationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mutation-{Guid.NewGuid():N}");

    public ProjectMutationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string WriteProject(string name, string contents = SdkProject)
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{name}.csproj");
        File.WriteAllText(path, contents);
        return path;
    }

    private const string SdkProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>A pre-SDK project: sources are listed one by one, so an added file that is not
    /// listed is not compiled.</summary>
    private const string LegacyProject = """
        <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <ItemGroup>
            <Compile Include="Existing.cs" />
          </ItemGroup>
        </Project>
        """;

    // === Adding files ===

    [Fact]
    public async Task AddedFileGetsTheNamespaceItsFolderImplies()
    {
        string project = WriteProject("Orders");

        var result = await ProjectMutationService.AddFileAsync(
            project, Path.Combine("Billing", "Invoice.cs"));

        Assert.True(result.Ok, result.Message);
        string written = await File.ReadAllTextAsync(
            Path.Combine(Path.GetDirectoryName(project)!, "Billing", "Invoice.cs"));

        Assert.Contains("namespace Orders.Billing;", written);
        Assert.Contains("public class Invoice", written);
    }

    [Fact]
    public async Task RootNamespaceWinsOverTheProjectFileName()
    {
        string project = WriteProject("Orders", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <RootNamespace>Contoso.Orders</RootNamespace>
              </PropertyGroup>
            </Project>
            """);

        await ProjectMutationService.AddFileAsync(project, "Invoice.cs");

        Assert.Contains("namespace Contoso.Orders;", await File.ReadAllTextAsync(
            Path.Combine(Path.GetDirectoryName(project)!, "Invoice.cs")));
    }

    [Theory]
    [InlineData("interface", "public interface IThing")]
    [InlineData("record", "public record Thing")]
    [InlineData("enum", "public enum Thing")]
    public async Task ScaffoldFollowsTheRequestedKind(string kind, string expected)
    {
        string project = WriteProject("Lib");
        var fileKind = Enum.Parse<ProjectMutationService.FileKind>(kind, ignoreCase: true);

        await ProjectMutationService.AddFileAsync(project, "Thing.cs", fileKind);

        Assert.Contains(expected, await File.ReadAllTextAsync(
            Path.Combine(Path.GetDirectoryName(project)!, "Thing.cs")));
    }

    [Fact]
    public async Task AnEmptyFileIsLeftEmpty()
    {
        string project = WriteProject("Lib");

        await ProjectMutationService.AddFileAsync(
            project, "data.json", ProjectMutationService.FileKind.Empty);

        Assert.Equal("", await File.ReadAllTextAsync(
            Path.Combine(Path.GetDirectoryName(project)!, "data.json")));
    }

    [Fact]
    public async Task ANonGlobbingProjectGetsACompileItem()
    {
        // Without the item the file exists on disk and is invisible to the compiler, which is a
        // uniquely confusing failure.
        string project = WriteProject("Legacy", LegacyProject);

        var result = await ProjectMutationService.AddFileAsync(project, "Added.cs");

        Assert.True(result.Ok, result.Message);
        Assert.Contains("Added.cs", await File.ReadAllTextAsync(project));
    }

    [Fact]
    public async Task AGlobbingProjectIsLeftAlone()
    {
        string project = WriteProject("Sdk");
        string before = await File.ReadAllTextAsync(project);

        await ProjectMutationService.AddFileAsync(project, "Added.cs");

        Assert.Equal(before, await File.ReadAllTextAsync(project));
    }

    [Fact]
    public async Task AFileOutsideTheProjectIsRefused()
    {
        string project = WriteProject("Lib");

        var result = await ProjectMutationService.AddFileAsync(
            project, Path.Combine("..", "..", "Escaped.cs"));

        Assert.False(result.Ok);
        Assert.Contains("inside the project", result.Message);
    }

    [Fact]
    public async Task AnExistingFileIsNotOverwritten()
    {
        string project = WriteProject("Lib");
        string path = Path.Combine(Path.GetDirectoryName(project)!, "Keep.cs");
        await File.WriteAllTextAsync(path, "// precious");

        var result = await ProjectMutationService.AddFileAsync(project, "Keep.cs");

        Assert.False(result.Ok);
        Assert.Equal("// precious", await File.ReadAllTextAsync(path));
    }

    // === Deleting files ===

    [Fact]
    public async Task DeletingAFileAlsoRemovesItsCompileItem()
    {
        string project = WriteProject("Legacy", LegacyProject);
        string path = Path.Combine(Path.GetDirectoryName(project)!, "Existing.cs");
        await File.WriteAllTextAsync(path, "class Existing {}");

        var result = await ProjectMutationService.DeleteFileAsync(path);

        Assert.True(result.Ok, result.Message);
        Assert.False(File.Exists(path));
        Assert.DoesNotContain("Existing.cs", await File.ReadAllTextAsync(project));
    }

    [Fact]
    public async Task DeletingAMissingFileSaysSo()
    {
        var result = await ProjectMutationService.DeleteFileAsync(
            Path.Combine(_root, "nope.cs"));

        Assert.False(result.Ok);
        Assert.Contains("not found", result.Message);
    }

    // === References ===

    [Fact]
    public async Task AProjectCannotReferenceItself()
    {
        string project = WriteProject("Lib");

        var result = await ProjectMutationService.AddProjectReferenceAsync(project, project);

        Assert.False(result.Ok);
        Assert.Contains("itself", result.Message);
    }

    [Fact]
    public async Task AMissingReferenceTargetIsReportedBeforeAnythingRuns()
    {
        string project = WriteProject("Lib");

        var result = await ProjectMutationService.AddProjectReferenceAsync(
            project, Path.Combine(_root, "Ghost", "Ghost.csproj"));

        Assert.False(result.Ok);
        Assert.Contains("not found", result.Message);
    }

    // === Test run history ===

    [Fact]
    public void FailuresAreRecoverableAfterTheRunThatProducedThem()
    {
        string solution = Path.Combine(_root, "App.sln");
        File.WriteAllText(solution, "");

        var results = new List<TestResult>
        {
            new("Tests.Passes", "Passed", 3, null, null, null),
            new("Tests.Fails", "Failed", 5, "Assert.Equal() Failure", "   at Tests.Fails() in C:\\src\\Tests.cs:line 42", null),
        };

        string runId = TestRunStore.Record(solution, "Tests.csproj", results);
        try
        {
            var run = TestRunStore.Find(solution);

            Assert.NotNull(run);
            Assert.Equal(runId, run!.RunId);
            Assert.Single(run.Results.Where(r => r.Failed));
            Assert.Equal(run.RunId, TestRunStore.Find(solution, runId)!.RunId);
        }
        finally
        {
            TestRunStore.Clear(solution);
        }
    }

    [Fact]
    public void TheFailureLocationIsTheDeepestFrameInYourOwnCode()
    {
        // The top frame belongs to the assertion library, whose sources are not on this machine;
        // pointing there would send the reader nowhere useful.
        string ownFile = Path.Combine(_root, "OrderTests.cs");
        File.WriteAllText(ownFile, "// test");

        var result = new TestResult(
            "Tests.Total", "Failed", 1, "boom",
            $"""
               at Xunit.Assert.Equal() in D:\build\xunit\Assert.cs:line 11
               at Tests.Total() in {ownFile}:line 27
             """,
            null);

        var location = TestRunStore.LocateFailure(result);

        Assert.NotNull(location);
        Assert.Equal(ownFile, location!.FilePath);
        Assert.Equal(27, location.Line);
    }

    [Fact]
    public void AStackTraceWithNoSourceInformationYieldsNoLocation()
    {
        var result = new TestResult(
            "Tests.Total", "Failed", 1, "boom", "   at Tests.Total()", null);

        Assert.Null(TestRunStore.LocateFailure(result));
    }

    [Fact]
    public void OnlyTheMostRecentRunsAreKept()
    {
        string solution = Path.Combine(_root, "History.sln");
        File.WriteAllText(solution, "");

        try
        {
            for (int i = 0; i < 12; i++)
            {
                TestRunStore.Record(solution, "Tests.csproj",
                    [new TestResult($"Tests.Case{i}", "Passed", 1, null, null, null)]);
            }

            var runs = TestRunStore.Read(solution);

            Assert.Equal(10, runs.Count);
            // Newest first, so "the last run" is the head rather than a search.
            Assert.Equal("Tests.Case11", runs[0].Results[0].FullyQualifiedName);
        }
        finally
        {
            TestRunStore.Clear(solution);
        }
    }
}
