namespace RoslynMCP.Services.Debugging;

/// <summary>
/// Data breakpoints, built out of stepping and evaluation because neither engine has them.
/// </summary>
/// <remarks>
/// <para>
/// A real data breakpoint is a CPU debug register: the processor traps the write and the debugger
/// costs nothing until it fires. Neither engine here exposes that. netcoredbg has no
/// <c>setDataBreakpoints</c> at all and nothing in MI to express one; ICorDebug's value-change
/// support ended with the .NET Framework 1.x <c>ICorDebugValue</c> breakpoints, which no runtime
/// still honors.
/// </para>
/// <para>
/// So this steps and compares: step one line, evaluate every watched expression, and stop when one
/// of them reads back differently. That is a genuine data breakpoint in the only sense the user
/// cares about — execution stops on the statement that changed the value — and it costs a debugger
/// round trip per statement, which is why it is off unless a watch exists and why
/// <see cref="StepBudget"/> bounds it.
/// </para>
/// <para>
/// Two honest consequences follow from the mechanism, and both are reported rather than hidden:
/// only <c>write</c> access is detectable, since a read leaves the value alone; and the stop lands
/// on the statement <em>after</em> the write, because a change can only be observed once it has
/// happened.
/// </para>
/// </remarks>
internal sealed class DataBreakpointWatcher
{
    /// <summary>How many steps to take before giving up. A watch inside a long-running loop would
    /// otherwise hold the caller forever with nothing to show for it.</summary>
    public const int StepBudget = 20_000;

    private readonly IDebugBackend _backend;
    private readonly Dictionary<string, DataBreakpointSpec> _watches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _hits = new(StringComparer.Ordinal);

    public DataBreakpointWatcher(IDebugBackend backend) => _backend = backend;

    /// <summary>Whether any watch is armed. Stepping is only worth its cost when one is.</summary>
    public bool Any => _watches.Count > 0;

    /// <summary>The change that caused the most recent stop, for the DAP <c>stopped</c> event.</summary>
    public DataBreakpointHit? LastHit { get; private set; }

    public IReadOnlyList<DataBreakpointSpec> Watches => [.. _watches.Values];

    /// <summary>
    /// Replaces the watch set, which is what DAP's <c>setDataBreakpoints</c> means, and captures
    /// each expression's current value as the baseline to compare against.
    /// </summary>
    /// <returns>Per-watch verification: an expression that does not evaluate here cannot be
    /// watched, and the client should show it as unverified rather than silently ignore it.</returns>
    public async Task<IReadOnlyList<DataBreakpointStatus>> SetAsync(
        IReadOnlyList<DataBreakpointSpec> specs, CancellationToken cancellationToken = default)
    {
        _watches.Clear();
        _values.Clear();
        _hits.Clear();
        LastHit = null;

        var results = new List<DataBreakpointStatus>(specs.Count);

        foreach (var spec in specs)
        {
            if (!spec.AccessType.Equals("write", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new DataBreakpointStatus(spec.DataId, false,
                    $"'{spec.AccessType}' access cannot be detected; only writes change a value."));
                continue;
            }

            var (ok, value) = await ReadAsync(spec.Expression, cancellationToken);
            if (!ok)
            {
                results.Add(new DataBreakpointStatus(
                    spec.DataId, false, $"'{spec.Expression}' does not evaluate here: {value}"));
                continue;
            }

            _watches[spec.DataId] = spec;
            _values[spec.DataId] = value;
            results.Add(new DataBreakpointStatus(spec.DataId, true, ""));
        }

        return results;
    }

    public void Clear()
    {
        _watches.Clear();
        _values.Clear();
        _hits.Clear();
        LastHit = null;
    }

