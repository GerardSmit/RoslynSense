using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Contracts.EditAndContinue;
using RoslynMCP.Services.Debugging;

namespace RoslynMCP.Services.HotReload;

/// <summary>
/// Where the debuggee is currently executing, in the shape Roslyn's edit analysis wants.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets the compiler refuse the rude edit that matters most: changing a method that
/// is on a stack. Without it Roslyn analyses every edit as though nothing were running, accepts
/// edits it would otherwise reject, and the delta is applied to a method whose old frames are
/// still executing — after which the process's behaviour is undefined rather than diagnosed.
/// The whole cost of getting it is one stack walk of one suspended process.
/// </para>
/// <para>
/// Reported for every suspended thread, not only the one the stop landed on. A method is on a
/// stack whichever thread's stack it is on, and a web application under a breakpoint has many.
/// </para>
/// </remarks>
internal static class EncActiveStatements
{
    /// <summary>
    /// Reads the live debug session's stacks, or reports none when nothing is stopped.
    /// </summary>
    /// <remarks>
    /// Never throws: a failure to enumerate must not take the whole apply down with it. It does
    /// degrade honestly rather than silently, though — an empty list means "no statement is
    /// active", which is exactly true when nothing is being debugged and merely optimistic when
    /// the walk failed, so a failure is reported as a diagnostic through the usual channel.
    /// </remarks>
    public static async ValueTask<ImmutableArray<ManagedActiveStatementDebugInfo>> CollectAsync(
        CancellationToken cancellationToken)
    {
        if (DebugSessionManager.GetSession() is not { } session)
            return [];

        try
        {
            var threads = await session.GetThreadsAsync(cancellationToken).ConfigureAwait(false);
            if (threads.Count == 0)
                return [];

            var builder = ImmutableArray.CreateBuilder<ManagedActiveStatementDebugInfo>();
            var byInstruction = new Dictionary<(Guid, int, int), int>();

            foreach (var thread in threads)
            {
                var frames = await session.GetStackFramesAsync(thread.Id, cancellationToken)
                    .ConfigureAwait(false);

                for (int i = 0; i < frames.Count; i++)
                    Add(builder, byInstruction, frames[i], isLeaf: i == 0);
            }

            return builder.ToImmutable();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Analysing the edit as though nothing were running is the pre-existing behaviour and
            // still produces a delta; it just cannot catch an edit to a running method. Saying so
            // beats letting the user believe the check happened.
            ServiceLog.Warn(
                $"Could not read the debuggee's active statements, so edits to methods that are " +
                $"currently executing will not be diagnosed: {ex.Message}",
                key: "enc-active-statements");
            return [];
        }
    }

    private static void Add(
        ImmutableArray<ManagedActiveStatementDebugInfo>.Builder builder,
        Dictionary<(Guid, int, int), int> byInstruction,
        StackFrameInfo frame,
        bool isLeaf)
    {
        // No source means nothing to compare an edit against — framework and native frames are
        // not editable and reporting them would only make Roslyn resolve documents that do not
        // exist in the solution.
        if (frame.FilePath.Length == 0 || frame.MethodToken == 0 || frame.IlOffset < 0)
            return;

        if (ExternalSource.DebugFrameSource.TryReadMvid(frame.ModulePath) is not { Length: > 0 } mvidText ||
            !Guid.TryParse(mvidText, out Guid mvid))
        {
            return;
        }

        // The same statement can be on several threads' stacks at once, and reporting it twice makes
        // Roslyn analyse it repeatedly for the same answer.
        var key = (mvid, frame.MethodToken, frame.IlOffset);
        bool duplicate = byInstruction.TryGetValue(key, out int existing);

        // When the two sightings disagree, the leaf wins. A non-leaf frame is described as
        // partially executed — it is stopped at a call that has not returned — and a statement that
        // is the leaf somewhere has not necessarily run at all, so keeping the non-leaf description
        // would tell Roslyn more of it has executed than is true of every thread.
        if (duplicate && !isLeaf)
            return;

        // Version 1 is the baseline. A method already updated by an earlier delta is on a later
        // version, which this host does not track per frame — the consequence is that Roslyn
        // treats such a frame as up to date, which is the same assumption it made before active
        // statements were reported at all.
        var instruction = new ManagedInstructionId(
            new ManagedMethodId(mvid, frame.MethodToken, version: 1),
            frame.IlOffset);

        var flags = (isLeaf ? ActiveStatementFlags.LeafFrame : ActiveStatementFlags.NonLeafFrame) |
                    ActiveStatementFlags.MethodUpToDate;

        // A non-leaf frame is stopped at a call that has not returned, so the part of the
        // statement before the call has already run — which changes what an edit may do to it.
        if (!isLeaf)
            flags |= ActiveStatementFlags.PartiallyExecuted;

        if (frame.IsNonUserCode)
            flags |= ActiveStatementFlags.NonUserCode;

        var reported = new ManagedActiveStatementDebugInfo(
            instruction,
            frame.FilePath,
            Span(frame),
            flags);

        if (duplicate)
        {
            builder[existing] = reported;
            return;
        }

        byInstruction[key] = builder.Count;
        builder.Add(reported);
    }

    /// <summary>
    /// The frame's statement as a 0-based span, which is what the contract uses.
    /// </summary>
    /// <remarks>
    /// Symbols report 1-based lines and columns, and a frame whose PDB gave no end falls back to
    /// the start — a degenerate span on the right line, rather than a span reaching to line 0 that
    /// would overlap everything above it.
    /// </remarks>
    private static SourceSpan Span(StackFrameInfo frame)
    {
        int startLine = Math.Max(0, frame.Line - 1);
        int startColumn = Math.Max(0, frame.Column - 1);
        int endLine = frame.EndLine > 0 ? frame.EndLine - 1 : startLine;
        int endColumn = frame.EndColumn > 0 ? frame.EndColumn - 1 : startColumn;

        if (endLine < startLine || (endLine == startLine && endColumn < startColumn))
            (endLine, endColumn) = (startLine, startColumn);

        return new SourceSpan(startLine, startColumn, endLine, endColumn);
    }
}
