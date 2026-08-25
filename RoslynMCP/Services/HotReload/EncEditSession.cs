using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Contracts.EditAndContinue;
using Microsoft.CodeAnalysis.EditAndContinue;
using Microsoft.CodeAnalysis.Host;

namespace RoslynMCP.Services.HotReload;

/// <summary>
/// Roslyn's Edit-and-Continue engine, with emitting and committing kept apart.
/// </summary>
/// <remarks>
/// <para>
/// This replaces <c>UnitTestingHotReloadService</c>, which is the same engine behind a smaller
/// door. That wrapper takes a <c>commitUpdates</c> flag and decides at emit time: true commits the
/// new baseline the moment the delta exists, false throws the delta away. Neither is what an apply
/// needs, because whether the edit actually reached the running process is not known until after
/// the emit. Committing regardless is what makes a failed apply unrecoverable — the baseline has
/// moved on, so the user's retry diffs against the edit that never landed and reports "no changes"
/// while the running code is still the old code.
/// </para>
/// <para>
/// Going a level down costs nothing else: the wrapper is a hundred lines over the same
/// <see cref="IEditAndContinueService"/>, and two things it discards turn out to matter. It hands
/// Roslyn an active-statement list that is hardcoded empty, so the rude edit that matters most —
/// editing a method that is on a stack — can never be diagnosed; and it drops the per-method line
/// deltas Roslyn computes, which are what keeps a debugger's line numbers correct across several
/// edits. Both are carried through here.
/// </para>
/// </remarks>
internal sealed class EncEditSession
{
    private readonly IEditAndContinueService _service;
    private readonly DebuggingSessionId _sessionId;

    /// <summary>Whether an emit is waiting to be committed or discarded. Roslyn holds exactly one
    /// pending update per session, and leaving it pending blocks the next emit.</summary>
    private bool _pending;

    /// <summary>Whether Roslyn currently believes the debuggee is stopped. Starts false because
    /// that is what <c>StartDebuggingSession</c> assumes.</summary>
    private bool _inBreakState;

    private EncEditSession(IEditAndContinueService service, DebuggingSessionId sessionId)
    {
        _service = service;
        _sessionId = sessionId;
    }

    /// <summary>
    /// Opens an edit session against the built output, which becomes the baseline every delta is
    /// computed against.
    /// </summary>
    /// <param name="activeStatements">Where the debuggee is stopped. Asked for once per edit session
    /// and only while Roslyn believes the process is stopped — see the break state in
    /// <see cref="EmitAsync"/>. Null when nothing is being debugged, which is the same as "no
    /// statement is active".</param>
    public static async Task<EncEditSession> StartAsync(
        HostWorkspaceServices services,
        Solution solution,
        ImmutableArray<string> capabilities,
        Func<CancellationToken, ValueTask<ImmutableArray<ManagedActiveStatementDebugInfo>>>? activeStatements,
        CancellationToken cancellationToken)
    {
        // Roslyn reads the documents' text and checksums up front; without this the first emit
        // pays for the whole solution and can see a torn view of it.
        await EditAndContinueService.HydrateDocumentsAsync(solution, cancellationToken)
            .ConfigureAwait(false);

        var service = services.GetRequiredService<IEditAndContinueWorkspaceService>().Service;
        var sessionId = service.StartDebuggingSession(
            solution,
            new DebuggerView(capabilities, activeStatements),
            NullPdbMatchingSourceTextProvider.Instance,
            reportDiagnostics: false);

        return new EncEditSession(service, sessionId);
    }

    /// <summary>
    /// Computes the delta for everything edited since the last commit, without moving the baseline.
    /// </summary>
    /// <remarks>
    /// The result is pending until <see cref="Commit"/> or <see cref="Discard"/> says what happened
    /// to it. Emitting twice without deciding is a bug rather than a retry, so the second emit
    /// discards the first — a delta nobody committed was, by definition, not applied.
    /// </remarks>
    /// <param name="inBreakState">Whether the debuggee is stopped right now. This is not a hint:
    /// Roslyn only reads the active statements at all while it believes the process is stopped, and
    /// out of break state it substitutes an empty map without asking. Left unsaid, an edit to a
    /// method with live frames is analysed as though nothing were running and quietly accepted.</param>
    public async Task<EncEmitResult> EmitAsync(
        Solution solution, bool inBreakState, CancellationToken cancellationToken)
    {
        if (_pending)
            Discard();

        // Only on a change: this restarts the edit session, which is cheap but not free, and the
        // usual case is a run of edits with the process in the same state throughout.
        if (_inBreakState != inBreakState)
        {
            _inBreakState = inBreakState;
            _service.BreakStateOrCapabilitiesChanged(_sessionId, inBreakState);
        }

        var results = await _service.EmitSolutionUpdateAsync(
            _sessionId,
            solution,
            ImmutableDictionary<ProjectId, RunningProjectOptions>.Empty,
            NoActiveStatementSpans,
            cancellationToken).ConfigureAwait(false);

        var diagnostics = results.GetAllDiagnostics();
        _pending = results.ModuleUpdates.Status == ModuleUpdateStatus.Ready;

        // Roslyn emits nothing rather than something wrong, so errors and updates never arrive
        // together: an error here is a refusal, and there is no pending baseline to decide about.
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            if (_pending)
                Discard();
            return new EncEmitResult([], diagnostics, Blocked: true);
        }

