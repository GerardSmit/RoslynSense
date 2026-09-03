using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public sealed class CliVersionTests
{
    [Fact]
    public void VersionCommandUsesTheProductAssemblyVersion()
    {
        string expected = typeof(WorkspaceService).Assembly.GetName().Version!.ToString(3);

        Assert.Equal(expected, Program.CurrentVersion());
        Assert.Matches(@"^\d+\.\d+\.\d+$", Program.CurrentVersion());
    }
}
