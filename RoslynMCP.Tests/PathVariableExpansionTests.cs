using RoslynMCP.Services.Database;
using Xunit;

namespace RoslynMCP.Tests;

public sealed class PathVariableExpansionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnvironmentPathsResolveAgainstTheSuppliedBaseDirectory(bool absolute)
    {
        string root = Path.Combine(Path.GetTempPath(), "path-expansion-" + Guid.NewGuid().ToString("N"));
        string variable = "ROSLYNSENSE_TEST_PATH_" + Guid.NewGuid().ToString("N");
        string configDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(configDirectory);
        string expected = Path.Combine(configDirectory, "settings.json");
        File.WriteAllText(expected, "{}");
        Environment.SetEnvironmentVariable(variable, absolute ? configDirectory : "config");
        try
        {
            string resolved = PathVariableExpander.ResolveFilePath($"${{env:{variable}}}/settings.json", root);
            Assert.Equal(expected, Path.GetFullPath(resolved));
            Assert.True(Path.IsPathFullyQualified(resolved));
            Assert.Equal("{}", File.ReadAllText(resolved));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            Directory.Delete(root, recursive: true);
        }
    }
}
