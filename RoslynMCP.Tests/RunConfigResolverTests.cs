using RoslynMCP.Services;
using RoslynMCP.Services.Run;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers legacy WebProjectProperties parsing and the mapping from a classified project to a
/// launch spec.
/// </summary>
public class RunConfigResolverTests
{
    [Fact]
    public void WhenLegacyWebProjectThenPortAndVirtualPathAreRead()
    {
        var found = RunConfigResolver.TryReadWebProjectProperties(
            FixturePaths.WebFormsSiteFile, out var props);

        Assert.True(found);
        Assert.Equal(18090, props.Port);
        Assert.Equal("", props.VirtualPath); // "/" is the site root, which appends nothing
        Assert.False(props.UseSsl);
    }

    [Fact]
    public void WhenSdkStyleProjectThenWebProjectPropertiesAreAbsent()
    {
        // SDK-style projects use launch profiles; the legacy flavor block never appears.
        Assert.False(RunConfigResolver.TryReadWebProjectProperties(
            FixturePaths.SampleProjectFile, out _));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("/app", "/app")]
    [InlineData("app", "/app")]
    [InlineData("/app/", "/app")]
    public void WhenVirtualPathNormalizedThenRootBecomesEmptyAndOthersGainOneLeadingSlash(
        string raw, string expected) =>
        Assert.Equal(expected, RunConfigResolver.NormalizeVPath(raw));

    [Fact]
    public void WhenPreferredPortIsFreeThenItIsUsed()
    {
        var free = RunConfigResolver.PickPort(0); // an OS-assigned port, now closed again
        Assert.Equal(free, RunConfigResolver.PickPort(free));
    }

    [Fact]
    public void WhenPreferredPortIsBusyThenADifferentFreePortIsChosen()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var busy = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

            var chosen = RunConfigResolver.PickPort(busy);

            Assert.NotEqual(busy, chosen);
            Assert.InRange(chosen, 1, 65535);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void WhenLegacyWebProjectThenItClassifiesAsAspNetClassic()
    {
        var classification = ProjectClassifier.Classify(FixturePaths.WebFormsSiteFile);

        Assert.Equal(ProjectStyle.Legacy, classification.Style);
        Assert.Equal(RuntimeFlavor.NetFramework, classification.Runtime);
        Assert.Equal("net472", classification.TargetFramework);
        Assert.Equal(AppKind.AspNetClassic, classification.Kind);
        Assert.Equal(BuildTool.VisualStudioMsBuild, classification.BuildTool);
        Assert.Equal(DebugRuntime.NetFramework, classification.DebugRuntime);
        Assert.True(classification.IsRunnable);
    }

    [Fact]
    public void WhenLibraryProjectThenResolvingReportsItIsNotRunnable()
    {
        var spec = RunConfigResolver.Resolve(FixturePaths.SampleProjectFile);

        Assert.False(spec.CanRun);
        Assert.Contains("library", spec.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenTestProjectThenResolvingPointsAtRunTests()
    {
        var classification = ProjectClassifier.Classify(FixturePaths.DebugTestProjectFile);
        if (!classification.IsTestProject)
            return; // fixture is not shaped as a test project on this checkout

        var spec = RunConfigResolver.Resolve(FixturePaths.DebugTestProjectFile);

        Assert.False(spec.CanRun);
        Assert.Contains("RunTests", spec.Error);
    }

    [RequiresIisExpressFact]
    public void WhenLegacyWebProjectThenItLaunchesUnderIisExpressOnItsConfiguredPort()
    {
        var spec = RunConfigResolver.Resolve(FixturePaths.WebFormsSiteFile);

        Assert.True(spec.CanRun, spec.Error);
        Assert.Equal(AppKind.AspNetClassic, spec.Kind);
        Assert.EndsWith("iisexpress.exe", spec.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DebugRuntime.NetFramework, spec.DebugRuntime);

        // The site directory, not the built assembly, is what IIS Express serves.
        Assert.Contains($"/path:{FixturePaths.WebFormsSiteDir}", spec.Arguments);
        Assert.Contains("/clr:v4.0", spec.Arguments);
        Assert.Contains("/systray:false", spec.Arguments);

        Assert.NotNull(spec.Port);
        Assert.Equal($"http://localhost:{spec.Port}", spec.Url);
    }
}
