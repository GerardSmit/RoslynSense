using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class CodeScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codescope-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteProject(string relativePath, string? assemblyName = null, string? rootNamespace = null)
    {
        var path = Path.Combine(_root, relativePath.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var properties = "";
        if (assemblyName is not null)
            properties += $"<AssemblyName>{assemblyName}</AssemblyName>";
        if (rootNamespace is not null)
            properties += $"<RootNamespace>{rootNamespace}</RootNamespace>";

        File.WriteAllText(path,
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{properties}</PropertyGroup></Project>");
        return path;
    }

    private void WriteSolution(string name, params string[] projectPaths)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var project in projectPaths)
        {
            var relative = Path.GetRelativePath(_root, project);
            sb.AppendLine(
                $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = " +
                $"\"{Path.GetFileNameWithoutExtension(project)}\", \"{relative}\", \"{{{Guid.NewGuid()}}}\"");
            sb.AppendLine("EndProject");
        }
        File.WriteAllText(Path.Combine(_root, name), sb.ToString());
    }

    [Fact]
    public void WhenSolutionExistsThenPrefixesCoverAllProjects()
    {
        var web = WriteProject(@"src\Company.Product.Web\Company.Product.Web.csproj");
        var core = WriteProject(@"src\Company.Product.Core\Company.Product.Core.csproj");
        WriteSolution("Product.sln", web, core);

        var prefixes = CodeScope.OwnPrefixesForProject(web);

        Assert.Contains("Company.Product.Web", prefixes);
        Assert.Contains("Company.Product.Core", prefixes);
        Assert.Contains("Company", prefixes);
    }

    [Fact]
    public void WhenAssemblyNameDiffersFromFileNameThenBothNamespacesMatch()
    {
        // A real shape this has to handle: project file Storefront.Website, assembly
        // Legacy.Modules, code namespaces Legacy.*.
        var project = WriteProject(
            @"src\Storefront.Website\Storefront.Website.csproj",
            assemblyName: "Legacy.Modules");
        WriteSolution("Legacy.sln", project);

        var prefixes = CodeScope.OwnPrefixesForProject(project);

        Assert.True(CodeScope.IsOwn("Legacy.CustomerData.ExcludePage", prefixes));
        Assert.True(CodeScope.IsOwn("Storefront.Website.Handlers.Sitemap", prefixes));
        Assert.False(CodeScope.IsOwn("System.String.Concat", prefixes));
        Assert.False(CodeScope.IsOwn("DotNetNuke.Common.Globals.GetPortalSettings", prefixes));
    }

    [Fact]
    public void WhenNoSolutionExistsThenTheProjectItselfStillYieldsPrefixes()
    {
        var project = WriteProject(@"standalone\App.Tool\App.Tool.csproj");

        var prefixes = CodeScope.OwnPrefixesForProject(project);

        Assert.Contains("App.Tool", prefixes);
        Assert.Contains("App", prefixes);
    }

    [Fact]
    public void WhenPrefixMatchesMidIdentifierThenItIsNotOwn()
    {
        var project = WriteProject(@"src\App\App.csproj");
        WriteSolution("App.sln", project);

        var prefixes = CodeScope.OwnPrefixesForProject(project);

        Assert.True(CodeScope.IsOwn("App.Program.Main()", prefixes));
        Assert.True(CodeScope.IsOwn("App+Nested.Run", prefixes));
        Assert.False(CodeScope.IsOwn("Apple.Pie.Bake", prefixes));
        Assert.False(CodeScope.IsOwn("SNINativeMethodWrapper.SNIReadSyncOverAsync", prefixes));
    }

    [Fact]
    public void WhenFrameCarriesModulePrefixThenModuleIsAlsoChecked()
    {
        var project = WriteProject(@"src\App\App.csproj");
        WriteSolution("App.sln", project);

        var prefixes = CodeScope.OwnPrefixesForProject(project);

        Assert.True(CodeScope.IsOwn("App!Program.Main()", prefixes));
        Assert.False(CodeScope.IsOwn("System.Private.CoreLib!System.String.Concat", prefixes));
    }

    [Fact]
    public void WhenFilteringThenHiddenCountsFrameworkMethods()
    {
        var project = WriteProject(@"src\App\App.csproj");
        WriteSolution("App.sln", project);
        var prefixes = CodeScope.OwnPrefixesForProject(project);

        List<SpeedscopeParser.MethodProfile> methods =
        [
            new("Main", "App", "App.Program.Main", 10, 20, 50, 100, 1),
            new("Concat", "System", "System.String.Concat", 5, 5, 25, 25, 1),
            new("Query", "App.Data", "App.Data.Repository.Query", 5, 10, 25, 50, 1),
        ];

        var (own, hidden) = CodeScope.FilterOwn(methods, prefixes);

        Assert.Equal(2, own.Count);
        Assert.Equal(1, hidden);
        Assert.DoesNotContain(own, m => m.FullName.StartsWith("System.", StringComparison.Ordinal));
    }
}
