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

    /// <summary>Break All: suspends the target and establishes a stop context, so what follows can
    /// read a stack, evaluate, and step — a suspend on its own gives none of that.</summary>
    void Pause();

    void Step(StepKind kind);

    /// <summary>
    /// The call stack of one thread, top-first.
    /// </summary>
    /// <param name="threadId">Which thread to walk; <c>0</c> means whichever one the stop landed
    /// on. Naming another suspended thread is the only way to see what the rest of the process was
    /// doing — on a server, that is every other in-flight request.</param>
    Task<List<StackFrame>> StackTraceAsync(int threadId = 0);

    /// <summary>Every managed thread in the target, and which one the stop landed on.</summary>
    Task<List<DebugThread>> ThreadsAsync();

    Task<List<DebugVariable>> VariablesAsync(uint frameIndex);

    /// <summary>
    /// The children of one value: an object's fields, an array's elements, or the members of the
    /// debugger view its type asks to be shown through.
    /// </summary>
    /// <param name="path">The expression the value was reached by, taken from a variable's
    /// <c>VariablesReference</c>. Empty lists the frame's own arguments and locals.</param>
    Task<List<DebugVariable>> ExpandAsync(uint frameIndex, string path);

    /// <summary>
    /// Sets which <c>System.Diagnostics</c> debugger attributes the engine honours — display
    /// strings, type proxies, browsable states, and Just My Code stepping.
    /// </summary>
    /// <remarks>
    /// On the engine rather than on each call because it is a session-wide policy the user sets
    /// once in configuration, and because a worker-hosted session has to be told separately: the
    /// settings live in the host's process and the engine runs in another one.
    /// </remarks>
    void SetDisplayOptions(DebugDisplayOptions options);

    /// <summary>
    /// Hands the engine decompiled source for one type in a module that has no PDB, so that the
    /// engine can locate, step and bind inside it the way it does with real symbols.
    /// </summary>
    /// <remarks>
    /// Pushed from the host because the decompiler lives there and the engine may be another
    /// process. Additive and idempotent: a type sent twice replaces itself, and a module accretes
    /// its types as the session visits them.
    /// </remarks>
    void AddDecompiledSymbols(string modulePath, DecompiledSymbolMap map);
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
    /// <param name="pdb">The PDB delta. The runtime never sees it — <c>ApplyChanges</c> takes
    /// metadata and IL only — but the debugger's own symbol reader has to be updated with it or
    /// every line number in the edited method is stale from that point on.</param>
    /// <param name="symbolMap">A serialized <see cref="EncSymbolMap"/>: which methods the delta
    /// describes and how the edit moved the lines around them. The PDB delta alone fixes only the
    /// methods that changed, so without this the rest of each edited file drifts further out of
    /// step with every edit.</param>
    Task<(bool Ok, string Error)> ApplyDeltaAsync(
        string assemblyName, byte[] metadata, byte[] il, byte[] pdb, string? symbolMap = null);

    /// <summary>Runs to a source location without leaving a breakpoint behind — "Run to Cursor".</summary>
    Task<RunToLocationResponse> RunToLocationAsync(RunToLocationRequest request);

    /// <summary>
    /// Moves the instruction pointer within the current frame — "Set Next Statement".
    /// </summary>
    /// <remarks>
    /// The one debugger operation that rewrites history rather than observing it: it re-runs a
    /// block after changing a variable, or skips a call that would fail. The runtime refuses moves
    /// it cannot make safely (across frames, into a different scope), which the response reports.
    /// </remarks>
    Task<SetNextStatementResponse> SetNextStatementAsync(SetNextStatementRequest request);

    /// <summary>Loaded modules and whether each has symbols — the actionable answer to "why does
    /// my breakpoint never bind".</summary>
    Task<List<DebugModule>> ModulesAsync();

    /// <summary>Detaches, leaving the target running. The alternative to killing an app that was
    /// only being inspected.</summary>
    Task<(bool Ok, string Error)> DetachAsync();

    /// <summary>
    /// Replaces the policy deciding which exceptions suspend the target.
    /// </summary>
    /// <remarks>
    /// Applied inside the engine rather than by resuming through unwanted stops: a framework that
    /// throws internally on a hot path makes "break on all exceptions" unusable otherwise, and a
    /// type filter is only cheap if the exception it rejects never becomes a stop.
    /// </remarks>
    void SetExceptionPolicy(ExceptionPolicy policy);

    /// <summary>
    /// Ends the session by letting the debuggee shut itself down, terminating it only if that
    /// runs past <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// The difference the caller is buying: <see cref="Terminate"/> kills the process outright,
    /// so hosted services never see <c>StopAsync</c> and <c>finally</c> blocks never run. Only a
    /// launched debuggee can be asked — an attached one should be detached from instead.
    /// </remarks>
    Task<(bool Graceful, string Error)> ShutdownAsync(TimeSpan timeout);

    void Terminate();
}

