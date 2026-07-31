namespace RoslynMCP.Services.HotReload;

/// <summary>One assembly's worth of change, in the shape both appliers need.</summary>
/// <param name="ModuleId">The MVID of the module the delta was computed against. Identity is the
/// module, not the file: the same assembly can be loaded more than once, and applying a delta to
/// the wrong copy corrupts it.</param>
internal sealed record HotReloadDelta(
    Guid ModuleId,
    byte[] MetadataDelta,
    byte[] IlDelta,
    byte[] PdbDelta,
    int[] UpdatedTypes);

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
