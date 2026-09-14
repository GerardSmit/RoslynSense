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
        // The release workflow's sync-versions step rewrites this together with the plugin
        // manifest and the extension's package.json, in a commit that skips CI. Pinning a literal
        // here failed the first run after every release; the invariant is that they agree.
        Assert.Equal(ManifestVersion(".claude-plugin", "plugin.json"), GetPropertyValue(project, "VersionPrefix"));
        Assert.Equal(ManifestVersion("vscode-extension", "package.json"), GetPropertyValue(project, "VersionPrefix"));
        Assert.Equal("README.md", GetPropertyValue(project, "PackageReadmeFile"));
    }

    private static string ManifestVersion(params string[] parts)
    {
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(GetRepoPath(parts)));
        return manifest.RootElement.GetProperty("version").GetString()!;
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
