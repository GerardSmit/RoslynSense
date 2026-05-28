using RoslynMCP.Config;
using RoslynMCP.Daemon;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class DaemonHostTests
{
    [Fact]
    public async Task IpcProtocolRoundTripsBackToBackMessages()
    {
        using var stream = new MemoryStream();

        var request = new DaemonRequest("id-1", "list_projects",
            new Dictionary<string, string> { ["path"] = @"C:\foo\bar.sln" }, "markdown");
        var response = new DaemonResponse("id-1", true, "the result text", null);

        await IpcProtocol.WriteMessageAsync(stream, request, default);
        await IpcProtocol.WriteMessageAsync(stream, response, default);

        stream.Position = 0;

        var readRequest = await IpcProtocol.ReadMessageAsync<DaemonRequest>(stream, default);
        var readResponse = await IpcProtocol.ReadMessageAsync<DaemonResponse>(stream, default);
        var atEof = await IpcProtocol.ReadMessageAsync<DaemonRequest>(stream, default);

        Assert.NotNull(readRequest);
        Assert.Equal("list_projects", readRequest!.Tool);
        Assert.Equal(@"C:\foo\bar.sln", readRequest.Args["path"]);
        Assert.Equal("markdown", readRequest.Format);

        Assert.NotNull(readResponse);
        Assert.True(readResponse!.Ok);
        Assert.Equal("the result text", readResponse.Result);

        Assert.Null(atEof); // clean EOF
    }

    [Fact]
    public async Task ToolInvokerDispatchesToolThroughSharedServiceProvider()
    {
        // This is the daemon's dispatch core: resolve a tool by name, bind its string arg from
        // the IPC dictionary, inject IOutputFormatter/CancellationToken from DI, invoke, return text.
        var settings = EffectiveSettings.Resolve(Array.Empty<string>(), null, out _);
        await using var services = ToolHostServices.Build(settings, new MarkdownFormatter(), FixturePaths.MultiSolutionDir);

        var method = ToolInvoker.FindTool("list_projects");
        Assert.NotNull(method);

        var args = new Dictionary<string, string> { ["path"] = FixturePaths.MultiSolutionFile };
        string result = await ToolInvoker.InvokeAsync(method!, args, services, new MarkdownFormatter(), default);

        Assert.Contains("ProjectA", result);
        Assert.Contains("ProjectB", result);
    }

    [Fact]
    public void ResourceDiscoveryResolvesTemplatedResourcesByName()
    {
        // The daemon resolves forwarded resource reads via FindResource. Both shipped
        // resource templates must be discoverable so they can be served by the host.
        Assert.NotNull(ToolInvoker.FindResource("project-structure"));
        Assert.NotNull(ToolInvoker.FindResource("file-outline"));
        Assert.Null(ToolInvoker.FindResource("no-such-resource"));
    }
}
