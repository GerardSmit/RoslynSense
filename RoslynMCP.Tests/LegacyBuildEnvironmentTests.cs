using System.Diagnostics;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Building a .NET Framework project from inside the daemon.
/// </summary>
[Collection(SharedState.Name)]
public class LegacyBuildEnvironmentTests
{
    [Fact]
    public void TheSdkMsBuildEnvironmentIsNotHandedToVisualStudiosMsBuild()
    {
        // Registering the .NET SDK for MSBuildWorkspace sets these process-wide, and a child
        // inherits them. VS MSBuild then resolves $(MSBuildExtensionsPath) into the SDK and
        // fails loading a task that only exists beside the SDK's own MSBuild — reported as a
        // missing Microsoft.NET.Build.Extensions.Tasks.dll under C:\Program Files\dotnet.
        WorkspaceService.EnsureRegistered();
        Assert.False(
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSBuildExtensionsPath")),
            "this test is meaningless unless the SDK environment is actually set");

        var startInfo = new ProcessStartInfo("msbuild.exe");
        MsBuildLocator.SetVsEnvironment(startInfo, @"C:\VS\MSBuild\Current\Bin\MSBuild.exe");

        foreach (string variable in new[]
        {
            "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildExtensionsPath32",
            "MSBuildExtensionsPath64", "MSBuildSDKsPath",
        })
        {
            Assert.False(
                startInfo.Environment.TryGetValue(variable, out string? value) && !string.IsNullOrEmpty(value),
                $"{variable} must not reach Visual Studio's MSBuild (was '{value}')");
        }
    }

    [Fact]
    public async Task ALegacyProjectBuilds()
    {
        // Asserted rather than skipped: a silent return would make this pass on the very
        // machine where the bug happens, which is the one with both toolchains installed.
        Assert.NotNull(MsBuildLocator.FindMsBuild());

        // Rebuild, so this cannot pass by finding the project already up to date and doing no
        // work at all — the failure being guarded against happens while loading a task, which
        // an incremental no-op never reaches.
        string output = Path.Combine(
            Path.GetDirectoryName(FixturePaths.LegacyProjectFile)!, "bin", "Debug");
        if (Directory.Exists(output))
        {
            try { Directory.Delete(output, recursive: true); } catch { }
        }

        var result = await LaunchHandler.BuildAsync(FixturePaths.LegacyProjectFile, "Debug", default);

        // The specific failure this guards against names a task assembly under the dotnet SDK.
        string detail = result.Summary + "\n" +
            string.Join("\n", result.Errors.Select(e => e.Message));
        Assert.DoesNotContain("Microsoft.NET.Build.Extensions", detail);
        Assert.True(result.Success, detail);
        Assert.True(
            Directory.Exists(output) && Directory.EnumerateFiles(output, "*.dll").Any(),
            "the build reported success but produced no assembly");
    }
}
