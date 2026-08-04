using RoslynMCP.Config;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Regenerating a designer rewrites a file in the user's tree, so the CLI has to gate it on the
/// same setting the MCP server and the shared host gate it on. The CLI builds the service by hand
/// instead of resolving it from a container, which is exactly how the three drifted apart before.
/// </summary>
public class CliRunnerDesignerGateTests
{
    [Fact]
    public void WebFormsOnHandlesAspx()
    {
        var service = CliRunner.CreateDesignerService(Settings());

        Assert.True(service.IsGeneratedFrom(@"C:\site\Default.aspx"));
        Assert.True(service.IsGeneratedFrom(@"C:\site\Controls\Menu.ascx"));
    }

    [Fact]
    public void WebFormsOffLeavesAspxAlone()
    {
        var service = CliRunner.CreateDesignerService(Settings("--no-webforms"));

        Assert.False(service.IsGeneratedFrom(@"C:\site\Default.aspx"));
        Assert.False(service.IsGeneratedFrom(@"C:\site\Controls\Menu.ascx"));
    }

    /// <summary>The dbml generator is not a language pack, so no language flag may switch it off.</summary>
    [Fact]
    public void DbmlSurvivesWebFormsBeingOff()
    {
        var service = CliRunner.CreateDesignerService(Settings("--no-webforms"));

        Assert.True(service.IsGeneratedFrom(@"C:\site\Model.dbml"));
    }

    private static EffectiveSettings Settings(params string[] args) =>
        EffectiveSettings.Resolve(args, null, out _);
}
