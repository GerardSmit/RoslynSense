using System.Text.Json.Nodes;
using RoslynMCP.Services;
using RoslynMCP.Services.Debugging;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Run to Cursor, Set Next Statement, modules and detach.
/// </summary>
/// <remarks>
/// All four were implemented in the ICorDebug engine and unreachable: <c>IDebugEngine</c>, the
/// contract everything above it drives, did not carry them. These tests exist mostly to keep that
/// from happening again — a capability with no route to a caller is the same as a missing one.
/// </remarks>
public class DebugNavigationTests
{
    [Fact]
    public async Task RunToCursorReachesTheBackendWithTheFileAndLine()
    {
        var backend = new RecordingBackend();

        await new PublishingDebugBackend(backend).RunToLocationAsync(@"C:\src\Order.cs", 42);

        Assert.Equal(@"C:\src\Order.cs", backend.LastFile);
        Assert.Equal(42, backend.LastLine);
    }

    [Fact]
    public async Task SetNextStatementReachesTheBackendWithTheFileAndLine()
    {
        var backend = new RecordingBackend();

        await new PublishingDebugBackend(backend).SetNextStatementAsync(@"C:\src\Order.cs", 17);

        Assert.Equal(17, backend.LastLine);
    }

    [Fact]
    public async Task ModulesCarryWhetherSymbolsWereFound()
    {
        // The whole point of the list: a module without a PDB can never bind a breakpoint,
        // however right the file and line are.
        var backend = new RecordingBackend
        {
            Modules =
            [
                new ModuleInfo("App.dll", @"C:\out\App.dll", true, @"C:\out\App.pdb", "CoreCLR"),
                new ModuleInfo("Vendor.dll", @"C:\out\Vendor.dll", false, "", "CoreCLR"),
            ],
        };

        var modules = await new PublishingDebugBackend(backend).GetModulesAsync();

        Assert.True(modules.Single(m => m.Name == "App.dll").SymbolsLoaded);
        Assert.False(modules.Single(m => m.Name == "Vendor.dll").SymbolsLoaded);
    }

    [Fact]
    public async Task DetachIsDistinctFromStopping()
    {
        // Detaching leaves the process alive; that difference is the reason the command exists.
        var backend = new RecordingBackend();
        var session = new PublishingDebugBackend(backend);

        await session.DetachAsync();

        Assert.True(backend.Detached);
        Assert.False(backend.Stopped);
    }

    [Fact]
    public async Task TheAdapterAdvertisesSetNextStatementAndRoutesItToTheBackend()
    {
        var backend = new RecordingBackend
        {
            Frame = new DebuggerService.StoppedFrame("breakpoint-hit", "Order.Total", @"C:\src\Order.cs", 42, 1),
        };

        var messages = await DapConversation.RunAsync(backend, [
            DapConversation.Request(1, "initialize"),
            DapConversation.Request(2, "gotoTargets", new JsonObject
            {
                ["source"] = new JsonObject { ["path"] = @"C:\src\Order.cs" },
                ["line"] = 30,
            }),
            DapConversation.Request(3, "goto", new JsonObject { ["targetId"] = 30 }),
        ]);

        var capabilities = messages.First(m => m["command"]?.GetValue<string>() == "initialize")["body"];
        Assert.True(capabilities!["supportsGotoTargetsRequest"]!.GetValue<bool>());

        // The source came from gotoTargets: DAP's goto carries only the target id.
        Assert.Equal(@"C:\src\Order.cs", backend.LastFile);
        Assert.Equal(30, backend.LastLine);
    }

    [Fact]
    public async Task FrameworkExceptionFiltersAreAppliedRatherThanRefused()
    {
        // This used to answer "the filters cannot be changed" while the engine underneath
        // supported exactly that.
        var engine = new RecordingEngine();
        var backend = new IcorDebugBackend();
        backend.GetType().GetField("_engine", System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance)!.SetValue(backend, engine);

        string result = await backend.SetExceptionFiltersAsync(new ExceptionFilters(All: true, UserUnhandled: false));

        Assert.True(engine.BreakOnFirstChance);
        Assert.DoesNotContain("cannot", result);
    }

    private sealed class RecordingEngine : RoslynMCP.Debugger.IDebugEngine
    {
        public bool BreakOnFirstChance;

        public System.Threading.Channels.ChannelReader<RoslynMCP.Debugger.DebugEvent> Events =>
            System.Threading.Channels.Channel.CreateUnbounded<RoslynMCP.Debugger.DebugEvent>().Reader;

        public void SetExceptionPolicy(bool breakOnFirstChance) => BreakOnFirstChance = breakOnFirstChance;

        public void Attach(int pid, IEnumerable<RoslynMCP.Debugger.BreakpointSpec> breakpoints,
            RoslynMCP.Debugger.DebugRuntime runtime) { }
        public void Launch(string executable, IReadOnlyList<string> arguments,
            IEnumerable<RoslynMCP.Debugger.BreakpointSpec> breakpoints,
            IReadOnlyDictionary<string, string>? environment, string? workingDirectory,
            RoslynMCP.Debugger.DebugRuntime runtime) { }
        public void AddBreakpoint(RoslynMCP.Debugger.BreakpointSpec spec) { }
        public bool RemoveBreakpoint(string filePath, int line) => true;
        public void Continue() { }
        public void Pause() { }
        public void Step(RoslynMCP.Debugger.StepKind kind) { }
        public Task<List<RoslynMCP.Debugger.StackFrame>> StackTraceAsync() => Task.FromResult(new List<RoslynMCP.Debugger.StackFrame>());
        public Task<List<RoslynMCP.Debugger.DebugVariable>> VariablesAsync(uint frameIndex) =>
            Task.FromResult(new List<RoslynMCP.Debugger.DebugVariable>());
        public Task<(bool Ok, string Value, string Error)> EvaluateAsync(uint frameIndex, string expression) =>
            Task.FromResult((true, "", ""));
        public Task<(bool Ok, RoslynMCP.Debugger.DebugVariable? Variable, string Error)> SetVariableAsync(
            uint frameIndex, string name, string value) =>
            Task.FromResult<(bool, RoslynMCP.Debugger.DebugVariable?, string)>((true, null, ""));
        public Task<(bool Ok, string Error)> ApplyDeltaAsync(string assemblyName, byte[] metadata, byte[] il, byte[] pdb) =>
            Task.FromResult((true, ""));
        public Task<RoslynMCP.Debugger.RunToLocationResponse> RunToLocationAsync(
            RoslynMCP.Debugger.RunToLocationRequest request) =>
            Task.FromResult(new RoslynMCP.Debugger.RunToLocationResponse { Ok = true });
        public Task<RoslynMCP.Debugger.SetNextStatementResponse> SetNextStatementAsync(
            RoslynMCP.Debugger.SetNextStatementRequest request) =>
            Task.FromResult(new RoslynMCP.Debugger.SetNextStatementResponse { Ok = true });
        public Task<List<RoslynMCP.Debugger.DebugModule>> ModulesAsync() =>
            Task.FromResult(new List<RoslynMCP.Debugger.DebugModule>());
        public Task<(bool Ok, string Error)> DetachAsync() => Task.FromResult((true, ""));
        public void Terminate() { }
        public void Dispose() { }
    }
}
