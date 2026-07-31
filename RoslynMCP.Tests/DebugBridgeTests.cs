using System.Diagnostics;
using System.Text.Json;
using RoslynMCP.Services;
using RoslynMCP.Services.Debugging;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>Debug bridge: state stores, publishing decorator, command pipe, hook context.</summary>
public class DebugBridgeTests
{
    [Fact]
    public void DebugStateStorePublishListClear()
    {
        int ownerPid = Environment.ProcessId;
        DebugStateStore.Publish(new DebugStateStore.Entry(
            ownerPid, DebugStateStore.PipeNameFor(ownerPid), "test", "proj.csproj",
            "stopped", "breakpoint-hit", "M", @"C:\x\Program.cs", 42, DateTime.UtcNow));
        try
        {
            var entry = DebugStateStore.List().FirstOrDefault(e => e.OwnerPid == ownerPid);
            Assert.NotNull(entry);
            Assert.Equal("stopped", entry!.State);
            Assert.Equal(42, entry.Line);
        }
        finally
        {
            DebugStateStore.Clear(ownerPid);
        }
        Assert.DoesNotContain(DebugStateStore.List(), e => e.OwnerPid == ownerPid);
    }

    [Fact]
    public void DebugStateStorePrunesDeadOwners()
    {
        // A PID that cannot exist keeps the entry dead; List must delete it.
        const int deadPid = int.MaxValue - 17;
        DebugStateStore.Publish(new DebugStateStore.Entry(
            deadPid, DebugStateStore.PipeNameFor(deadPid), "test", "x",
            "running", null, null, null, 0, DateTime.UtcNow));

        Assert.DoesNotContain(DebugStateStore.List(), e => e.OwnerPid == deadPid);
    }

    [Fact]
    public void EditorDebugStateStoreRoundTrip()
    {
        string solution = Path.Combine(Path.GetTempPath(), $"debug-bridge-{Guid.NewGuid():N}", "App.sln");
        EditorDebugStateStore.Write(solution, new EditorDebugStateStore.State(
            Active: true, "App", "coreclr", "stopped", "breakpoint",
            @"C:\x\Program.cs", 10, DateTime.UtcNow));
        try
        {
            var state = EditorDebugStateStore.Read(solution);
            Assert.NotNull(state);
            Assert.True(state!.Active);
            Assert.Equal("stopped", state.ExecutionState);
            Assert.Equal(10, state.Line);
        }
        finally
        {
            EditorDebugStateStore.Clear(solution);
        }
        Assert.Null(EditorDebugStateStore.Read(solution));
    }

    [Fact]
    public async Task PublishingBackendMirrorsFrameTransitions()
    {
        var fake = new FakeBackend();
        var publishing = new PublishingDebugBackend(fake);
        try
        {
            await publishing.StartTestSessionAsync("proj.csproj", null);
            var entry = DebugStateStore.List().First(e => e.OwnerPid == Environment.ProcessId);
            Assert.Equal("running", entry.State);
            Assert.Equal("test", entry.Kind);

            fake.Frame = new DebuggerService.StoppedFrame(
                "breakpoint-hit", "Main", @"C:\x\Program.cs", 7, 1);
            await publishing.ContinueAsync();
            entry = DebugStateStore.List().First(e => e.OwnerPid == Environment.ProcessId);
            Assert.Equal("stopped", entry.State);
            Assert.Equal(7, entry.Line);

            publishing.Stop();
            Assert.DoesNotContain(DebugStateStore.List(), e => e.OwnerPid == Environment.ProcessId);
        }
        finally
        {
            publishing.Dispose();
        }
    }

    [Fact]
    public async Task CommandPipeServerExecutesAgainstProvidedSession()
    {
        var fake = new FakeBackend
        {
            Frame = new DebuggerService.StoppedFrame("breakpoint-hit", "Main", @"C:\x\P.cs", 3, 1),
        };
        string pipeName = $"roslyn-sense-debug-test-{Guid.NewGuid():N}";
        using var server = new DebugCommandPipeServer(() => fake, pipeName);

        var response = await SendAsync(pipeName, new DebugPipeRequest("evaluate", Expression: "x + 1"));
        Assert.True(response.Ok);
        Assert.Equal("eval:x + 1", response.Result);

        response = await SendAsync(pipeName, new DebugPipeRequest("status"));
        Assert.True(response.Ok);
        Assert.Equal("status", response.Result);

        response = await SendAsync(pipeName, new DebugPipeRequest("bogus"));
        Assert.False(response.Ok);
    }

    [Fact]
    public void SharedBreakpointStoreRoundTrip()
    {
        string solution = Path.Combine(Path.GetTempPath(), $"bp-store-{Guid.NewGuid():N}", "App.sln");
        SharedBreakpointStore.Write(solution,
        [
            new SharedBreakpointStore.Breakpoint(@"C:\x\A.cs", 10, null),
            new SharedBreakpointStore.Breakpoint(@"C:\x\B.cs", 20, "i == 3"),
        ]);

        var read = SharedBreakpointStore.Read(solution);
        Assert.Equal(2, read.Count);
        Assert.Equal("i == 3", read.Single(b => b.Line == 20).Condition);

        SharedBreakpointStore.Write(solution, []);
        Assert.Empty(SharedBreakpointStore.Read(solution));
    }

