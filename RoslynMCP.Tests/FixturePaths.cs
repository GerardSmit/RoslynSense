namespace RoslynMCP.Tests;

/// <summary>
/// Resolves paths to the fixture project files shipped alongside the test assembly.
/// </summary>
internal static class FixturePaths
{
    private static readonly string s_fixturesRoot = FindFixturesRoot();

    public static string SampleProjectDir => Path.Combine(s_fixturesRoot, "SampleProject");
    public static string AlternateProjectFile => Path.Combine(SampleProjectDir, "Aardvark.Empty.csproj");
    public static string SampleProjectFile => Path.Combine(SampleProjectDir, "SampleProject.csproj");
    public static string CalculatorFile => Path.Combine(SampleProjectDir, "Calculator.cs");
    public static string ExternalReferencesFile => Path.Combine(SampleProjectDir, "ExternalReferences.cs");
    public static string FrameworkReferencesFile => Path.Combine(SampleProjectDir, "FrameworkReferences.cs");
    public static string ManyUsagesFile => Path.Combine(SampleProjectDir, "ManyUsages.cs");
    public static string ResultFile => Path.Combine(SampleProjectDir, "Models", "Result.cs");
    public static string ServicesFile => Path.Combine(SampleProjectDir, "Services.cs");
    public static string OutlineShowcaseFile => Path.Combine(SampleProjectDir, "OutlineShowcase.cs");
    public static string TextUtilitiesFile => Path.Combine(SampleProjectDir, "TextUtilities.cs");
    public static string WarningsFile => Path.Combine(SampleProjectDir, "Warnings.cs");
    public static string BrokenProjectDir => Path.Combine(s_fixturesRoot, "BrokenProject");
    public static string BrokenProjectFile => Path.Combine(BrokenProjectDir, "BrokenProject.csproj");
    public static string BrokenSyntaxFile => Path.Combine(BrokenProjectDir, "BrokenSyntax.cs");
    public static string BrokenSemanticFile => Path.Combine(BrokenProjectDir, "BrokenSemantic.cs");

    public static string LegacyProjectDir => Path.Combine(s_fixturesRoot, "LegacyProject");
    public static string LegacyProjectFile => Path.Combine(LegacyProjectDir, "LegacyProject.csproj");
    public static string LegacyCalculatorFile => Path.Combine(LegacyProjectDir, "Calculator.cs");
    public static string LegacyCustomerFile => Path.Combine(LegacyProjectDir, "Models", "Customer.cs");

    public static string AspxProjectDir => Path.Combine(s_fixturesRoot, "AspxProject");
    public static string AspxProjectFile => Path.Combine(AspxProjectDir, "AspxProject.csproj");
    public static string DefaultAspxFile => Path.Combine(AspxProjectDir, "Default.aspx");
    public static string HeaderControlFile => Path.Combine(AspxProjectDir, "Controls", "HeaderControl.ascx");
    public static string SiteMasterFile => Path.Combine(AspxProjectDir, "Site.master");
    public static string DataServiceFile => Path.Combine(AspxProjectDir, "DataService.asmx");
    public static string ImageHandlerFile => Path.Combine(AspxProjectDir, "ImageHandler.ashx");
    public static string AspxPageHelperFile => Path.Combine(AspxProjectDir, "PageHelper.cs");
    public static string AspxWebConfigFile => Path.Combine(AspxProjectDir, "web.config");

    public static string BlazorProjectDir => Path.Combine(s_fixturesRoot, "BlazorProject");
    public static string BlazorProjectFile => Path.Combine(BlazorProjectDir, "BlazorProject.csproj");
    public static string BlazorAppHelperFile => Path.Combine(BlazorProjectDir, "AppHelper.cs");
    public static string CounterRazorFile => Path.Combine(BlazorProjectDir, "Counter.razor");
    public static string WeatherRazorFile => Path.Combine(BlazorProjectDir, "Weather.razor");

    public static string DebugTestProjectDir => Path.Combine(s_fixturesRoot, "DebugTestProject");
    public static string DebugTestProjectFile => Path.Combine(DebugTestProjectDir, "DebugTestProject.csproj");
    public static string DebugCalculatorFile => Path.Combine(DebugTestProjectDir, "Calculator.cs");
    public static string DebugCalculatorTestsFile => Path.Combine(DebugTestProjectDir, "CalculatorTests.cs");

    /// <summary>
    /// Walks up from the test assembly location to find the Fixtures directory.
    /// Prefer the source-tree fixtures so Roslyn can open the nested sample project
    /// with its real restore/build artifacts; fall back to copied output fixtures.
    /// </summary>
    private static string FindFixturesRoot()
    {
        string? copiedFixturesRoot = null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var solutionCandidate = Path.Combine(dir.FullName, "RoslynMCP.sln");
            var sourceCandidate = Path.Combine(dir.FullName, "RoslynMCP.Tests", "Fixtures");
            if (File.Exists(solutionCandidate) && Directory.Exists(sourceCandidate))
                return sourceCandidate;

            var copiedCandidate = Path.Combine(dir.FullName, "Fixtures");
            if (copiedFixturesRoot is null && Directory.Exists(copiedCandidate))
                copiedFixturesRoot = copiedCandidate;

            dir = dir.Parent;
        }

        if (copiedFixturesRoot is not null)
            return copiedFixturesRoot;

        throw new InvalidOperationException(
            "Could not locate the Fixtures directory. Ensure the test project copies fixture files to the output directory.");
    }
}
