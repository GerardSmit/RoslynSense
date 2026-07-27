using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers expression evaluation on the .NET Framework engine, including function evaluation —
/// the parity gap that previously stopped ICorDebug from replacing netcoredbg outright.
/// </summary>
/// <remarks>
/// The target's <c>Count</c> is a manual property (backing field <c>_count</c>, so the name
/// <c>Count</c> resolves to no field) and <c>Doubled</c> is computed with no backing field at all.
/// Reading either therefore proves a real getter call ran in the debuggee.
/// </remarks>
[Collection(DebuggerCollection.Name)]
public class DebugEvaluationTests : IAsyncLifetime
{
    private FxTargetProcess? _target;
    private DebugSession? _session;

    public async Task InitializeAsync()
    {
        if (!FxTargetProcess.IsAvailable)
            return;

        _target = FxTargetProcess.Launch();
        _session = new DebugSession(1);

        var stopped = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            await foreach (var e in _session.Events.ReadAllAsync())
            {
                if (e.Kind == DebugEventKind.Breakpoint)
                    stopped.TrySetResult();
            }
        });

        _session.Attach(
            _target.Process.Id,
            [new BreakpointSpec { FilePath = FxTargetProcess.SourcePath, Line = (uint)FxTargetProcess.BreakpointLine }],
            DebugRuntime.NetFramework);

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public Task DisposeAsync()
    {
        try { _session?.Terminate(); } catch { }
        _target?.Dispose();
        return Task.CompletedTask;
    }

    [RequiresNetFrameworkFact]
    public async Task WhenExpressionIsAnArgumentThenItResolvesWithoutFuncEval()
    {
        var (ok, value, error) = await Session.EvaluateAsync(0, "input");

        Assert.True(ok, error);
        Assert.True(int.TryParse(value, out _), $"expected an integer, got '{value}'");
    }

    [RequiresNetFrameworkFact]
    public async Task WhenExpressionIsAPropertyWithABackingFieldThenTheGetterIsCalled()
    {
        // 'Count' names no field — only '_count' exists — so this can only come from get_Count.
        var (ok, value, error) = await Session.EvaluateAsync(0, "counter.Count");

        Assert.True(ok, error);
        Assert.True(int.TryParse(value, out var count), $"expected an integer, got '{value}'");
        Assert.True(count >= 1, "the target bumps the counter before each call");
    }

    [RequiresNetFrameworkFact]
    public async Task WhenExpressionIsAComputedPropertyThenFuncEvalProducesIt()
    {
        var counted = await Session.EvaluateAsync(0, "counter.Count");
        var doubled = await Session.EvaluateAsync(0, "counter.Doubled");

        Assert.True(counted.Ok, counted.Error);
        Assert.True(doubled.Ok, doubled.Error);

        // Doubled has no storage anywhere; the only way to obtain it is to run the getter.
        Assert.Equal(int.Parse(counted.Value) * 2, int.Parse(doubled.Value));
    }

    [RequiresNetFrameworkFact]
    public async Task WhenExpressionIsAMethodCallThenItIsInvoked()
    {
        var (ok, value, error) = await Session.EvaluateAsync(0, "counter.Describe()");

        Assert.True(ok, error);
        Assert.Contains("count=", value);
    }

    [RequiresNetFrameworkFact]
    public async Task WhenMemberDoesNotExistThenAClearErrorIsReturned()
    {
        var (ok, _, error) = await Session.EvaluateAsync(0, "counter.NoSuchMember");

        Assert.False(ok);
        Assert.Contains("NoSuchMember", error);
    }

    [RequiresNetFrameworkFact]
    public async Task WhenVariableIsAssignedThenTheNewValueIsReadBack()
    {
        var (ok, _, error) = await Session.SetVariableAsync(0, "input", "4242");
        Assert.True(ok, error);

        var read = await Session.EvaluateAsync(0, "input");
        Assert.True(read.Ok, read.Error);
        Assert.Equal("4242", read.Value);
    }

    [RequiresNetFrameworkFact]
    public async Task WhenAssignedValueIsNotValidForTheTypeThenItIsRejected()
    {
        var (ok, _, error) = await Session.SetVariableAsync(0, "input", "not-a-number");

        Assert.False(ok);
        Assert.NotEqual("", error);
    }

    [RequiresNetFrameworkFact]
    public async Task WhenStoppedThenLocalsAndStackRemainReadableAfterAnEvaluation()
    {
        // A func-eval resumes the debuggee to run the getter; the session must be left stopped in
        // the same frame afterwards, or every evaluation would lose the user's position.
        await Session.EvaluateAsync(0, "counter.Doubled");

        var frames = await Session.StackTraceAsync();
        Assert.Contains(frames, f => f.Method.Contains("Compute", StringComparison.Ordinal));

        var variables = await Session.VariablesAsync(0);
        Assert.Contains(variables, v => v.Name == "input");
    }

    private DebugSession Session => _session
        ?? throw new InvalidOperationException("The debug session was not started.");
}
