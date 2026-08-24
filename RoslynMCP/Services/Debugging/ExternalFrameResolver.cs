using RoslynMCP.Services.ExternalSource;

namespace RoslynMCP.Services.Debugging;

/// <summary>
/// Fills in source for stack frames that have none, so a stop inside a dependency shows the
/// executing code instead of a bare method name.
/// </summary>
/// <remarks>
/// Both engines' structured stacks pass through here, which is what puts the resolved file on
/// every surface at once — the editor's Call Stack, the DAP bridge, and the markdown the tools
/// print. Resolution is <see cref="DebugFrameSource"/>'s chain: PDB-mapped real source first,
/// the reference source next, decompilation last.
/// </remarks>
internal static class ExternalFrameResolver
{
    /// <summary>
    /// How many frames of one stop may pay for a fresh decompilation. The innermost frames are
    /// where the reader is looking; a deep framework tail fills in from the cache on later stops
    /// instead of costing a dozen decompilations up front.
    /// </summary>
    private const int DecompileBudget = 3;

    /// <summary>The same frames with source filled in where it could be resolved.</summary>
    public static async Task<IReadOnlyList<StackFrameInfo>> EnrichAsync(
        IReadOnlyList<StackFrameInfo> frames, CancellationToken ct)
    {
        StackFrameInfo[]? enriched = null;
        int budget = DecompileBudget;

        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.FilePath.Length > 0 || frame.ModulePath.Length == 0
                || frame.MethodToken == 0 || frame.IlOffset < 0)
            {
                continue;
            }

            FrameSourceResult? resolved;
            try
            {
                resolved = await DebugFrameSource.TryResolveAsync(
                    frame.ModulePath, frame.MethodToken, frame.IlOffset,
                    allowDecompile: budget > 0, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // A stack trace with an unresolved frame beats no stack trace.
                continue;
            }

            if (resolved is null)
            {
                if (budget > 0)
                    budget--;
                continue;
            }

            if (resolved.Origin == "decompiled")
                budget--;

            enriched ??= [.. frames];
            enriched[i] = frame with
            {
                FilePath = resolved.FilePath,
                Line = resolved.Line,
                Column = resolved.Column,
                SourceOrigin = resolved.Origin,
            };
        }

        return enriched ?? frames;
    }
}