/// <summary>
/// Creates the engine appropriate to a target: a worker of the target's bitness whenever one is
/// installed, in-process only as the fallback for a bitness-matched target without workers.
/// </summary>
/// <remarks>
/// A worker is preferred even when the bitness matches, for two reasons. Edit-and-Continue:
/// <c>ApplyChanges</c> faults rather than fails on a bad stop shape, and only a disposable
/// worker may take that risk — the in-process engine refuses every apply by design, which made
/// hot reload structurally impossible for bitness-matched targets (the x64 IIS Express default
/// among them). And uniformity: one engine path exercised everywhere instead of two that
/// diverge only in the environments tests do not cover.
/// </remarks>
public static class DebugEngineFactory
{
    /// <summary>
    /// Where the per-architecture workers are published, relative to the host assembly.
    /// </summary>
    private const string WorkerDirectory = "workers";

    public static IDebugEngine ForProcess(int pid, uint sessionId = 1)
    {
        var targetArch = ProcessArch.OfProcess(pid);
        var worker = FindWorker(targetArch);
        if (worker is not null)
            return new WorkerDebugEngine(worker, sessionId);

        if (targetArch == ProcessArch.Host)
            return new InProcessDebugEngine(sessionId);

        throw new InvalidOperationException(
            $"Process {pid} is {Describe(targetArch)} but this host is {Describe(ProcessArch.Host)}, " +
            "and ICorDebug cannot attach across architectures. The matching debug worker was not " +
            $"found (expected under '{WorkerDirectory}/{Suffix(targetArch)}'). Either install it or " +
            $"run the target as {Describe(ProcessArch.Host)} — for IIS Express, use the " +
            $"{Describe(ProcessArch.Host)} iisexpress.exe.");
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
        var worker = FindWorker(targetArch);
        if (worker is not null)
            return new WorkerDebugEngine(worker, sessionId);

        if (targetArch == ProcessArch.Host)
            return new InProcessDebugEngine(sessionId);

        throw new InvalidOperationException(
            $"'{Path.GetFileName(executablePath)}' is {Describe(targetArch)} but this host is " +
            $"{Describe(ProcessArch.Host)}, and ICorDebug cannot debug across architectures. " +
            $"The matching debug worker was not found (expected under " +
            $"'{WorkerDirectory}/{Suffix(targetArch)}'). Either install it, or build the " +
            $"project as {Describe(ProcessArch.Host)}.");
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

    /// <summary>The <c>workers/</c> subfolder an architecture's worker is published to.</summary>
    public static string Suffix(DebugArch arch) => arch switch
    {
        DebugArch.X86 => "x86",
        DebugArch.Arm64 => "arm64",
        _ => "x64",
    };

    /// <summary>How an architecture should read in a message to the user.</summary>
    public static string Describe(DebugArch arch) => arch switch
    {
        DebugArch.X86 => "32-bit x86",
        DebugArch.Arm64 => "64-bit arm64",
        _ => "64-bit x64",
    };
}
