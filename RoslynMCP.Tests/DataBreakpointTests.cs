using RoslynMCP.Services;
using RoslynMCP.Services.Debugging;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Data breakpoints, which neither engine has and which are therefore built out of stepping and
/// evaluation. The tests drive the watcher against a scripted backend so the mechanism — step,
/// re-evaluate, compare — is checked without a debuggee.
/// </summary>
public class DataBreakpointTests
{
    // === Arming ===

    [Fact]
    public async Task AWatchIsArmedOnlyWhenItsExpressionEvaluates()
    {
        var backend = new ScriptedBackend();
        backend.Values["order.Total"] = ["10"];
        var watcher = new DataBreakpointWatcher(backend);

        var results = await watcher.SetAsync([
            new DataBreakpointSpec("0:order.Total", "order.Total"),
            new DataBreakpointSpec("0:nonsense", "nonsense"),
        ]);

        Assert.True(results[0].Verified);
        Assert.False(results[1].Verified);
        Assert.Contains("does not evaluate", results[1].Message);
        Assert.Single(watcher.Watches);
    }

    [Fact]
    public async Task ReadAccessIsRefusedRatherThanSilentlyTreatedAsAWrite()
    {
        // Comparing values cannot see a read, so accepting the request would be a lie.
        var backend = new ScriptedBackend();
        backend.Values["total"] = ["1"];
        var watcher = new DataBreakpointWatcher(backend);

        var results = await watcher.SetAsync([
            new DataBreakpointSpec("0:total", "total", AccessType: "read"),
        ]);

        Assert.False(results[0].Verified);
        Assert.Contains("only writes", results[0].Message);
    }

    [Fact]
    public void TheDataIdCarriesTheFrameSoTwoLocalsWithOneNameStayDistinct()
    {
        string inner = DataBreakpointId.For("total", 0);
        string outer = DataBreakpointId.For("total", 3);

        Assert.NotEqual(inner, outer);
        Assert.Equal("total", DataBreakpointId.ExpressionOf(inner));
        Assert.Equal("total", DataBreakpointId.ExpressionOf(outer));
    }

    // === Detection ===

    [Fact]
    public async Task AChangedValueIsReportedWithBothSidesOfIt()
    {
        var backend = new ScriptedBackend();
        backend.Values["total"] = ["10", "10", "42"];
        var watcher = new DataBreakpointWatcher(backend);
        await watcher.SetAsync([new DataBreakpointSpec("0:total", "total")]);

        var (outcome, _) = await watcher.ContinueAsync(() => Task.FromResult(false));

        Assert.Equal(DataWatchOutcome.Changed, outcome);
        Assert.Equal("10", watcher.LastHit!.OldValue);
        Assert.Equal("42", watcher.LastHit.NewValue);
        Assert.Contains("10 → 42", watcher.LastHit.Description);
    }

    [Fact]
    public async Task AnUnchangedValueDoesNotStop()
    {
        var backend = new ScriptedBackend { StepsBeforeExit = 5 };
        backend.Values["total"] = ["10"];
        var watcher = new DataBreakpointWatcher(backend);
        await watcher.SetAsync([new DataBreakpointSpec("0:total", "total")]);

        var (outcome, _) = await watcher.ContinueAsync(() => Task.FromResult(false));

        Assert.Equal(DataWatchOutcome.Exited, outcome);
        Assert.Null(watcher.LastHit);
    }

    [Fact]
    public async Task AnExpressionThatGoesOutOfScopeIsSkippedRatherThanCountedAsAChange()
    {
        // Stepping into a callee makes the caller's local unresolvable; treating that as a change
        // would stop on every method call.
        var backend = new ScriptedBackend();
        backend.Values["total"] = ["10", "Error: not in scope", "10"];
        backend.StepsBeforeExit = 3;
        var watcher = new DataBreakpointWatcher(backend);
        await watcher.SetAsync([new DataBreakpointSpec("0:total", "total")]);

        var (outcome, _) = await watcher.ContinueAsync(() => Task.FromResult(false));

        Assert.Equal(DataWatchOutcome.Exited, outcome);
        Assert.Null(watcher.LastHit);
    }

