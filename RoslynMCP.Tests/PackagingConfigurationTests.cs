using Microsoft.Language.Xml;
using Xunit;

namespace RoslynMCP.Tests;

public class PackagingConfigurationTests
{
    [Fact]
    public void RoslynMcpProjectIsConfiguredAsDotnetTool()
    {
        var project = Parser.ParseText(
            File.ReadAllText(GetRepoPath("RoslynMCP", "RoslynMCP.csproj")));

        Assert.Equal("net10.0", GetPropertyValue(project, "TargetFramework"));
        Assert.Equal("true", GetPropertyValue(project, "PackAsTool"));
        Assert.Equal("roslyn-sense", GetPropertyValue(project, "ToolCommandName"));
        Assert.Equal("RoslynSense", GetPropertyValue(project, "PackageId"));
        Assert.Equal("0.1.0", GetPropertyValue(project, "VersionPrefix"));
        Assert.Equal("README.md", GetPropertyValue(project, "PackageReadmeFile"));
    }

    private static string GetRepoPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string solutionPath = Path.Combine(directory.FullName, "RoslynMCP.sln");
            if (File.Exists(solutionPath))
                return Path.Combine([directory.FullName, .. parts]);

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static string? GetPropertyValue(XmlDocumentSyntax project, string propertyName) =>
        project.RootSyntax?
            .GetElementsByLocalName("PropertyGroup")
            .SelectMany(group => group.Elements)
            .FirstOrDefault(element => element.NameNode?.LocalName == propertyName)
            ?.Value;
}
