using RoslynMCP.Services;
using RoslynMCP.Services.Debugging;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The structured debug surface: MI responses parsed into records the editor's Call Stack and
/// Variables views can render, plus the hit counts and logpoints neither engine implements.
/// </summary>
public class StructuredDebugTests
{
    // === Stack frames ===

    [Fact]
    public void FramesCarryFullPathsSoDeepFramesStayNavigable()
    {
        // The markdown surface shows only file names; the editor needs the full path or the
        // frame is not clickable.
        const string response = """
            ^done,stack=[frame={level="0",func="Order.Total",file="Order.cs",fullname="C:\\src\\Order.cs",line="42",col="9"},frame={level="1",func="Program.Main",file="Program.cs",fullname="C:\\src\\Program.cs",line="7"}]
            """;

        var frames = DebuggerService.ParseStackFrames(response);

        Assert.Equal(2, frames.Count);
        Assert.Equal(@"C:\src\Order.cs", frames[0].FilePath);
        Assert.Equal(42, frames[0].Line);
        Assert.Equal(9, frames[0].Column);
        Assert.Equal("Program.Main", frames[1].Name);
        Assert.All(frames, f => Assert.False(f.IsExternal));
    }

    [Fact]
    public void RuntimeTransitionsAreMarkedExternal()
    {
        const string response =
            @"^done,stack=[frame={level=""0"",func=""[Native Frames]""},frame={level=""1""},frame={level=""2"",func=""App.Run"",file=""App.cs"",line=""3""}]";

        var frames = DebuggerService.ParseStackFrames(response);

        Assert.True(frames[0].IsExternal);
        Assert.True(frames[1].IsExternal, "a frame with neither name nor file has nothing to show");
        Assert.False(frames[2].IsExternal);
    }

    [Fact]
    public void ErrorResponseYieldsNoFramesRatherThanAFakeOne()
    {
        Assert.Empty(DebuggerService.ParseStackFrames(@"^error,msg=""Thread is not stopped"""));
    }

    // === MI tuple splitting ===

    [Fact]
    public void BracesInsideAValueDoNotSplitTheTuple()
    {
        // A ToString() of a dictionary or an anonymous type is the ordinary case here, and naive
        // brace counting truncates the variable list at the first one.
        const string response =
            @"^done,variables=[{name=""map"",value=""{ Count = 2 }""},{name=""n"",value=""7""}]";

        var pairs = DebuggerService.ParseNameValueList(response);

        Assert.Equal(2, pairs.Count);
        Assert.Equal("map", pairs[0].Name);
        Assert.Equal("{ Count = 2 }", pairs[0].Value);
        Assert.Equal("n", pairs[1].Name);
    }