    [Fact]
    public async Task ARealBreakpointStillWinsWhileAWatchIsArmed()
    {
        var backend = new ScriptedBackend { BreakpointNumberAfterStep = 7 };
        backend.Values["total"] = ["10"];
        var watcher = new DataBreakpointWatcher(backend);
        await watcher.SetAsync([new DataBreakpointSpec("0:total", "total")]);

        var (outcome, _) = await watcher.ContinueAsync(() => Task.FromResult(true));

        Assert.Equal(DataWatchOutcome.OtherStop, outcome);
    }

    // === Conditions ===

    [Fact]
    public async Task AConditionSuppressesTheStopButTheBaselineStillMoves()
    {
        // The change happened whether or not it was surfaced, so reporting it again on the next
        // step would be wrong.
        var backend = new ScriptedBackend();
        backend.Values["total"] = ["10", "42", "99"];
        // Read once per detected change, so the second change is the one that passes.
        backend.Values["gate"] = ["false", "true"];
        var watcher = new DataBreakpointWatcher(backend);
        await watcher.SetAsync([new DataBreakpointSpec("0:total", "total", Condition: "gate")]);

        var (outcome, _) = await watcher.ContinueAsync(() => Task.FromResult(false));

        Assert.Equal(DataWatchOutcome.Changed, outcome);
        // 10→42 was swallowed by the condition; the surfaced change is the next one.
        Assert.Equal("42", watcher.LastHit!.OldValue);
        Assert.Equal("99", watcher.LastHit.NewValue);
    }

    [Fact]
    public async Task AHitCountLetsTheFirstChangesPass()
    {
        var backend = new ScriptedBackend();
        backend.Values["total"] = ["0", "1", "2", "3"];
        var watcher = new DataBreakpointWatcher(backend);
        await watcher.SetAsync([new DataBreakpointSpec("0:total", "total", HitCondition: ">= 3")]);

        var (outcome, _) = await watcher.ContinueAsync(() => Task.FromResult(false));

        Assert.Equal(DataWatchOutcome.Changed, outcome);
        Assert.Equal("3", watcher.LastHit!.NewValue);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("null", false)]
    [InlineData("", false)]
    public void AConditionReadsTheSameWayABreakpointConditionDoes(string value, bool expected) =>
        Assert.Equal(expected, DataBreakpointWatcher.IsTruthy(value));

    // === Through the decorator ===

    [Fact]
    public async Task ContinueBecomesAStepWalkOnlyWhileAWatchIsArmed()
    {
        var backend = new ScriptedBackend();
        backend.Values["total"] = ["10", "42"];
        var session = new PublishingDebugBackend(backend);

        await session.ContinueAsync();
        Assert.Equal(0, backend.Steps);
        Assert.Equal(1, backend.Continues);

        await session.SetDataBreakpointsAsync([new DataBreakpointSpec("0:total", "total")]);
        string result = await session.ContinueAsync();

        Assert.True(backend.Steps > 0);
        Assert.Contains("Data breakpoint hit", result);
    }

    [Fact]
    public async Task DroppingTheWatchesGivesNormalContinueBack()
    {
        var backend = new ScriptedBackend();
        backend.Values["total"] = ["10", "42"];
        var session = new PublishingDebugBackend(backend);
        await session.SetDataBreakpointsAsync([new DataBreakpointSpec("0:total", "total")]);

        await session.SetDataBreakpointsAsync([]);
        backend.Steps = 0;
        await session.ContinueAsync();

        Assert.Equal(0, backend.Steps);
    }

    /// <summary>
    /// A backend whose evaluations follow a script: each call to an expression returns the next
    /// value, holding at the last. That is enough to model "the value changed on step 2".
    /// </summary>
    private sealed class ScriptedBackend : IDebugBackend
    {
        private readonly Dictionary<string, int> _reads = [];

