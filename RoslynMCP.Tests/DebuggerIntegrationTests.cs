using RoslynMCP.Services;
using Xunit;
using Xunit.Abstractions;

namespace RoslynMCP.Tests;

/// <summary>
/// Integration tests for the full debug workflow using a real netcoredbg process.
/// These tests use the DebugTestProject fixture and verify the complete flow:
/// start test session → breakpoint hit → inspect → step → continue → exit.
/// </summary>
public class DebuggerIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private DebuggerService? _service;

    public DebuggerIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_service is not null)
        {
            _service.Stop();
            _service.Dispose();
        }

        // Clean up any lingering processes from failed tests
        await Task.Delay(500);
    }

    private string ProjectFile => FixturePaths.DebugTestProjectFile;
    private string CalculatorTestsFile => FixturePaths.DebugCalculatorTestsFile;
    private string CalculatorFile => FixturePaths.DebugCalculatorFile;

    [Fact]
    public async Task FullDebugFlow_BreakpointHitAndInspect()
    {
        _service = new DebuggerService();

        // Line 12 in CalculatorTests.cs: var result = Calculator.Add(a, b);
        var breakpointLine = 12;
        var initialBreakpoints = new[] { (CalculatorTestsFile, breakpointLine) };

        _output.WriteLine($"Starting test debug session for {ProjectFile}");
        _output.WriteLine($"Breakpoint at {CalculatorTestsFile}:{breakpointLine}");

        var startResult = await _service.StartTestSessionAsync(
            ProjectFile,
            "CalculatorTests.Add_ReturnsSum",
            initialBreakpoints);

        _output.WriteLine($"Start result: {startResult}");

        Assert.DoesNotContain("Error", startResult);
        Assert.Contains("Debug session started", startResult);

        // The test should auto-resume and hit the breakpoint
        Assert.Contains("breakpoint", startResult, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DebuggerService.DebugState.Stopped, _service.State);
        Assert.NotNull(_service.CurrentFrame);
        Assert.Equal("breakpoint-hit", _service.CurrentFrame.Reason);

        _output.WriteLine($"Stopped at: {_service.CurrentFrame.Function} line {_service.CurrentFrame.Line}");

        // Inspect locals
        var locals = await _service.GetLocalsAsync();
        _output.WriteLine($"Locals: {locals}");
        Assert.DoesNotContain("Error", locals);

        // Get stack trace
        var stack = await _service.GetStackTraceAsync();
        _output.WriteLine($"Stack: {stack}");
        Assert.DoesNotContain("Error", stack);

        // Evaluate an expression
        var evalResult = await _service.EvaluateAsync("a + b");
        _output.WriteLine($"Evaluate a+b: {evalResult}");

        // Continue to completion
        var continueResult = await _service.ContinueAsync();
        _output.WriteLine($"Continue result: {continueResult}");
        Assert.True(
            _service.State == DebuggerService.DebugState.Exited,
            $"Expected Exited state but got {_service.State}");
    }

    [Fact]
    public async Task DebugFlow_StepIntoCalledMethod()
    {
        _service = new DebuggerService();

        // Line 12 in CalculatorTests.cs: var result = Calculator.Add(a, b);
        var breakpointLine = 12;
        var initialBreakpoints = new[] { (CalculatorTestsFile, breakpointLine) };

        var startResult = await _service.StartTestSessionAsync(
            ProjectFile,
            "CalculatorTests.Add_ReturnsSum",
            initialBreakpoints);

        _output.WriteLine($"Start result: {startResult}");
        Assert.DoesNotContain("Error", startResult);
        Assert.Equal(DebuggerService.DebugState.Stopped, _service.State);

        // Step into Calculator.Add
        var stepResult = await _service.StepInAsync();
        _output.WriteLine($"Step in: {stepResult}");
        Assert.DoesNotContain("Error", stepResult);
        Assert.Equal(DebuggerService.DebugState.Stopped, _service.State);
        Assert.NotNull(_service.CurrentFrame);

        // We should now be inside Calculator.Add
        _output.WriteLine($"After step in: {_service.CurrentFrame.Function} at {_service.CurrentFrame.FilePath}:{_service.CurrentFrame.Line}");

        // Step over
        var stepOverResult = await _service.StepOverAsync();
        _output.WriteLine($"Step over: {stepOverResult}");
        Assert.DoesNotContain("Error", stepOverResult);

        // Continue to exit
        var continueResult = await _service.ContinueAsync();
        _output.WriteLine($"Continue: {continueResult}");
        Assert.True(_service.State == DebuggerService.DebugState.Exited,
            $"Expected Exited state but got {_service.State}");
    }

    [Fact]
    public async Task DebugFlow_MultipleBreakpoints()
    {
        _service = new DebuggerService();

        // Set breakpoints on both test methods
        // Line 12: var result = Calculator.Add(a, b);
        // Line 21: var result = Calculator.Multiply(a, b);
        var initialBreakpoints = new[]
        {
            (CalculatorTestsFile, 12),
            (CalculatorTestsFile, 21)
        };

        var startResult = await _service.StartTestSessionAsync(
            ProjectFile,
            "CalculatorTests",
            initialBreakpoints);

        _output.WriteLine($"Start result: {startResult}");
        Assert.DoesNotContain("Error", startResult);

        // Should hit first breakpoint
        Assert.Equal(DebuggerService.DebugState.Stopped, _service.State);
        Assert.NotNull(_service.CurrentFrame);
        _output.WriteLine($"First stop: {_service.CurrentFrame.Function} line {_service.CurrentFrame.Line}");

        // Continue to hit second breakpoint
        var continueResult = await _service.ContinueAsync();
        _output.WriteLine($"Continue 1: {continueResult}");

        if (_service.State == DebuggerService.DebugState.Stopped && _service.CurrentFrame is not null)
        {
            _output.WriteLine($"Second stop: {_service.CurrentFrame.Function} line {_service.CurrentFrame.Line}");

            // Continue to exit
            continueResult = await _service.ContinueAsync();
            _output.WriteLine($"Continue 2: {continueResult}");
        }

        Assert.True(_service.State == DebuggerService.DebugState.Exited,
            $"Expected Exited state but got {_service.State}");
    }

    [Fact]
    public async Task DebugFlow_BreakpointInSourceFile()
    {
        _service = new DebuggerService();

        // Set breakpoint inside Calculator.Add (line 7: var result = a + b;)
        var initialBreakpoints = new[] { (CalculatorFile, 7) };

        var startResult = await _service.StartTestSessionAsync(
            ProjectFile,
            "CalculatorTests.Add_ReturnsSum",
            initialBreakpoints);

        _output.WriteLine($"Start result: {startResult}");
        Assert.DoesNotContain("Error", startResult);

        Assert.Equal(DebuggerService.DebugState.Stopped, _service.State);
        Assert.NotNull(_service.CurrentFrame);
        _output.WriteLine($"Stopped at: {_service.CurrentFrame.Function} line {_service.CurrentFrame.Line}");

        // Should be inside the Add method
        Assert.Contains("Add", _service.CurrentFrame.Function);

        // Inspect locals — should see a, b parameters
        var locals = await _service.GetLocalsAsync();
        _output.WriteLine($"Locals: {locals}");
        Assert.Contains("a", locals);
        Assert.Contains("b", locals);

        // Continue to exit
        var continueResult = await _service.ContinueAsync();
        Assert.True(_service.State == DebuggerService.DebugState.Exited,
            $"Expected Exited state but got {_service.State}");
    }

    [Fact]
    public async Task DebugFlow_NoBreakpoints_TestRunsToCompletion()
    {
        _service = new DebuggerService();

        var startResult = await _service.StartTestSessionAsync(
            ProjectFile,
            "CalculatorTests.Add_ReturnsSum");

        _output.WriteLine($"Start result: {startResult}");
        Assert.DoesNotContain("Error", startResult);
        Assert.Contains("continue", startResult, StringComparison.OrdinalIgnoreCase);
    }
}
