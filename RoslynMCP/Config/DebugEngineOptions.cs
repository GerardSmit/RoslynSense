namespace RoslynMCP.Config;

/// <summary>
/// Which engine debugs a CoreCLR target.
/// </summary>
/// <remarks>
/// .NET Framework has no choice to make — ICorDebug is the only thing that can attach to it — so
/// this names the case where two engines can both do the job and they differ in what they can do.
/// </remarks>
public enum CoreClrDebugEngine
{
    /// <summary>
    /// The external MI debugger the tool used for CoreCLR before it had an engine of its own.
    /// Runs everywhere the tool does, which is why a host other than Windows still gets it.
    /// </summary>
    NetCoreDbg,

    /// <summary>
    /// The tool's own ICorDebug engine, the one .NET Framework already uses and the one a .NET
    /// target gets on Windows unless told otherwise. Windows only.
    /// </summary>
    IcorDebug,
}

/// <summary>
/// The engine choice in force for this process, and the one place a new debug session reads it
/// from.
/// </summary>
/// <remarks>
/// <para>
/// Static for the reason <see cref="DebuggerViewOptions"/> is: the daemon is shared and a session
/// is created deep inside a tool call with no settings in hand.
/// </para>
/// <para>
/// Unlike the view options this is read once, when the session is created, and never pushed into a
/// running one — an engine cannot be swapped under a live debuggee. Changing it takes effect the
/// next time debugging starts, which is why nothing here has an <c>Apply</c> counterpart.
/// </para>
/// </remarks>
public static class DebugEngineOptions
{
    /// <summary>The engine the next CoreCLR session will be given.</summary>
    public static CoreClrDebugEngine CoreClr { get; set; } = DefaultFor(OperatingSystem.IsWindows());

    /// <summary>
    /// The engine a host gets when nothing names one: the tool's own on Windows, where it can run,
    /// and netcoredbg everywhere else.
    /// </summary>
    /// <remarks>
    /// The tool's own engine is the default because it is the one that can hot reload a debugged
    /// .NET process: the runtime refuses the in-process updater while any debugger is attached,
    /// and netcoredbg has no path of its own for applying an edit. It also brings Just My Code,
    /// return values after a step, and breakpoints that bind against binaries built elsewhere.
    /// </remarks>
    public static CoreClrDebugEngine DefaultFor(bool onWindows) =>
        onWindows ? CoreClrDebugEngine.IcorDebug : CoreClrDebugEngine.NetCoreDbg;

    /// <summary>The configuration value and the environment variable that name each engine.</summary>
    private const string NetCoreDbgName = "netcoredbg";
    private const string IcorDebugName = "icordebug";

    /// <summary>The environment override, matching the <c>ROSLYNMCP_</c> family of server flags.</summary>
    public const string EnvironmentVariable = "ROSLYNMCP_CORECLR_ENGINE";

    /// <summary>Where this lives in <c>roslynsense.json</c>, for the warnings that name it.</summary>
    public const string ConfigKey = "debugger.coreClrEngine";

    /// <summary>
    /// Reads an engine name, or null when the text names no engine.
    /// </summary>
    /// <remarks>
    /// Null rather than a default so the caller can tell "nothing was written" from "something
    /// unreadable was written" — the first is the normal case and the second deserves a warning.
    /// </remarks>
    public static CoreClrDebugEngine? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        NetCoreDbgName => CoreClrDebugEngine.NetCoreDbg,
        IcorDebugName => CoreClrDebugEngine.IcorDebug,
        _ => null,
    };

    /// <summary>The name this engine is configured under.</summary>
    public static string NameOf(CoreClrDebugEngine engine) =>
        engine == CoreClrDebugEngine.IcorDebug ? IcorDebugName : NetCoreDbgName;

    /// <summary>
    /// Resolves the engine from configuration with the environment over it — the order every other
    /// switch uses — defaulting to the one the host can run (<see cref="DefaultFor"/>).
    /// </summary>
    public static CoreClrDebugEngine Resolve(DebuggerConfig? config, List<string> warnings) =>
        Resolve(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            config?.CoreClrEngine,
            OperatingSystem.IsWindows(),
            warnings);

    /// <summary>
    /// The resolution itself, with the environment and the platform passed in so both the
    /// precedence and the platform refusal can be exercised on any host.
    /// </summary>
    internal static CoreClrDebugEngine Resolve(
        string? environment, string? configured, bool onWindows, List<string> warnings)
    {
        var chosen = DefaultFor(onWindows);

        // Named so a refusal below can point at the setting the user actually wrote rather than
        // at whichever one the message was drafted against.
        var from = ConfigKey;

        // Configuration first, then environment over it — the order every other switch uses.
        foreach (var (source, text) in new[]
                 {
                     (ConfigKey, configured),
                     (EnvironmentVariable, environment),
                 })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (Parse(text) is { } parsed)
            {
                chosen = parsed;
                from = source;
                continue;
            }

            warnings.Add(
                $"{source}: '{text}' is not a debug engine. Expected '{NetCoreDbgName}' or " +
                $"'{IcorDebugName}'; using '{NameOf(chosen)}'.");
        }

        if (chosen == CoreClrDebugEngine.IcorDebug && !onWindows)
        {
            // Refused rather than attempted: the engine's CoreCLR attach throws
            // PlatformNotSupportedException off Windows, which would surface as debugging being
            // broken rather than as a setting that does not apply here.
            warnings.Add(
                $"{from}: '{IcorDebugName}' is a Windows-only engine; using '{NetCoreDbgName}'.");
            return CoreClrDebugEngine.NetCoreDbg;
        }

        return chosen;
    }
}
