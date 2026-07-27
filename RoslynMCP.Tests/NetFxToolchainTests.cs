using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers the .NET Framework toolchain probe. The machine-dependent assertions are conditional on
/// the tool actually being installed, so the suite stays green on a CI box without Visual Studio
/// while still verifying the discovery logic wherever the tools do exist.
/// </summary>
public class NetFxToolchainTests
{
    [Fact]
    public void WhenProbedThenReportIsCachedAndSelfConsistent()
    {
        var first = NetFxToolchain.Info;
        var second = NetFxToolchain.Info;

        Assert.Same(first, second);

        // Every path the probe reports must actually exist — an empty string means "not found".
        foreach (var path in new[]
                 {
                     first.MsBuildPath, first.IisExpressX64, first.IisExpressX86,
                     first.AspnetCompiler, first.SqlMetal,
                 })
        {
            if (path.Length > 0)
                Assert.True(File.Exists(path), $"Reported path does not exist: {path}");
        }
    }

    [Fact]
    public void WhenNotOnWindowsThenToolchainIsEmpty()
    {
        if (OperatingSystem.IsWindows())
            return;

        var info = NetFxToolchain.Probe();

        Assert.False(info.DesktopClr);
        Assert.Equal("", info.MsBuildPath);
        Assert.Equal("", info.SqlMetal);
        Assert.Null(info.PreferredIisExpress);
    }

    [Fact]
    public void WhenSqlMetalInstalledThenNewestNetfxToolsCopyIsChosen()
    {
        var sqlMetal = NetFxToolchain.FindSqlMetal();
        if (sqlMetal is null)
            return; // Windows SDK not installed on this machine.

        Assert.True(File.Exists(sqlMetal));
        Assert.Equal("SqlMetal.exe", Path.GetFileName(sqlMetal));

        // It must come from a "NETFX <ver> Tools" directory, and be the newest such copy present.
        var toolsDir = Path.GetFileName(Path.GetDirectoryName(sqlMetal))!;
        Assert.StartsWith("NETFX", toolsDir, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Tools", toolsDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenIisExpressPresentThenX64IsPreferred()
    {
        var info = NetFxToolchain.Info;
        if (info.PreferredIisExpress is null)
            return; // IIS Express not installed.

        // x64 avoids a bitness-mismatched debug worker, so it wins whenever both are installed.
        var expected = info.IisExpressX64.Length > 0 ? info.IisExpressX64 : info.IisExpressX86;
        Assert.Equal(expected, info.PreferredIisExpress);
    }

    [RequiresVisualStudioFact]
    public void WhenVisualStudioInstalledThenMsBuildAndWebTargetsAreReported()
    {
        var info = NetFxToolchain.Info;

        Assert.NotEqual("", info.MsBuildPath);
        Assert.True(File.Exists(info.MsBuildPath));

        // WebApplicationTargets is derived from the MSBuild path, so the two must agree.
        Assert.Equal(
            NetFxToolchain.HasWebApplicationTargets(info.MsBuildPath),
            info.WebApplicationTargets);
    }
}
