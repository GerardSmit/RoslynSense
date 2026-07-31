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

    /// <summary>
    /// Starts the debuggee suspended and attaches before any of its code runs.
    /// </summary>
    /// <remarks>
    /// Attaching to an already-started process cannot catch anything in Main or a static
    /// constructor, which is exactly where a startup bug lives — so F5 has to launch, not attach.
    /// </remarks>
    void Launch(
        string executable,
        IReadOnlyList<string> arguments,
        IEnumerable<BreakpointSpec> breakpoints,
        IReadOnlyDictionary<string, string>? environment,
        string? workingDirectory,
        DebugRuntime runtime);
    void AddBreakpoint(BreakpointSpec spec);
    bool RemoveBreakpoint(string filePath, int line);
    void Continue();
    void Step(StepKind kind);

    Task<List<StackFrame>> StackTraceAsync();
    Task<List<DebugVariable>> VariablesAsync(uint frameIndex);
    Task<(bool Ok, string Value, string Error)> EvaluateAsync(uint frameIndex, string expression);
    Task<(bool Ok, DebugVariable? Variable, string Error)> SetVariableAsync(
        uint frameIndex, string name, string value);

    /// <summary>
    /// Applies one Edit-and-Continue delta to a loaded module, by simple assembly name.
    /// </summary>
    /// <remarks>
    /// This is how hot reload reaches .NET Framework. CoreCLR has an in-process updater and needs
    /// no debugger at all; the desktop runtime has only <c>ICorDebugModule2::ApplyChanges</c>, so
    /// the app must already be under this engine for an edit to land.
    /// </remarks>
    Task<(bool Ok, string Error)> ApplyDeltaAsync(string assemblyName, byte[] metadata, byte[] il);

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

    /// <summary>
    /// The engine for a program about to be launched, chosen from the executable's own bitness.
    /// </summary>
    /// <remarks>
    /// A .NET Framework build is frequently x86 (AnyCPU with Prefer32Bit, or an explicit x86
    /// target) while this host is x64, and ICorDebug cannot cross that boundary — so the choice
    /// has to be made from the PE header before the process exists to ask.
    /// </remarks>
    public static IDebugEngine ForExecutable(string executablePath, uint sessionId = 1)
    {
        var targetArch = ProcessArch.OfExecutable(executablePath);
        if (targetArch == ProcessArch.Host)
            return new InProcessDebugEngine(sessionId);

        var worker = FindWorker(targetArch);
        if (worker is null)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(executablePath)}' is {Describe(targetArch)} but this host is " +
                $"{Describe(ProcessArch.Host)}, and ICorDebug cannot debug across architectures. " +
                $"The matching debug worker was not found (expected under " +
                $"'{WorkerDirectory}/{Suffix(targetArch)}'). Either install it, or build the " +
                $"project as {Describe(ProcessArch.Host)}.");
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