        return new EncEmitResult(
            results.ModuleUpdates.Updates,
            diagnostics,
            Blocked: results.ModuleUpdates.Status == ModuleUpdateStatus.Blocked);
    }

    /// <summary>
    /// Makes the emitted delta the new baseline, so the next edit diffs against it.
    /// </summary>
    /// <remarks>
    /// Called only once the running process has taken the delta. Called any earlier, a target that
    /// refused the edit is left running code the baseline says it is not running.
    /// </remarks>
    public void Commit()
    {
        if (!_pending)
            return;
        _pending = false;
        _service.CommitSolutionUpdate(_sessionId);
    }

    /// <summary>
    /// Throws the emitted delta away and leaves the baseline where it was.
    /// </summary>
    /// <remarks>
    /// This is what makes a failed apply retryable: the edit is still an edit as far as Roslyn is
    /// concerned, so fixing whatever refused it and applying again emits the same delta rather than
    /// reporting that there is nothing to do.
    /// </remarks>
    public void Discard()
    {
        if (!_pending)
            return;
        _pending = false;
        _service.DiscardSolutionUpdate(_sessionId);
    }

    public void EndSession()
    {
        Discard();
        _service.EndDebuggingSession(_sessionId);
    }

    /// <summary>
    /// Where the user has moved statements within a document since the baseline was taken.
    /// </summary>
    /// <remarks>
    /// Empty because this host has no editor buffers of its own: an apply reads the files from
    /// disk, so the spans Roslyn computes from the document text are already current. An editor
    /// that tracked unsaved edits would answer here instead.
    /// </remarks>
    private static readonly ActiveStatementSpanProvider NoActiveStatementSpans =
        (_, _, _) => ValueTask.FromResult(ImmutableArray<ActiveStatementSpan>.Empty);

    /// <summary>
    /// What Roslyn is allowed to ask about the running process.
    /// </summary>
    /// <remarks>
    /// The capabilities decide which edits are expressible at all — .NET Framework's EnC accepts
    /// far less than CoreCLR's — and the active statements decide which are safe right now.
    /// </remarks>
    private sealed class DebuggerView(
        ImmutableArray<string> capabilities,
        Func<CancellationToken, ValueTask<ImmutableArray<ManagedActiveStatementDebugInfo>>>? activeStatements)
        : IManagedHotReloadService
    {
        public ValueTask<ImmutableArray<string>> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(capabilities);

        public ValueTask<ImmutableArray<ManagedActiveStatementDebugInfo>> GetActiveStatementsAsync(
            CancellationToken cancellationToken) =>
            activeStatements?.Invoke(cancellationToken)
            ?? ValueTask.FromResult(ImmutableArray<ManagedActiveStatementDebugInfo>.Empty);

        /// <remarks>
        /// Always available. Whether a module can actually take an edit is decided by the runtime
        /// when the delta is applied, and reporting a guess here would refuse edits that would have
        /// worked — the failure this answers is not one this host can observe in advance.
        /// </remarks>
        public ValueTask<ManagedHotReloadAvailability> GetAvailabilityAsync(
            Guid module, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ManagedHotReloadAvailability(
                ManagedHotReloadAvailabilityStatus.Available));

        public ValueTask PrepareModuleForUpdateAsync(Guid module, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}

/// <summary>One emit: the deltas, whatever Roslyn had to say, and whether it refused outright.</summary>
/// <param name="Blocked">Roslyn could not produce a delta for at least one project. Distinct from
/// "no updates", which is the ordinary answer when nothing changed.</param>
internal readonly record struct EncEmitResult(
    ImmutableArray<ManagedHotReloadUpdate> Updates,
    ImmutableArray<Diagnostic> Diagnostics,
    bool Blocked);
