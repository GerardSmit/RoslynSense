using System.Reflection;
using System.Threading.Channels;
using RoslynMCP.Debugger;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public sealed class IcorDebugStopSignalTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task ContinueAndInterruptBothObserveTheSamePause()
    {
        await using var session = new TestSession();
        Task<string> continuing = session.Backend.ContinueAsync(session.Cancellation.Token);
        Task<string> interrupting = session.Backend.InterruptAsync(session.Cancellation.Token);
        Assert.Equal(1, session.Engine.ContinueCalls);
        Assert.Equal(1, session.Engine.PauseCalls);

        session.Engine.Stop(DebugEventKind.Paused, line: 42);

        string[] results = await Task.WhenAll(continuing, interrupting).WaitAsync(TestTimeout);
        Assert.All(results, result => Assert.Contains("Program.cs:42", result));
        Assert.Equal(results[0], results[1]);
        Assert.Equal(1, session.Backend.StopSequence);
    }

    [Fact]
    public async Task ResumingAfterAStopWaitsForANewStop()
    {
        await using var session = new TestSession();
        Task<string> first = session.Backend.ContinueAsync(session.Cancellation.Token);
        session.Engine.Stop(DebugEventKind.Breakpoint, line: 42);
        Assert.Contains("Program.cs:42", await first.WaitAsync(TestTimeout));

        Task<string> second = session.Backend.ContinueAsync(session.Cancellation.Token);
        Assert.Equal(2, session.Engine.ContinueCalls);
        Assert.False(second.IsCompleted, "The new resume consumed the previous stop.");
        Assert.Null(session.Backend.CurrentFrame);

        session.Engine.Stop(DebugEventKind.Breakpoint, line: 57);
        Assert.Contains("Program.cs:57", await second.WaitAsync(TestTimeout));
        Assert.Equal(2, session.Backend.StopSequence);
    }

    [Fact]
    public async Task ProcessExitReleasesEveryPendingWaiter()
    {
        await using var session = new TestSession();
        Task<string> continuing = session.Backend.ContinueAsync(session.Cancellation.Token);
        Task<string> interrupting = session.Backend.InterruptAsync(session.Cancellation.Token);

        session.Engine.Exit();

        string[] results = await Task.WhenAll(continuing, interrupting).WaitAsync(TestTimeout);
        Assert.All(results, result => Assert.Equal("The process exited.", result));
        Assert.Null(session.Backend.CurrentFrame);
        Assert.Equal("The process has exited.", await session.Backend.ContinueAsync(session.Cancellation.Token));
        Assert.Equal(1, session.Engine.ContinueCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingOneCallerDoesNotCancelTheOther(bool cancelContinue)
    {
        await using var session = new TestSession();
        using var cancelled = new CancellationTokenSource();
        Task<string> continuing = session.Backend.ContinueAsync(
            cancelContinue ? cancelled.Token : session.Cancellation.Token);
        Task<string> interrupting = session.Backend.InterruptAsync(
            cancelContinue ? session.Cancellation.Token : cancelled.Token);
        Task<string> cancelledCall = cancelContinue ? continuing : interrupting;
        Task<string> remainingCall = cancelContinue ? interrupting : continuing;

        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelledCall.WaitAsync(TestTimeout));
        Assert.False(remainingCall.IsCompleted);

        session.Engine.Stop(DebugEventKind.Paused, line: 42);
        Assert.Contains("Program.cs:42", await remainingCall.WaitAsync(TestTimeout));
        Assert.Equal(1, session.Backend.StopSequence);
    }

    private sealed class TestSession : IAsyncDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly Task _pump;

        public IcorDebugBackend Backend { get; } = new();
        public EventEngine Engine { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();

        public TestSession()
        {
            // Replace only the native engine: the real backend consumes its event channel and
            // handles public continue/pause requests exactly as it does for a worker process.
            typeof(IcorDebugBackend).GetField("_engine", PrivateInstance)!.SetValue(Backend, Engine);
            typeof(IcorDebugBackend).GetField("_state", PrivateInstance)!
                .SetValue(Backend, DebuggerService.DebugState.Running);
            typeof(IcorDebugBackend).GetMethod("StartPump", PrivateInstance)!.Invoke(Backend, null);
            _pump = (Task)typeof(IcorDebugBackend).GetField("_pump", PrivateInstance)!.GetValue(Backend)!;
        }

        public async ValueTask DisposeAsync()
        {
            Cancellation.Cancel();
            Engine.Dispose();
            await _pump.WaitAsync(TestTimeout);
            Backend.Dispose();
            Cancellation.Dispose();
        }
    }

    private sealed class EventEngine : IDebugEngine
    {
        private readonly Channel<DebugEvent> _events = Channel.CreateUnbounded<DebugEvent>();
        public ChannelReader<DebugEvent> Events => _events.Reader;
        public int ContinueCalls { get; private set; }
        public int PauseCalls { get; private set; }

        public void Continue() => ContinueCalls++;
        public void Pause() => PauseCalls++;
        public void Stop(DebugEventKind kind, uint line) => Assert.True(_events.Writer.TryWrite(new DebugEvent
        {
            Kind = kind,
            FilePath = Path.Combine(Path.GetTempPath(), "Program.cs"),
            MethodName = "Program.Main",
            Line = line,
            ThreadId = 1,
        }));
        public void Exit() => Assert.True(_events.Writer.TryWrite(new DebugEvent { Kind = DebugEventKind.Exited }));
        public void Dispose() => _events.Writer.TryComplete();

        public void Attach(int pid, IEnumerable<BreakpointSpec> breakpoints, RoslynMCP.Debugger.DebugRuntime runtime) => throw new NotSupportedException();
        public void Launch(string executable, IReadOnlyList<string> arguments, IEnumerable<BreakpointSpec> breakpoints,
            IReadOnlyDictionary<string, string>? environment, string? workingDirectory, RoslynMCP.Debugger.DebugRuntime runtime) => throw new NotSupportedException();
        public void AddBreakpoint(BreakpointSpec spec) => throw new NotSupportedException();
        public bool RemoveBreakpoint(string filePath, int line) => throw new NotSupportedException();
        public void Step(StepKind kind) => throw new NotSupportedException();
        public Task<List<StackFrame>> StackTraceAsync(int threadId = 0) => throw new NotSupportedException();
        public Task<List<DebugThread>> ThreadsAsync() => throw new NotSupportedException();
        public Task<List<DebugVariable>> VariablesAsync(uint frameIndex) => throw new NotSupportedException();
        public Task<List<DebugVariable>> ExpandAsync(uint frameIndex, string path) => throw new NotSupportedException();
        public void SetDisplayOptions(DebugDisplayOptions options) { }
        public void AddDecompiledSymbols(string modulePath, DecompiledSymbolMap map) => throw new NotSupportedException();
        public Task<(bool Ok, string Detail)> InjectAgentAsync(string assemblyPath, string typeName, string methodName, string? argument) => throw new NotSupportedException();
        public Task<(bool Ok, string Value, string Error)> EvaluateAsync(uint frameIndex, string expression) => throw new NotSupportedException();
        public Task<(bool Ok, DebugVariable? Variable, string Error)> SetVariableAsync(uint frameIndex, string name, string value) => throw new NotSupportedException();
        public Task<(bool Ok, string Error)> ApplyDeltaAsync(string assemblyName, byte[] metadata, byte[] il, byte[] pdb, string? symbolMap = null) => throw new NotSupportedException();
        public Task<RunToLocationResponse> RunToLocationAsync(RunToLocationRequest request) => throw new NotSupportedException();
        public Task<SetNextStatementResponse> SetNextStatementAsync(SetNextStatementRequest request) => throw new NotSupportedException();
        public Task<List<DebugModule>> ModulesAsync() => throw new NotSupportedException();
        public Task<(bool Ok, string Error)> DetachAsync() => throw new NotSupportedException();
        public void SetExceptionPolicy(ExceptionPolicy policy) => throw new NotSupportedException();
        public Task<(bool Graceful, string Error)> ShutdownAsync(TimeSpan timeout) => throw new NotSupportedException();
        public void Terminate() => throw new NotSupportedException();
    }
}
