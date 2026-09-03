namespace RoslynMCP.Debugger;

/// <summary>
/// The rules a breakpoint carries beyond "stop here", shared by everything that has to apply them.
/// </summary>
/// <remarks>
/// Lives in the engine assembly because that is where the rules are now enforced — inside the
/// debuggee's own suspend, where excluding a hit costs a comparison. The host keeps an emulation
/// path for engines that cannot do it themselves, and that path has to agree with this one
/// exactly: a hit count that means different things on the two runtimes is a bug nobody can
/// reproduce.
/// </remarks>
public static class BreakpointRules
{
    /// <summary>
    /// Applies the editor's hit-count vocabulary: <c>&gt; n</c>, <c>&gt;= n</c>, <c>&lt; n</c>,
    /// <c>&lt;= n</c>, <c>= n</c>, <c>% n</c>, and a bare count meaning "on hit n and after".
    /// </summary>
    /// <remarks>
    /// An unparseable rule reports <c>true</c>. Fail-open for the same reason a broken condition
    /// does: a breakpoint that stops more often than asked is visible and fixable, one that
    /// silently never stops is neither.
    /// </remarks>
    public static bool HitConditionMet(string condition, int hits)
    {
        var text = condition.Trim();

        var split = 0;
        while (split < text.Length && !char.IsAsciiDigit(text[split]))
            split++;

        // An empty prefix is the bare-count form, which means ">= n". Anything else that is not a
        // known operator — "!=", a typo, a stray word — is not quietly read as ">=": guessing an
        // operator the user did not write is how a breakpoint stops on hits nobody asked for.
        // Fail open instead, so the rule is visibly ignored rather than invisibly reinterpreted.
        var prefix = text[..split].Trim();
        var @operator = prefix switch
        {
            ">=" or "" => ">=",
            "<=" => "<=",
            "==" or "=" => "=",
            ">" => ">",
            "<" => "<",
            "%" => "%",
            _ => null,
        };

        if (@operator is null)
            return true;

        if (!int.TryParse(text[split..].Trim(), out var target) || target <= 0)
            return true;

        return @operator switch
        {
            ">" => hits > target,
            "<" => hits < target,
            "<=" => hits <= target,
            "=" => hits == target,
            "%" => hits % target == 0,
            _ => hits >= target,
        };
    }
}
