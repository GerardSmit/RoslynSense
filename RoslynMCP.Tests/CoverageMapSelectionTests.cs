using RoslynMCP.Services;
using RoslynMCP.Services.Testing;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public class CoverageMapSelectionTests
{
    [Fact]
    public async Task FilteredRebuildRetainsUnselectedClassesAndKeepsSameNamedProjectsSeparate()
    {
        string project = FixturePaths.DebugTestProjectFile;
        string solution = PathHelper.FindNearestSolution(project)!;
        var previous = TestCoverageMapStore.Load(solution);
        var tests = await TestDiscoveryService.DiscoverAsync(project);
        var calculator = tests.Where(test => test.ClassName == "CalculatorTests").ToList();
        Assert.NotEmpty(calculator);
        string source = calculator[0].FilePath!;
        const string className = "DebugTestProject.CalculatorTests";
        var own = new CoverageMapEntry(className, project, calculator.Select(test => test.FullyQualifiedName).ToList(),
            [CoveredFile.FromLines(Path.Combine(FixturePaths.DebugTestProjectDir, "Calculator.cs"), null, [7])],
            source, CoverageMapHash.OfFile(source));
        var otherProject = own with
        {
            ProjectPath = Path.Combine(Path.GetDirectoryName(project)!, "Another.Tests.csproj"),
            Files = [CoveredFile.FromLines(Path.Combine(Path.GetTempPath(), "Other.cs"), null, [99])],
        };
        var unselected = own with { ClassFullName = "DebugTestProject.FilterSelectionTests", Tests = ["Unselected.Test"] };

        try
        {
            TestCoverageMapStore.Save(solution, new TestCoverageMap(solution, DateTime.UtcNow, [otherProject, own, unselected]));

            // All selected source is unchanged, so this exercises the actual rebuild/save path
            // without compiling a fixture or starting a coverage collector.
            var result = await TestCoverageMapBuilder.BuildAsync(project, classFilter: "CalculatorTests");

            Assert.Null(result.Error);
            Assert.Empty(result.Failures);
            Assert.Equal(0, result.ClassesRun);
            Assert.Equal(1, result.ClassesReused);
            Assert.Equal(3, result.Map.Entries.Count);
            var retained = Assert.Single(result.Map.Entries,
                entry => entry.ProjectPath == project && entry.ClassFullName == className);
            Assert.Equal(own.Files, retained.Files);
            Assert.Contains(otherProject, result.Map.Entries);
            Assert.Contains(unselected, result.Map.Entries);
            Assert.Equal(3, TestCoverageMapStore.Load(solution).Entries.Count);
        }
        finally
        {
            if (previous.IsEmpty)
                TestCoverageMapStore.Clear(solution);
            else
                TestCoverageMapStore.Save(solution, previous);
        }
    }
}
