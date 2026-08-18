namespace RoslynMCP.Services.Debugging;

/// <summary>What a <see cref="DebugNotice"/> is telling the client.</summary>
internal enum DebugNoticeKind
{
    /// <summary>The debuggee wrote to its console.</summary>
    Output,

    /// <summary>The engine talking about itself: missing symbols, a bind it could not do, an edit
    /// the runtime refused.</summary>
    Diagnostic,

    /// <summary>A module loaded into the debuggee.</summary>
    Module,

    /// <summary>A pending breakpoint bound to real code, possibly on a different line than asked.</summary>
    BreakpointBound,

    /// <summary>A bound breakpoint went back to pending because its module unloaded.</summary>
    BreakpointUnbound,

    /// <summary>The debuggee suspended. <see cref="DebugNotice.Message"/> carries the DAP reason.</summary>
    Stopped,

    /// <summary>A resuming command started. Raised by the publishing decorator rather than the
    /// engine, so an adapter can announce a resume some other client of the shared backend
    /// issued — its own resumes it narrates itself.</summary>
    Resumed,

    /// <summary>The debuggee ended.</summary>
    Exited,
}

/// <summary>Something the engine reported that is not a stop.</summary>
internal sealed record DebugNotice(
    DebugNoticeKind Kind,
    string Message,
    string FilePath = "",
    int Line = 0,
    int BreakpointId = 0);

/// <summary>
/// A backend that reports what happens between stops.
/// </summary>
/// <remarks>
/// The engine has always emitted these — every module load, every "no symbols for X", every
/// "bound at line N" — and everything above it dropped them on the floor, so a breakpoint that
/// never bound looked exactly like a breakpoint the debuggee never reached. Separate from
/// <see cref="IDebugBackend"/> because only the ICorDebug engine produces them; netcoredbg
/// reports the same things over DAP itself.
/// </remarks>
internal interface IDebugNoticeSource
{
    event Action<DebugNotice>? Notice;

    /// <summary>
    /// Counts stops, so a listener can tell a stop it has already announced from a new one.
    /// </summary>
    /// <remarks>
    /// A stop reaches a listener twice — once as the result of the command that resumed into it,
    /// once as a notice — and which arrives first is a scheduling race. Numbering them is what
    /// makes "report this stop once" expressible at all.
    /// </remarks>
    long StopSequence { get; }
}