    [Fact]
    public void EscapedQuotesAndBackslashesSurviveParsing()
    {
        const string response =
            @"^done,variables=[{name=""path"",value=""C:\\tmp\\a.txt""},{name=""text"",value=""say \""hi\""""}]";

        var pairs = DebuggerService.ParseNameValueList(response);

        Assert.Equal(@"C:\tmp\a.txt", pairs[0].Value);
        Assert.Equal(@"say ""hi""", pairs[1].Value);
    }

    [Fact]
    public void SplittingStopsAtTheEndOfTheList()
    {
        var tuples = DebuggerService.SplitMiTuples(@"{a=""1""},{b=""2""}],current-thread-id=""1""");

        Assert.Equal(2, tuples.Count);
    }

    // === Hit conditions ===

    [Theory]
    [InlineData(">= 3", 2, false)]
    [InlineData(">= 3", 3, true)]
    [InlineData("> 3", 3, false)]
    [InlineData("= 5", 5, true)]
    [InlineData("== 5", 4, false)]
    [InlineData("% 3", 6, true)]
    [InlineData("% 3", 7, false)]
    [InlineData("4", 4, true)]
    [InlineData("4", 3, false)]
    public void HitConditionsFollowTheEditorsVocabulary(string condition, int hits, bool expected) =>
        Assert.Equal(expected, PublishingDebugBackend.HitConditionMet(condition, hits));

    [Fact]
    public void AnUnparseableHitConditionStopsRatherThanSwallowingEveryHit()
    {
        // Silently never stopping is the worst possible answer to a typo in a breakpoint rule.
        Assert.True(PublishingDebugBackend.HitConditionMet("whenever", 1));
        Assert.True(PublishingDebugBackend.HitConditionMet(">= 0", 1));
    }

    // === Emulated breakpoints ===

    [Fact]
    public async Task AHitConditionResumesThroughTheEarlyHits()
    {
        var inner = new CountingBackend();
        using var backend = new PublishingDebugBackend(inner);
        await backend.AttachToProcessAsync(1234);

        await backend.SetBreakpointAsync("Order.cs", 42, hitCondition: ">= 3");
        inner.StopAt(breakpointId: 1);

        await backend.ContinueAsync();

        // Hits 1 and 2 are swallowed; the caller's continue returns on hit 3.
        Assert.Equal(3, inner.Resumes);
    }

    [Fact]
    public async Task ALogpointLogsAndKeepsGoing()
    {
        var inner = new CountingBackend { StopsRemaining = 2 };
        using var backend = new PublishingDebugBackend(inner);
        await backend.AttachToProcessAsync(1234);

        await backend.SetBreakpointAsync("Order.cs", 42, logMessage: "total={order.Total}");
        inner.StopAt(breakpointId: 1);

        await backend.ContinueAsync();
        var log = backend.DrainLog();

        Assert.NotEmpty(log);
        Assert.Contains("total=eval:order.Total", log[0]);
        Assert.Empty(backend.DrainLog());
    }

    [Fact]
    public async Task APlainBreakpointStopsImmediately()
    {
        var inner = new CountingBackend();
        using var backend = new PublishingDebugBackend(inner);
        await backend.AttachToProcessAsync(1234);

        await backend.SetBreakpointAsync("Order.cs", 42);
        inner.StopAt(breakpointId: 1);

        await backend.ContinueAsync();

        Assert.Equal(1, inner.Resumes);
    }

    [Fact]
    public async Task RemovingABreakpointForgetsItsHitCount()
    {
        var inner = new CountingBackend();
        using var backend = new PublishingDebugBackend(inner);
        await backend.AttachToProcessAsync(1234);

        await backend.SetBreakpointAsync("Order.cs", 42, hitCondition: ">= 2");
        inner.StopAt(breakpointId: 1);
        await backend.ContinueAsync();
        Assert.Equal(2, inner.Resumes);

        await backend.RemoveBreakpointAsync(1);
        await backend.SetBreakpointAsync("Order.cs", 42, hitCondition: ">= 2");
        inner.Resumes = 0;
        inner.StopAt(breakpointId: 1);

        await backend.ContinueAsync();

        // A re-set breakpoint counts from zero again, so the rule means the same thing twice.
        Assert.Equal(2, inner.Resumes);
    }

    // === Exception filters ===

    [Theory]
    [InlineData(new[] { "all" }, true, false)]
    [InlineData(new[] { "user-unhandled" }, false, true)]
    [InlineData(new[] { "userUnhandled", "all" }, true, true)]
    [InlineData(new string[0], false, false)]
    public void FilterIdsMapToWhatTheEngineAdvertises(string[] ids, bool all, bool userUnhandled)
    {
        var filters = ExceptionFilters.FromIds(ids);

        Assert.Equal(all, filters.All);
        Assert.Equal(userUnhandled, filters.UserUnhandled);
    }

    [Fact]
    public void UnknownFiltersAreIgnoredRatherThanBreakingOnEverything()
    {
        var filters = ExceptionFilters.FromIds(["always", "uncaught"]);

        Assert.Equal(ExceptionFilters.None, filters);
    }

    // === Variable handles ===

    [Fact]
    public void TheSameExpressionKeepsItsReferenceWithinAStop()
    {
        var handles = new VariableHandles();

        int first = handles.For("0|order");
        int second = handles.For("0|order");

        Assert.Equal(first, second);
        Assert.Equal("0|order", handles.Expression(first));
    }

    [Fact]
    public void ReferencesDoNotSurviveAReset()
    {
        // Handing out a handle from the previous stop reports another object's fields.
        var handles = new VariableHandles();
        int reference = handles.For("0|order");

        handles.Reset();

        Assert.Null(handles.Expression(reference));
    }

    /// <summary>A backend that stops on a chosen breakpoint every time it is resumed.</summary>
    private sealed class CountingBackend : IDebugBackend
    {
        public int Resumes;

        /// <summary>How many stops to report before running to completion; effectively
        /// unlimited by default.</summary>
        public int StopsRemaining = int.MaxValue;

        private DebuggerService.StoppedFrame? _frame;

        public DebuggerService.StoppedFrame? CurrentFrame => _frame;

        public void StopAt(int breakpointId) =>
            _frame = new DebuggerService.StoppedFrame(
                "breakpoint-hit", "Order.Total", @"C:\src\Order.cs", 42, breakpointId);

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

        public Task<string> ContinueAsync(CancellationToken cancellationToken = default)
        {
            Resumes++;
            if (--StopsRemaining <= 0)
                _frame = null;
            return Task.FromResult("continued");
        }

        public Task<string> StepInAsync(CancellationToken cancellationToken = default) => ContinueAsync(cancellationToken);
        public Task<string> StepOverAsync(CancellationToken cancellationToken = default) => ContinueAsync(cancellationToken);
        public Task<string> StepOutAsync(CancellationToken cancellationToken = default) => ContinueAsync(cancellationToken);

        public Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default) =>
            Task.FromResult($"eval:{expression}");

        public Task<string> GetLocalsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("locals");

        public Task<string> GetStackTraceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("stack");

        public Task<string> InterruptAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("interrupted");

        public Task<IReadOnlyList<StackFrameInfo>> GetStackFramesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StackFrameInfo>>([]);

        public Task<IReadOnlyList<VariableInfo>> GetVariablesAsync(int frameId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VariableInfo>>([]);

        public Task<IReadOnlyList<VariableInfo>> GetVariableChildrenAsync(
            int variablesReference, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VariableInfo>>([]);

        public Task<(bool Ok, string Value, string Error)> SetVariableAsync(
            string name, string value, int frameId = 0, CancellationToken cancellationToken = default) =>
            Task.FromResult((true, value, ""));

        public Task<string> SelectFrameAsync(int frameId, CancellationToken cancellationToken = default) =>
            Task.FromResult($"frame {frameId}");

        public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ThreadInfo>>([]);

        public Task<ExceptionDetail?> GetExceptionInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ExceptionDetail?>(null);

        public Task<string> SetExceptionFiltersAsync(
            ExceptionFilters filters, CancellationToken cancellationToken = default) =>
            Task.FromResult("ok");

        public Task<string> RunToLocationAsync(
            string filePath, int line, CancellationToken cancellationToken = default) =>
            Task.FromResult("ran to location");

        public Task<string> SetNextStatementAsync(
            string filePath, int line, CancellationToken cancellationToken = default) =>
            Task.FromResult("moved");

        public Task<IReadOnlyList<ModuleInfo>> GetModulesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModuleInfo>>([]);

        public Task<string> DetachAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("detached");

        public string GetStatus() => "status";
        public string Stop() => "stopped";
        public void Dispose() { }
    }
}
