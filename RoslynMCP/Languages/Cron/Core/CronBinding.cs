using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.Cron.Core;

/// <summary>Which scheduler reads the expression, which is what fixes the dialect.</summary>
internal enum CronLibrary
{
    /// <summary>Nothing named a library — a parameter called <c>cronExpression</c> on a method of
    /// the solution's own.</summary>
    Unknown,

    Hangfire,
    Quartz,
}

/// <summary>What a registration call does.</summary>
internal enum CronRegistrationKind
{
    /// <summary>Declares or replaces a recurring job.</summary>
    Schedule,

    /// <summary>Removes one. It names a job but carries no schedule.</summary>
    Remove,

    /// <summary>Builds a trigger, which carries a schedule but names its job elsewhere.</summary>
    Trigger,
}

/// <summary>
/// One place in C# whose string argument is a crontab expression.
/// </summary>
/// <remarks>
/// <para>
/// The same triple of containing type, member name and signature that
/// <see cref="Services.Symbols.MemberSignature"/> already defines and that the value-set bindings
/// are written with, so the settings page's shape editor draws this without being taught anything
/// new, and a reader who has configured one has configured the other.
/// </para>
/// <para>
/// The shipped entries in <see cref="CronPresets"/> all name a fully-qualified containing type, so
/// the table is inert in a solution that references neither library — a method of the user's own
/// called <c>AddOrUpdate</c> is not Hangfire's and is not claimed as one.
/// </para>
/// </remarks>
internal sealed record CronBinding
{
    /// <summary>The member's name.</summary>
    public required string MemberName { get; init; }

    /// <summary>The declaring type's full name, or null to match any type declaring the member.</summary>
    public string? ContainingType { get; init; }

    /// <summary>One type name per parameter, <c>*</c> for any, or null to match every overload.</summary>
    public ImmutableArray<string>? ParameterTypes { get; init; }

    /// <summary>
    /// Which parameter carries the expression, counted from zero — or null to find it by name.
    /// </summary>
    /// <remarks>
    /// Null is what the Hangfire entry uses, and it is the reason that entry is one row rather than
    /// fourteen: <c>RecurringJob.AddOrUpdate</c> has an overload for every combination of job id,
    /// queue and options, and the expression sits in a different position in most of them — but it
    /// is called <c>cronExpression</c> in all of them.
    /// </remarks>
    public int? CronIndex { get; init; }

    /// <summary>Which parameter names the job, when one does.</summary>
    public int? IdIndex { get; init; }

    /// <summary>Which parameter says what to run, when one does.</summary>
    public int? MethodIndex { get; init; }

    public CronLibrary Library { get; init; } = CronLibrary.Unknown;

    public CronRegistrationKind Kind { get; init; } = CronRegistrationKind.Schedule;

    /// <summary>How the expression is read. See <see cref="CronDialect"/>.</summary>
    public CronDialect Dialect { get; init; } = CronDialect.Standard;
}

/// <summary>
/// A string literal that turned out to be a schedule, and what decides how to read it.
/// </summary>
/// <param name="Binding">The entry that claimed it, or null when only the parameter's name did.</param>
/// <param name="Dialect">The reading that applies here, already resolved.</param>
/// <param name="Subject">The call, as it should be named to a person — <c>RecurringJob.AddOrUpdate</c>.</param>
/// <param name="Span">The literal's own span, for a diagnostic that has nothing finer to point at.</param>
internal readonly record struct CronCall(
    CronBinding? Binding,
    CronDialect Dialect,
    string Subject,
    TextSpan Span);
