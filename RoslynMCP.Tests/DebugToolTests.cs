using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class DebugToolTests
{
    [Fact]
    public async Task WhenStartTestWithoutProjectThenReturnsError()
    {
        var result = await DebugStartTool.DebugStartTest("");

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenStartTestWithNonExistentProjectThenReturnsError()
    {
        var result = await DebugStartTool.DebugStartTest("/nonexistent/path.csproj");

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenAttachWithInvalidPidThenReturnsError()
    {
        var result = await DebugStartTool.DebugAttach(999999999);

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenSetBreakpointWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugBreakpointTool.DebugSetBreakpoint("test.cs", 10);

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenRemoveBreakpointWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugBreakpointTool.DebugRemoveBreakpoint(1);

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenContinueWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugControlTool.DebugContinue();

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenStepInWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugControlTool.DebugStepIn();

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenStepOverWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugControlTool.DebugStepOver();

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenStepOutWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugControlTool.DebugStepOut();

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenEvaluateWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugInspectTool.DebugEvaluate("1 + 1");

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenLocalsWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugInspectTool.DebugLocals();

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenStackTraceWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugInspectTool.DebugStackTrace();

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public void WhenStatusWithoutSessionThenReturnsNoSession()
    {
        DebugControlTool.DebugStop();
        var result = DebugInspectTool.DebugStatus();

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public void WhenStopWithoutSessionThenReturnsNoSession()
    {
        DebugControlTool.DebugStop();
        var result = DebugControlTool.DebugStop();

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenListProcessesThenReturnsOutput()
    {
        var result = await DebugStartTool.DebugListProcesses();

        Assert.NotEmpty(result);
    }
}
