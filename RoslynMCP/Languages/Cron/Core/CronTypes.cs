using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Languages.Cron.Core;

/// <summary>
/// Which schedulers a compilation can see, and the gate in front of everything else the pack does.
/// </summary>
/// <remarks>
/// <para>
/// Two jobs in one lookup. The first is cost: in a solution that schedules nothing this is one
/// metadata probe per compilation, memoized, after which nothing walks a syntax tree. The second is
/// the dialect — a solution that references Quartz and not Hangfire reads its six-field expressions
/// Quartz's way, and that is knowable here for free rather than guessable later.
/// </para>
/// <para>
/// Memoized on the <see cref="Compilation"/> through a <see cref="ConditionalWeakTable"/>: a
/// keystroke makes a new compilation and the old entry falls out with it, so there is nothing to
/// invalidate and no symbol is held past the compilation that owns it.
/// </para>
/// </remarks>
internal sealed record CronTypes(bool Hangfire, bool Quartz)
{
    private static readonly ConditionalWeakTable<Compilation, CronTypes> s_cache = new();

    /// <summary>Whether this compilation references a scheduler at all.</summary>
    public bool Any => Hangfire || Quartz;

    /// <summary>
    /// The reading this compilation implies, or null when it implies none.
    /// </summary>
    /// <remarks>
    /// Only when exactly one library is present. A solution referencing both — a service migrating
    /// from one to the other, which is when a wrong-day-of-the-week bug is most likely — gets no
    /// answer from here, and the call site has to say.
    /// </remarks>
    public CronDialect? Dialect => (Hangfire, Quartz) switch
    {
        (true, false) => CronDialect.Hangfire,
        (false, true) => CronDialect.Quartz,
        _ => null,
    };

    public static CronTypes For(Compilation compilation) =>
        s_cache.GetValue(compilation, static c => new CronTypes(
            Hangfire:
                c.GetTypeByMetadataName(CronPresets.HangfireRecurringJob) is not null
                || c.GetTypeByMetadataName("Hangfire.IRecurringJobManager") is not null,
            Quartz:
                c.GetTypeByMetadataName(CronPresets.QuartzTriggerBuilder) is not null
                || c.GetTypeByMetadataName("Quartz.IJob") is not null));
}
