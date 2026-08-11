using RoslynMCP.Services;
using RoslynMCP.Services.Testing;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The mechanism the coverage map is built on: narrowing a coverage run to one test class, so
/// that what the report shows as covered is what that class alone executed.
/// </summary>
/// <remarks>
/// <para>
/// This is the property the whole map rests on, and it is worth a real run to assert rather than
/// assume. If a filtered run ever started reporting lines some other class executed, every impact
/// selection built on the map would quietly widen, and nothing else in the suite would notice.
/// </para>
/// <para>
/// It also documents why the map costs one run per class. Attributing coverage from *inside* a
/// single run — an in-process data collector cutting a <c>dotnet-coverage</c> session at test
/// boundaries with <c>snapshot --reset</c> — was built and measured, and it does not work: a
/// snapshot requested from a live test host does not capture the instant it is asked for. All the
/// coverage of the run landed in the first snapshot and the rest came back empty, which as an
/// attribution is not merely imprecise but wrong. Between-process resets do work, and that is
/// exactly what one filtered run per class is.
/// </para>
/// </remarks>
[Collection(SharedState.Name)]
public class CoverageAttributionTests
{
    [Fact]
    public async Task AFilteredRunReportsOnlyWhatThatClassExecuted()
    {
        string csproj = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "DebugTestProject", "DebugTestProject.csproj"));
        Assert.True(File.Exists(csproj), $"fixture not found: {csproj}");

        // Both tests live in CalculatorTests: one calls Calculator.Add, the other
        // Calculator.Multiply. Narrowed to a single test, only that method should come back hit.
        var addOnly = await CoverageService.CollectAsync(
            csproj, "FullyQualifiedName~CalculatorTests.Add_ReturnsSum", timeoutSeconds: 600);

        Assert.True(addOnly.Success, $"coverage run failed: {addOnly.Message}");
        Assert.NotNull(addOnly.Data);

        Assert.True(CoversMethod(addOnly.Data!, "Add"), "the Add run does not cover Calculator.Add");
        Assert.False(CoversMethod(addOnly.Data!, "Multiply"),
            "the Add run covers Calculator.Multiply — a filtered run is not isolating the class");

        var multiplyOnly = await CoverageService.CollectAsync(
            csproj, "FullyQualifiedName~CalculatorTests.Multiply_ReturnsProduct", timeoutSeconds: 600);

        Assert.True(multiplyOnly.Success, $"coverage run failed: {multiplyOnly.Message}");
        Assert.True(CoversMethod(multiplyOnly.Data!, "Multiply"),
            "the Multiply run does not cover Calculator.Multiply");
        Assert.False(CoversMethod(multiplyOnly.Data!, "Add"),
            "the Multiply run covers Calculator.Add — a filtered run is not isolating the class");
    }

    [Theory]
    [InlineData("Namespace.Class.Method", "Namespace.Class")]
    [InlineData("Namespace.Class.Method(System.String)", "Namespace.Class")]
    [InlineData("Class.Method", "Class")]
    [InlineData("Bare", "Bare")]
    public void ClassNameOf_TakesEverythingBeforeTheLastDotOutsideTheArguments(
        string fullyQualifiedName, string expected) =>
        Assert.Equal(expected, TestCoverageMapBuilder.ClassNameOf(fullyQualifiedName));

    /// <summary>
    /// Whether the report shows any line of <c>Calculator.&lt;methodName&gt;</c> having run.
    /// </summary>
    /// <remarks>
    /// Matched exactly on both class and method. The fixture's test class is
    /// <c>CalculatorTests</c> and its tests are named after the methods they exercise, so a
    /// substring match would find the test method instead of the code under test and the
    /// assertion would pass or fail for the wrong reason.
    /// </remarks>
    private static bool CoversMethod(CoverageData data, string methodName) =>
        data.Files.Values
            .SelectMany(file => file.Classes)
            .Where(cls => cls.FullName is "DebugTestProject.Calculator")
            .SelectMany(cls => cls.Methods)
            .Where(method => method.Name == methodName
                || method.Name.StartsWith(methodName + "(", StringComparison.Ordinal))
            .Any(method => method.Lines.Any(line => line.Hits > 0));
}
