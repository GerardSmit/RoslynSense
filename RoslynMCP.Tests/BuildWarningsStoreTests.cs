using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public sealed class BuildWarningsStoreTests
{
    private const string FakeProject = @"C:\Projects\Example.csproj";

    private static readonly string[] SampleWarnings =
    [
        @"C:\Projects\Foo.cs(10,5): warning CS0219: The variable 'x' is assigned but its value is never used [C:\Projects\Example.csproj]",
        @"C:\Projects\Foo.cs(20,5): warning CS0219: The variable 'y' is assigned but its value is never used [C:\Projects\Example.csproj]",
        @"C:\Projects\Bar.cs(7,1): warning CS0414: The field 'Bar.value' is assigned but its value is never used [C:\Projects\Example.csproj]",
    ];

    [Fact]
    public void Store_GroupsByWarningCode()
    {
        var store = new BuildWarningsStore();
        store.Store(FakeProject, SampleWarnings);

        var cs0219 = store.GetWarnings(FakeProject, "CS0219");
        var cs0414 = store.GetWarnings(FakeProject, "CS0414");

        Assert.NotNull(cs0219);
        Assert.Equal(2, cs0219.Count);
        Assert.NotNull(cs0414);
        Assert.Single(cs0414);
    }

    [Fact]
    public void GetWarnings_IsCaseInsensitive()
    {
        var store = new BuildWarningsStore();
        store.Store(FakeProject, SampleWarnings);

        Assert.NotNull(store.GetWarnings(FakeProject, "cs0219"));
        Assert.NotNull(store.GetWarnings(FakeProject, "Cs0219"));
        Assert.NotNull(store.GetWarnings(FakeProject, "CS0219"));
    }

    [Fact]
    public void GetWarnings_UnknownProject_ReturnsNull()
    {
        var store = new BuildWarningsStore();
        Assert.Null(store.GetWarnings(@"C:\Missing\Project.csproj", "CS0219"));
    }

    [Fact]
    public void GetWarnings_UnknownCode_ReturnsEmptyList()
    {
        var store = new BuildWarningsStore();
        store.Store(FakeProject, SampleWarnings);
        var result = store.GetWarnings(FakeProject, "CS9999");
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void Store_ReplacesDataOnSecondCall()
    {
        var store = new BuildWarningsStore();
        store.Store(FakeProject, SampleWarnings);
        store.Store(FakeProject, [SampleWarnings[0]]); // only one warning

        var cs0219 = store.GetWarnings(FakeProject, "CS0219");
        Assert.NotNull(cs0219);
        Assert.Single(cs0219);
    }

    [Fact]
    public void ExtractMessage_StripsBothPaths()
    {
        var raw = @"C:\Projects\Foo.cs(10,5): warning CS0219: The variable 'x' is assigned but its value is never used [C:\Projects\Example.csproj]";
        var msg = BuildWarningsStore.ExtractMessage(raw);
        Assert.Equal("The variable 'x' is assigned but its value is never used", msg);
    }

    [Fact]
    public void ExtractMessage_WorksWithoutProjectBracket()
    {
        var raw = @"C:\Projects\Foo.cs(10,5): warning CS0219: The variable 'x' is never used";
        var msg = BuildWarningsStore.ExtractMessage(raw);
        Assert.Equal("The variable 'x' is never used", msg);
    }

    [Fact]
    public void GetAll_ReturnsAllGroupedWarnings()
    {
        var store = new BuildWarningsStore();
        store.Store(FakeProject, SampleWarnings);

        var all = store.GetAll(FakeProject);
        Assert.NotNull(all);
        Assert.Equal(2, all.Count); // CS0219 and CS0414
    }
}

public sealed class GetBuildWarningsToolTests
{
    [Fact]
    public void WhenNoCachedData_ReturnsHelpMessage()
    {
        var store = new BuildWarningsStore();
        var result = GetBuildWarningsTool.GetBuildWarnings(
            FixturePaths.SampleProjectFile, "CS0219", store);

        Assert.Contains("Run BuildProject first", result);
    }

    [Fact]
    public async Task WhenBuildRan_WarningsAreRetrievable()
    {
        var warningsStore = new BuildWarningsStore();
        await BuildProjectTool.BuildProject(
            FixturePaths.SampleProjectFile,
            new BackgroundTaskStore(),
            warningsStore);

        // CS0219: unused variable warning from Warnings.cs
        var result = GetBuildWarningsTool.GetBuildWarnings(
            FixturePaths.SampleProjectFile, "CS0219", warningsStore);

        // Either found warnings or "no warnings of that code" — either way, not the "run build first" message
        Assert.DoesNotContain("Run BuildProject first", result);
    }

    [Fact]
    public void WhenCodeNotFound_ReturnsNoWarningsMessage()
    {
        var store = new BuildWarningsStore();
        // Manually seed the store with a different code
        store.Store(
            FixturePaths.SampleProjectFile,
            [@"C:\Foo.cs(1,1): warning CS0414: msg [C:\Foo.csproj]"]);

        var result = GetBuildWarningsTool.GetBuildWarnings(
            FixturePaths.SampleProjectFile, "CS9999", store);

        Assert.Contains("No warnings with code CS9999", result);
    }
}
