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
}