    /// <summary>
    /// Steps until a watched value changes, something else stops the target, or the budget runs out.
    /// </summary>
    /// <param name="isRealStop">Asked at every stop that is not a value change: <c>true</c> means
    /// the stop belongs to the user (a breakpoint that should surface) and the walk ends there.
    /// This is what keeps a data breakpoint from stepping straight through a source breakpoint.</param>
    public async Task<(DataWatchOutcome Outcome, string Message)> ContinueAsync(
        Func<Task<bool>> isRealStop, CancellationToken cancellationToken = default)
    {
        LastHit = null;

        for (int steps = 0; steps < StepBudget; steps++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string message = await _backend.StepInAsync(cancellationToken);

            if (_backend.CurrentFrame is null)
                return (DataWatchOutcome.Exited, message);

            if (await CheckAsync(cancellationToken) is { } hit)
                return (DataWatchOutcome.Changed, hit.Description);

            if (_backend.CurrentFrame is { BreakpointNumber: > 0 } && await isRealStop())
                return (DataWatchOutcome.OtherStop, message);
        }

        return (DataWatchOutcome.BudgetExhausted,
            $"No watched value changed in {StepBudget} steps; the target is still suspended.");
    }

    /// <summary>
    /// Re-reads every watch and reports the first that moved, updating the baselines as it goes.
    /// </summary>
    /// <remarks>
    /// An expression that stops evaluating — because the step went into a callee where the name is
    /// out of scope — is skipped rather than treated as a change. The watch resumes as soon as the
    /// frame that owns it is current again, which is why a write inside a callee surfaces on
    /// return rather than at the assignment itself.
    /// </remarks>
    public async Task<DataBreakpointHit?> CheckAsync(CancellationToken cancellationToken = default)
    {
        DataBreakpointHit? hit = null;

        foreach (var (dataId, spec) in _watches)
        {
            var (ok, value) = await ReadAsync(spec.Expression, cancellationToken);
            if (!ok)
                continue;

            if (!_values.TryGetValue(dataId, out string? previous))
            {
                _values[dataId] = value;
                continue;
            }

            if (string.Equals(previous, value, StringComparison.Ordinal))
                continue;

            _values[dataId] = value;

            if (hit is not null)
                continue; // the first change wins the stop; the rest keep their new baselines

            if (!await PassesAsync(spec, cancellationToken))
                continue;

            hit = new DataBreakpointHit(dataId, spec.Expression, previous, value);
        }

        // Assigned unconditionally: a stop that was not a value change must not inherit the
        // description of one that was.
        LastHit = hit;
        return hit;
    }

    /// <summary>Applies the watch's own condition and hit count, which gate the stop but never the
    /// baseline update — a suppressed change has still happened.</summary>
    private async Task<bool> PassesAsync(DataBreakpointSpec spec, CancellationToken cancellationToken)
    {
        if (spec.Condition is { Length: > 0 } condition)
        {
            var (ok, value) = await ReadAsync(condition, cancellationToken);
            if (!ok || !IsTruthy(value))
                return false;
        }

        if (spec.HitCondition is not { Length: > 0 } hitCondition)
            return true;

        _hits.TryGetValue(spec.DataId, out int hits);
        _hits[spec.DataId] = ++hits;
        return PublishingDebugBackend.HitConditionMet(hitCondition, hits);
    }

    private async Task<(bool Ok, string Value)> ReadAsync(string expression, CancellationToken cancellationToken)
    {
        try
        {
            string value = (await _backend.EvaluateAsync(expression, cancellationToken)).Trim();
            return value.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                ? (false, value)
                : (true, value);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Both engines render a bool as <c>true</c>/<c>false</c>; anything non-zero and
    /// non-null counts, matching how a conditional breakpoint reads its condition.</summary>
    internal static bool IsTruthy(string value)
    {
        string text = value.Trim();
        return text.Length != 0 &&
            !text.Equals("false", StringComparison.OrdinalIgnoreCase) &&
            !text.Equals("0", StringComparison.Ordinal) &&
            !text.Equals("null", StringComparison.OrdinalIgnoreCase);
    }
}
