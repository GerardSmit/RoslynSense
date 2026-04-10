using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class DebugToolTests
{
    [Fact]
    public async Task WhenStartTestWithoutProjectThenReturnsError()
    {
        var result = await DebugStartTool.DebugStartTest("", new MarkdownFormatter());

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenStartTestWithNonExistentProjectThenReturnsError()
    {
        var result = await DebugStartTool.DebugStartTest("/nonexistent/path.csproj", new MarkdownFormatter());

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenAttachWithInvalidPidThenReturnsError()
    {
        var result = await DebugStartTool.DebugAttach(new MarkdownFormatter(), 999999999);

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenAttachWithZeroPidThenListsProcesses()
    {
        var result = await DebugStartTool.DebugAttach(new MarkdownFormatter(), 0);

        // Should list processes instead of error
        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task WhenSetBreakpointWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugBreakpointTool.DebugSetBreakpoint("test.cs", new MarkdownFormatter(), 10);

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenRemoveBreakpointWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugBreakpointTool.DebugRemoveBreakpoint(1, new MarkdownFormatter());

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenContinueWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugControlTool.DebugContinue(new MarkdownFormatter());

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenStepInWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugControlTool.DebugContinue(new MarkdownFormatter(), "step_in");

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenStepOverWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugControlTool.DebugContinue(new MarkdownFormatter(), "step_over");

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenStepOutWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugControlTool.DebugContinue(new MarkdownFormatter(), "step_out");

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenContinueWithInvalidActionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugControlTool.DebugContinue(new MarkdownFormatter(), "invalid_action");

        // No session check comes first
        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenEvaluateWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugInspectTool.DebugEvaluate("1 + 1", new MarkdownFormatter());

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenBatchEvaluateWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugInspectTool.DebugEvaluate("x;y;z", new MarkdownFormatter());

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenEvaluateEmptyExpressionThenReturnsError()
    {
        var result = await DebugInspectTool.DebugEvaluate("", new MarkdownFormatter());

        Assert.True(
            result.Contains("No active debug session") || result.Contains("No expressions provided"),
            $"Expected error message, got: {result}");
    }

    [Fact]
    public async Task WhenStatusWithoutSessionThenReturnsNoSession()
    {
        DebugControlTool.DebugStop();
        var result = await DebugInspectTool.DebugStatus(new MarkdownFormatter());

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenStatusWithLocalsWithoutSessionThenReturnsNoSession()
    {
        DebugControlTool.DebugStop();
        var result = await DebugInspectTool.DebugStatus(new MarkdownFormatter(), includeLocals: true);

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenStatusWithStackTraceWithoutSessionThenReturnsNoSession()
    {
        DebugControlTool.DebugStop();
        var result = await DebugInspectTool.DebugStatus(new MarkdownFormatter(), includeStackTrace: true);

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
    public async Task WhenSetBreakpointWithConditionWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugBreakpointTool.DebugSetBreakpoint("test.cs", new MarkdownFormatter(), 10, condition: "x > 5");

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenBatchSetBreakpointsWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugBreakpointTool.DebugSetBreakpoint("test.cs:10;other.cs:20", new MarkdownFormatter());

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenBatchRemoveBreakpointsWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugBreakpointTool.DebugRemoveBreakpoint(0, new MarkdownFormatter(), breakpointIds: "1;2;3");

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenRunUntilWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugControlTool.DebugRunUntil("test.cs", 42, new MarkdownFormatter());

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task WhenRunUntilWithConditionWithoutSessionThenReturnsError()
    {
        DebugControlTool.DebugStop();
        var result = await DebugControlTool.DebugRunUntil("test.cs", 42, new MarkdownFormatter(), condition: "i == 5");

        Assert.Contains("No active debug session", result);
    }
}
