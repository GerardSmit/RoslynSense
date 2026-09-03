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
    /// <param name="decompiled">Told which type of which module a frame was decompiled from, for an
    /// engine that can be given the decompilation as that module's symbols. Left unset for an
    /// engine in another process that speaks its own protocol and cannot take them.</param>
    public static async Task<IReadOnlyList<StackFrameInfo>> EnrichAsync(
        IReadOnlyList<StackFrameInfo> frames,
        CancellationToken ct,
        Func<string, string, CancellationToken, Task>? decompiled = null)
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
            {
                budget--;
                await ShareSymbolsAsync(frame.ModulePath, resolved, decompiled, ct).ConfigureAwait(false);
            }

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

    /// <summary>
    /// Gives the engine the decompilation that just answered for a frame, as symbols for its module.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolving the frame produced a file, a line, and a full sequence-point map for the type. The
    /// file and the line answer this stop; the map answers what the user does next, and only the
    /// engine can act on it. Without it the engine still has no symbols for that module — so a step
    /// inside the frame has no statement to run to, and runs a single IL instruction back onto the
    /// line it started on, and a breakpoint set in the decompiled file has no document to bind
    /// against. Filling in the stack afterwards could never fix either: by then the engine has
    /// already given its answer.
    /// </para>
    /// <para>
    /// Only the type name is passed on. Building the map is the expensive half — it copies every
    /// sequence point of every method in the type, and the engine may then have to serialize it
    /// across a pipe — and whether it is worth doing is a question only the engine's own side can
    /// answer, since it is the one that knows what it has already been given. A frame the pushed
    /// symbols cannot answer still arrives here without a file on every stop, so deciding here
    /// would mean rebuilding and resending the same type once per step.
    /// </para>
    /// <para>
    /// Best-effort throughout: it is the fallback for a module that had no symbols, and failing to
    /// install it leaves exactly the behaviour that was there before it existed.
    /// </para>
    /// </remarks>
    private static async Task ShareSymbolsAsync(
        string modulePath,
        FrameSourceResult resolved,
        Func<string, string, CancellationToken, Task>? decompiled,
        CancellationToken ct)
    {
        if (decompiled is null || resolved.DecompiledType.Length == 0)
            return;

        try
        {
            await decompiled(modulePath, resolved.DecompiledType, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not give the debug engine symbols for '{resolved.DecompiledType}': {ex.Message}",
                key: $"debug-decompiled-symbols:{modulePath}");
        }
    }
}