    [Fact]
    public void NodeHookInjectsEditorDebugContextOnce()
    {
        string hookScript = Path.Combine(FindRepoRoot(), "hooks", "drain-notifications.mjs");
        Assert.True(File.Exists(hookScript), $"hook script not found at {hookScript}");

        // Unique line number distinguishes this run's state from any previous marker file.
        int line = Random.Shared.Next(100_000, 999_999);
        // Must match the hook's solution discovery (first .sln, case-insensitive sort).
        string solution = Directory.EnumerateFiles(FixturePaths.MultiSolutionDir, "*.sln")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .First();
        EditorDebugStateStore.Write(solution, new EditorDebugStateStore.State(
            Active: true, "TestSession", "coreclr", "stopped", "breakpoint",
            @"C:\x\Program.cs", line, DateTime.UtcNow));
        try
        {
            string first = RunHook(hookScript, FixturePaths.MultiSolutionDir);
            Assert.Contains($"Program.cs:{line}", first);
            Assert.Contains("debugging in the editor", first);

            // Same state again → marker suppresses the repeat.
            string second = RunHook(hookScript, FixturePaths.MultiSolutionDir);
            Assert.DoesNotContain($"Program.cs:{line}", second);
        }
        finally
        {
            EditorDebugStateStore.Clear(solution);
        }
    }

    private static string RunHook(string hookScript, string cwd)
    {
        var startInfo = new ProcessStartInfo("node", $"\"{hookScript}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo)!;
        process.StandardInput.Write(JsonSerializer.Serialize(new { cwd }));
        process.StandardInput.Close();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15000);
        return stdout;
    }

    private static async Task<DebugPipeResponse> SendAsync(string pipeName, DebugPipeRequest request)
    {
        await using var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipeName, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await RoslynMCP.Daemon.IpcProtocol.WriteMessageAsync(pipe, request, CancellationToken.None);
        var response = await RoslynMCP.Daemon.IpcProtocol.ReadMessageAsync<DebugPipeResponse>(
            pipe, CancellationToken.None);
        Assert.NotNull(response);
        return response!;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoslynMCP.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private sealed class FakeBackend : IDebugBackend
    {
        public DebuggerService.StoppedFrame? Frame;
        public DebuggerService.StoppedFrame? CurrentFrame => Frame;

        public Task<string> StartTestSessionAsync(string csprojPath, string? filter,
            IEnumerable<(string file, int line)>? initialBreakpoints = null,
            CancellationToken cancellationToken = default) => Task.FromResult("started");

        public Task<string> AttachToProcessAsync(int pid,
            IEnumerable<(string file, int line)>? initialBreakpoints = null,
            CancellationToken cancellationToken = default) => Task.FromResult("attached");

        public Task<(string Message, int? BreakpointId)> SetBreakpointAsync(string filePath, int line,
            string? condition = null, string? hitCondition = null, string? logMessage = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(($"bp {filePath}:{line}", (int?)1));

        public Task<string> RemoveBreakpointAsync(int breakpointId, CancellationToken cancellationToken = default) =>
            Task.FromResult("removed");

        /// <summary>Counts resumes so emulated hit conditions and logpoints can be observed.</summary>
        public int Continues;

        /// <summary>Frames the session reports as it is resumed, one per stop.</summary>
        public Queue<DebuggerService.StoppedFrame> StopSequence = new();

        public Task<string> ContinueAsync(CancellationToken cancellationToken = default)
        {
            Continues++;
            if (StopSequence.TryDequeue(out var next))
                Frame = next;
            return Task.FromResult("continued");
        }

        public Task<string> StepInAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("stepped");

        public Task<string> StepOverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("stepped");

        public Task<string> StepOutAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("stepped");

        public Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default) =>
            Task.FromResult($"eval:{expression}");

        public Task<string> GetLocalsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("locals");

        public Task<string> GetStackTraceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("stack");

        public Task<string> InterruptAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("interrupted");

        public IReadOnlyList<StackFrameInfo> Frames = [];
        public IReadOnlyList<VariableInfo> Variables = [];

        public Task<IReadOnlyList<StackFrameInfo>> GetStackFramesAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Frames);

        public Task<IReadOnlyList<VariableInfo>> GetVariablesAsync(
            int frameId, CancellationToken cancellationToken = default) => Task.FromResult(Variables);

        public Task<IReadOnlyList<VariableInfo>> GetVariableChildrenAsync(
            int variablesReference, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VariableInfo>>([]);

        public Task<(bool Ok, string Value, string Error)> SetVariableAsync(
            string name, string value, int frameId = 0, CancellationToken cancellationToken = default) =>
            Task.FromResult((true, value, ""));

        public Task<string> SelectFrameAsync(int frameId, CancellationToken cancellationToken = default) =>
            Task.FromResult($"frame {frameId}");

        public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ThreadInfo>>([new ThreadInfo(1, "Main", "stopped")]);

        public Task<ExceptionDetail?> GetExceptionInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ExceptionDetail?>(null);

        public Task<string> SetExceptionFiltersAsync(
            ExceptionFilters filters, CancellationToken cancellationToken = default) =>
            Task.FromResult("filters set");

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
