using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public class PathHelperTests
{
    [Fact]
    public void WhenTheFileIsGoneFromDiskThenTheNearestSolutionIsStillFound()
    {
        // A loaded project can list a file whose folder was deleted on disk; every search-path
        // caller asks about such paths, and the walk used to enumerate the missing file path as
        // a directory and throw — killing the whole search.
        var root = Directory.CreateTempSubdirectory("roslynsense-pathhelper-");
        try
        {
            string solution = Path.Combine(root.FullName, "Sample.sln");
            File.WriteAllText(solution, "");

            string missing = Path.Combine(root.FullName, "Deleted", "Properties", "AssemblyInfo.cs");

            Assert.Equal(solution, PathHelper.FindNearestSolution(missing));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The spelling handed to a build. The editor hands the extension a lower-case drive letter,
    /// and a deterministic compiler bakes the path into its output — so a build from here and a
    /// build from a terminal produced two different images of the same sources.
    /// </summary>
    [Fact]
    public void OnDiskCasingRestoresEverySegmentThatExists()
    {
        var root = Directory.CreateTempSubdirectory("roslynsense-casing-");
        try
        {
            string project = Path.Combine(root.FullName, "Mixed.Case", "Sub Dir", "Proj.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, "");

            string spelled = PathHelper.WithOnDiskCasing(project.ToLowerInvariant());

            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(project, spelled, ignoreCase: true);
                Assert.EndsWith(Path.Combine("Mixed.Case", "Sub Dir", "Proj.csproj"), spelled, StringComparison.Ordinal);
                Assert.True(char.IsUpper(spelled[0]), spelled);
            }
            else
            {
                // Elsewhere the spelling is the identity, and nothing is looked up.
                Assert.Equal(project.ToLowerInvariant(), spelled);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void OnDiskCasingLeavesAMissingTailAsGiven()
    {
        var root = Directory.CreateTempSubdirectory("roslynsense-casing-");
        try
        {
            string missing = Path.Combine(root.FullName, "NotYet", "Build.csproj");

            string spelled = PathHelper.WithOnDiskCasing(missing);

            Assert.EndsWith(Path.Combine("NotYet", "Build.csproj"), spelled, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
