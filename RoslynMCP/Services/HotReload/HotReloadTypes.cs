namespace RoslynMCP.Services.HotReload;

/// <summary>One assembly's worth of change, in the shape both appliers need.</summary>
/// <param name="ModuleId">The MVID of the module the delta was computed against. Identity is the
/// module, not the file: the same assembly can be loaded more than once, and applying a delta to
/// the wrong copy corrupts it.</param>
/// <param name="UpdatedMethods">MethodDef tokens of the methods the delta changes. The runtime
/// does not need them, but a debugger does: they are what a symbol store is told to re-read.</param>
/// <param name="LineMaps">Where the edit moved existing lines, per source file. Roslyn computes
/// these while emitting the delta; without them a debugger's line numbers drift by the size of
/// each edit and every later breakpoint in the file binds to the wrong statement.</param>
internal sealed record HotReloadDelta(
    Guid ModuleId,
    byte[] MetadataDelta,
    byte[] IlDelta,
    byte[] PdbDelta,
    int[] UpdatedTypes,
    int[]? UpdatedMethods = null,
    IReadOnlyList<HotReloadLineMap>? LineMaps = null);

/// <summary>How one source file's lines moved in an edit.</summary>
internal sealed record HotReloadLineMap(string FilePath, IReadOnlyList<HotReloadLineShift> Shifts);

/// <summary>One run of lines that moved: everything from <paramref name="OldLine"/> onwards shifts
/// by the difference, until the next shift says otherwise. Both are 0-based, as Roslyn reports
/// them.</summary>
internal sealed record HotReloadLineShift(int OldLine, int NewLine);

/// <summary>
/// Something Roslyn refuses to emit a delta for — most often a rude edit.
/// </summary>
/// <remarks>
/// This is the part of hot reload users actually interact with: "you changed a method signature,
/// so the process has to restart". Reporting it precisely, with the file and line, is the
/// difference between a usable feature and a mysterious one.
/// </remarks>
internal sealed record HotReloadDiagnostic(
    string Id,
    string Message,
    string Severity,
    string FilePath,
    int Line);

/// <summary>What one apply did, including the parts that failed.</summary>
internal sealed record HotReloadOutcome(
    bool Ok,
    string Summary,
    IReadOnlyList<HotReloadDiagnostic> Diagnostics,
    IReadOnlyList<string> AppliedTo,
    IReadOnlyList<string> Errors)
{
    public static HotReloadOutcome Failed(string summary) =>
        new(false, summary, [], [], []);
}

/// <summary>A live target a delta can be applied to.</summary>
internal sealed record HotReloadTargetInfo(string Name, int ProcessId, string Runtime);
