using System.Threading.Channels;

namespace RoslynMCP.Debugger;

/// <summary>
/// The engine surface a debug session is driven through, whether it runs in this process or in a
/// bitness-matched worker.
/// </summary>
/// <remarks>
/// ICorDebug cannot attach across x86/x64, so a 32-bit target has to be debugged from a 32-bit
/// process. Both paths run the same <see cref="DebugSession"/> code; only the transport differs.
/// </remarks>
public interface IDebugEngine : IDisposable
{
    ChannelReader<DebugEvent> Events { get; }

    void Attach(int pid, IEnumerable<BreakpointSpec> breakpoints, DebugRuntime runtime);
    void AddBreakpoint(BreakpointSpec spec);
    bool RemoveBreakpoint(string filePath, int line);
    void Continue();
    void Step(StepKind kind);

    Task<List<StackFrame>> StackTraceAsync();
    Task<List<DebugVariable>> VariablesAsync(uint frameIndex);
    Task<(bool Ok, string Value, string Error)> EvaluateAsync(uint frameIndex, string expression);
    Task<(bool Ok, DebugVariable? Variable, string Error)> SetVariableAsync(
        uint frameIndex, string name, string value);

    void Terminate();
}

/// <summary>
/// Creates the engine appropriate to a target: in-process when the bitness matches, otherwise a
/// worker of the target's bitness.
/// </summary>
public static class DebugEngineFactory
{
    /// <summary>
    /// Where the per-architecture workers are published, relative to the host assembly.
    /// </summary>
    private const string WorkerDirectory = "workers";

    public static IDebugEngine ForProcess(int pid, uint sessionId = 1)
    {
        var targetArch = ProcessArch.OfProcess(pid);
        if (targetArch == ProcessArch.Host)
            return new InProcessDebugEngine(sessionId);

        var worker = FindWorker(targetArch);
        if (worker is null)
        {
            throw new InvalidOperationException(
                $"Process {pid} is {Describe(targetArch)} but this host is {Describe(ProcessArch.Host)}, " +
                "and ICorDebug cannot attach across architectures. The matching debug worker was not " +
                $"found (expected under '{WorkerDirectory}/{Suffix(targetArch)}'). Either install it or " +
                $"run the target as {Describe(ProcessArch.Host)} — for IIS Express, use the " +
                $"{Describe(ProcessArch.Host)} iisexpress.exe.");
        }

        return new WorkerDebugEngine(worker, sessionId);
    }

    /// <summary>Locates the published worker for an architecture, or null when absent.</summary>
    public static string? FindWorker(DebugArch arch)
    {
        var name = OperatingSystem.IsWindows() ? "RoslynMCP.DebugWorker.exe" : "RoslynMCP.DebugWorker";

        foreach (var root in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) })
        {
            if (string.IsNullOrEmpty(root))
                continue;

            var candidate = Path.Combine(root, WorkerDirectory, Suffix(arch), name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string Suffix(DebugArch arch) => arch == DebugArch.X86 ? "x86" : "x64";

    private static string Describe(DebugArch arch) => arch == DebugArch.X86 ? "32-bit" : "64-bit";
}
