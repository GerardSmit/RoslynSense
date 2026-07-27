using System.Diagnostics;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers the environment handed to a spawned build.
/// </summary>
/// <remarks>
/// MSBuildLocator pins this process to the .NET SDK's MSBuild so MSBuildWorkspace can load
/// projects. Those variables must not reach a child Visual Studio MSBuild.exe: it would then
/// resolve <c>$(VSToolsPath)</c> into the SDK directory and fail to import
/// <c>Microsoft.WebApplication.targets</c> (MSB4226), breaking every legacy web project.
/// </remarks>
public class BuildProcessEnvironmentTests
{
    [Theory]
    [InlineData("MSBUILD_EXE_PATH")]
    [InlineData("MSBuildExtensionsPath")]
    [InlineData("MSBuildExtensionsPath32")]
    [InlineData("MSBuildExtensionsPath64")]
    [InlineData("MSBuildSDKsPath")]
    public void WhenConfiguredThenLocatorVariablesAreRemoved(string variable)
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment[variable] = @"C:\Program Files\dotnet\sdk\10.0.204";

        BuildProcessHelper.ConfigureMsBuildEnvironment(startInfo);

        Assert.False(startInfo.Environment.ContainsKey(variable),
            $"{variable} must not be inherited by a child build.");
    }

    [Fact]
    public void WhenConfiguredThenTheMsBuildSafetyVariablesAreSet()
    {
        var startInfo = new ProcessStartInfo();

        BuildProcessHelper.ConfigureMsBuildEnvironment(startInfo);

        Assert.Equal("off", startInfo.Environment["MSBUILDTERMINALLOGGER"]);
        Assert.Equal("1", startInfo.Environment["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("0", startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
    }

    [Fact]
    public void WhenConfiguredThenStandardInputIsRedirected()
    {
        // The build must not inherit this process's stdin. It is the MCP protocol stream, and an
        // inherited never-closing pipe also hangs any git a build task spawns: git probes fd 0 to
        // detect the terminal type even for commands that never read it, and blocks there.
        var startInfo = new ProcessStartInfo();

        BuildProcessHelper.ConfigureMsBuildEnvironment(startInfo);

        Assert.True(startInfo.RedirectStandardInput);
    }

    [Fact]
    public void WhenStartedThenStandardInputIsClosedImmediately()
    {
        // Redirecting without closing is worse than inheriting: the child gets a pipe that never
        // delivers anything and never ends.
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        startInfo.ArgumentList.Add("exit 0");

        BuildProcessHelper.ConfigureMsBuildEnvironment(startInfo);

        using var process = new Process { StartInfo = startInfo };
        BuildProcessHelper.StartWithClosedInput(process);

        Assert.True(process.WaitForExit(30_000), "the process should exit promptly");

        // Writing to a closed stream must fail — proof the handle really was released.
        Assert.ThrowsAny<Exception>(() =>
        {
            process.StandardInput.WriteLine("x");
            process.StandardInput.Flush();
        });
    }

    [Fact]
    public void WhenConfiguredThenUnrelatedVariablesAreLeftAlone()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["PATH"] = "/somewhere";
        startInfo.Environment["MSBuildSomethingElse"] = "keep me";

        BuildProcessHelper.ConfigureMsBuildEnvironment(startInfo);

        Assert.Equal("/somewhere", startInfo.Environment["PATH"]);
        Assert.Equal("keep me", startInfo.Environment["MSBuildSomethingElse"]);
    }
}
