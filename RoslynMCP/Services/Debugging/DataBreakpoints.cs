namespace RoslynMCP.Services.Debugging;

/// <summary>
/// A watch on a value: break when the thing this expression names stops being what it was.
/// </summary>
/// <param name="DataId">The opaque handle DAP hands back on <c>setDataBreakpoints</c>. It is the
/// expression plus the frame it was captured from, so two identically-named locals in different
/// frames are distinct watches.</param>
/// <param name="Expression">What to evaluate at every step.</param>
/// <param name="AccessType">Always <c>write</c>. Reads leave no trace in a value, so they cannot
/// be detected by comparing one — see <see cref="DataBreakpointWatcher"/>.</param>
/// <param name="Condition">An expression that must also be true for the change to surface.</param>
/// <param name="HitCondition">A hit-count rule in the same vocabulary as a source breakpoint's.</param>
internal sealed record DataBreakpointSpec(
    string DataId,
    string Expression,
    string AccessType = "write",
    string? Condition = null,
    string? HitCondition = null);

/// <summary>One module loaded in the debuggee.</summary>
/// <param name="SymbolsLoaded">Whether a PDB was found. Without one, breakpoints in this module
/// never bind — which is the question this record exists to answer.</param>
/// <param name="SymbolPath">The file the symbols were read from. Empty when they never were a
/// file — embedded in the module, or handed over by the runtime — as well as when there are
/// none.</param>
/// <param name="SymbolStatus">One word for the outcome: <c>loaded</c>, <c>excluded</c>,
/// <c>not found</c>, <c>rejected</c>, <c>not probed</c>. Empty from engines that do not say.</param>
/// <param name="SymbolOrigin">Which kind of symbols answered: <c>portable pdb</c>,
/// <c>embedded pdb</c>, <c>windows pdb</c>, <c>supplied at run time</c>.</param>
/// <param name="SymbolDetail">Why, in a sentence, when <paramref name="SymbolStatus"/> is not
/// <c>loaded</c>. This is the difference between "rebuild and it will work" and "this module
/// was never going to have symbols".</param>
internal sealed record ModuleInfo(
    string Name,
    string Path,
    bool SymbolsLoaded,
    string SymbolPath,
    string Runtime,
    string SymbolStatus = "",
    string SymbolOrigin = "",
    string SymbolDetail = "");

/// <summary>Whether a requested watch could be armed, and why not when it could not.</summary>
internal sealed record DataBreakpointStatus(string DataId, bool Verified, string Message);

/// <summary>A change that stopped the target, with both sides of it for the stop message.</summary>
internal sealed record DataBreakpointHit(
    string DataId,
    string Expression,
    string OldValue,
    string NewValue)
{
    public string Description => $"{Expression}: {OldValue} → {NewValue}";
}

/// <summary>Why a watched resume ended, so the caller can say something true about it.</summary>
internal enum DataWatchOutcome
{
    /// <summary>A watched value changed.</summary>
    Changed,

    /// <summary>Something else stopped first — a breakpoint, a step completing, an exception.</summary>
    OtherStop,

    /// <summary>The target ran to completion.</summary>
    Exited,

    /// <summary>The step budget ran out before anything happened.</summary>
    BudgetExhausted,
}

/// <summary>
/// Encodes and decodes the <c>dataId</c> DAP passes around.
/// </summary>
/// <remarks>
/// DAP requires the client to be able to persist a <c>dataId</c> across sessions, so it must be
/// derived from the expression rather than from a counter.
/// </remarks>
internal static class DataBreakpointId
{
    public static string For(string expression, int frameId) => $"{frameId}:{expression}";

    public static string ExpressionOf(string dataId)
    {
        int separator = dataId.IndexOf(':');
        return separator < 0 ? dataId : dataId[(separator + 1)..];
    }
}