        public Dictionary<string, string[]> Values { get; } = [];
        public int Steps;
        public int Continues;

        /// <summary>Steps after which the target reports as exited; 0 means it never does.</summary>
        public int StepsBeforeExit;

        public int BreakpointNumberAfterStep;

        public DebuggerService.StoppedFrame? CurrentFrame { get; private set; } =
            new("breakpoint-hit", "Order.Total", @"C:\src\Order.cs", 10, 1);

        public Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default)
        {
            if (!Values.TryGetValue(expression, out var script))
                return Task.FromResult($"Error: unknown expression '{expression}'");

            _reads.TryGetValue(expression, out int index);
            _reads[expression] = index + 1;
            return Task.FromResult(script[Math.Min(index, script.Length - 1)]);
        }

        public Task<string> StepInAsync(CancellationToken cancellationToken = default)
        {
            Steps++;
            if (StepsBeforeExit > 0 && Steps >= StepsBeforeExit)
                CurrentFrame = null;
            else if (BreakpointNumberAfterStep > 0)
                CurrentFrame = CurrentFrame! with { BreakpointNumber = BreakpointNumberAfterStep };

            return Task.FromResult("stepped");
        }

        public Task<string> ContinueAsync(CancellationToken cancellationToken = default)
        {
            Continues++;
            return Task.FromResult("continued");
        }

        public Task<string> StepOverAsync(CancellationToken cancellationToken = default) => StepInAsync(cancellationToken);
        public Task<string> StepOutAsync(CancellationToken cancellationToken = default) => StepInAsync(cancellationToken);

        public Task<string> StartTestSessionAsync(string csprojPath, string? filter,
            IEnumerable<(string file, int line)>? initialBreakpoints = null,
            CancellationToken cancellationToken = default) => Task.FromResult("started");

        public Task<string> AttachToProcessAsync(int pid,
            IEnumerable<(string file, int line)>? initialBreakpoints = null,
            CancellationToken cancellationToken = default) => Task.FromResult("attached");

        public Task<(string Message, int? BreakpointId)> SetBreakpointAsync(
            string filePath, int line, string? condition = null, string? hitCondition = null,
            string? logMessage = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(("set", (int?)1));

        public Task<string> RemoveBreakpointAsync(int breakpointId, CancellationToken cancellationToken = default) =>
            Task.FromResult("removed");

        public Task<string> GetLocalsAsync(CancellationToken cancellationToken = default) => Task.FromResult("locals");
        public Task<string> GetStackTraceAsync(CancellationToken cancellationToken = default) => Task.FromResult("stack");
        public Task<string> InterruptAsync(CancellationToken cancellationToken = default) => Task.FromResult("paused");

        public Task<IReadOnlyList<StackFrameInfo>> GetStackFramesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StackFrameInfo>>([]);

        public Task<IReadOnlyList<VariableInfo>> GetVariablesAsync(
            int frameId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VariableInfo>>([]);

        public Task<IReadOnlyList<VariableInfo>> GetVariableChildrenAsync(
            int variablesReference, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VariableInfo>>([]);

        public Task<(bool Ok, string Value, string Error)> SetVariableAsync(
            string name, string value, int frameId = 0, CancellationToken cancellationToken = default) =>
            Task.FromResult((true, value, ""));

        public Task<string> SelectFrameAsync(int frameId, CancellationToken cancellationToken = default) =>
            Task.FromResult("selected");

        public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ThreadInfo>>([]);

        public Task<ExceptionDetail?> GetExceptionInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ExceptionDetail?>(null);

        public Task<string> SetExceptionFiltersAsync(
            ExceptionFilters filters, CancellationToken cancellationToken = default) => Task.FromResult("ok");

        public string GetStatus() => "status";
        public string Stop() => "stopped";
        public void Dispose() { }
    }
}
