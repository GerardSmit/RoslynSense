using System.Collections.Immutable;

namespace RoslynMCP.Languages.Cron.Core;

/// <summary>
/// The registration APIs the pack knows without being told, and the names of the well-known types
/// whose presence says a solution schedules anything at all.
/// </summary>
/// <remarks>
/// Shipped rather than configured, on the same reasoning as the resource-lookup presets: every
/// entry names a fully-qualified containing type, so the Hangfire rows are inert in a solution with
/// no Hangfire reference and the Quartz rows in one with no Quartz. A user's own entries are
/// appended to this list rather than replacing it, so configuring a wrapper method does not cost
/// them the library underneath it.
/// </remarks>
internal static class CronPresets
{
    /// <summary>Hangfire's static façade and the interface behind it.</summary>
    public const string HangfireRecurringJob = "Hangfire.RecurringJob";

    private const string HangfireManager = "Hangfire.IRecurringJobManager";
    private const string HangfireManagerExtensions = "Hangfire.RecurringJobManagerExtensions";

    /// <summary>Quartz's trigger builder, and the schedule builder it is usually given.</summary>
    public const string QuartzTriggerBuilder = "Quartz.TriggerBuilder";

    private const string QuartzCronScheduleBuilder = "Quartz.CronScheduleBuilder";
    private const string QuartzTriggerExtensions = "Quartz.CronScheduleTriggerBuilderExtensions";
    private const string QuartzConfigurator = "Quartz.ITriggerConfigurator";

    /// <summary>
    /// The types whose absence means there is nothing of that library to find.
    /// </summary>
    /// <remarks>
    /// Both façades of each library, because a solution may reference only one: a service that
    /// schedules through <c>IRecurringJobManager</c> never mentions <c>RecurringJob</c>, and
    /// checking for the static one alone would make the pack silently inert there.
    /// </remarks>
    public static ImmutableArray<string> WellKnownTypes { get; } =
    [
        HangfireRecurringJob,
        HangfireManager,
        QuartzTriggerBuilder,
        "Quartz.IJob",
    ];

    /// <summary>
    /// Parameter names that mean "this string is a schedule", whatever declares them.
    /// </summary>
    /// <remarks>
    /// The reason the pack works on a solution's own wrapper method with no configuration at all.
    /// <c>cronExpression</c> is Hangfire's spelling and by far the most common; the other three are
    /// what people write when they wrap it. Matched case-insensitively, so a <c>Cron</c> parameter
    /// on a record is claimed as readily as a <c>cron</c> argument.
    /// </remarks>
    public static ImmutableArray<string> ParameterNames { get; } =
        ["cronExpression", "cron", "cronSchedule", "crontab", "cronString"];

    /// <summary>
    /// Simple method names worth binding a call for even when the literal beside them says nothing.
    /// </summary>
    /// <remarks>
    /// The cheap syntax gate rejects a literal that does not look like a schedule, which is right
    /// almost always and wrong in the one case that matters most: the half-typed empty string a
    /// person is about to write one into. Naming the handful of methods that take one lets
    /// completion work there without binding every literal in the file.
    /// </remarks>
    public static ImmutableArray<string> SchedulingMethods { get; } =
    [
        "AddOrUpdate", "RemoveIfExists", "WithCronSchedule", "CronSchedule",
        "AddTrigger", "AddJob", "Schedule", "ScheduleJob",
    ];

    /// <summary>The shipped bindings, in the order they are tried.</summary>
    public static ImmutableArray<CronBinding> Bindings { get; } =
    [
        // One row for every AddOrUpdate overload there is. The expression moves position between
        // them and keeps its name, so naming the parameter rather than counting to it is both
        // shorter and the thing that will still be true after the next Hangfire release.
        new CronBinding
        {
            ContainingType = HangfireRecurringJob,
            MemberName = "AddOrUpdate",
            Library = CronLibrary.Hangfire,
            Kind = CronRegistrationKind.Schedule,
            Dialect = CronDialect.Hangfire,
        },
        new CronBinding
        {
            ContainingType = HangfireManager,
            MemberName = "AddOrUpdate",
            Library = CronLibrary.Hangfire,
            Kind = CronRegistrationKind.Schedule,
            Dialect = CronDialect.Hangfire,
        },
        new CronBinding
        {
            ContainingType = HangfireManagerExtensions,
            MemberName = "AddOrUpdate",
            Library = CronLibrary.Hangfire,
            Kind = CronRegistrationKind.Schedule,
            Dialect = CronDialect.Hangfire,
        },

        // Carries no schedule. Listed so that the tree can pair a removal with the job it names,
        // and so that nothing else tries to read its id as one.
        new CronBinding
        {
            ContainingType = HangfireRecurringJob,
            MemberName = "RemoveIfExists",
            IdIndex = 0,
            Library = CronLibrary.Hangfire,
            Kind = CronRegistrationKind.Remove,
            Dialect = CronDialect.Hangfire,
        },

        // Quartz's fluent side. The trigger carries the schedule and the job is named by the
        // WithIdentity and ForJob calls around it, which is why the kind is Trigger rather than
        // Schedule — the two halves are found separately and paired afterwards.
        new CronBinding
        {
            ContainingType = QuartzTriggerExtensions,
            MemberName = "WithCronSchedule",
            CronIndex = 0,
            Library = CronLibrary.Quartz,
            Kind = CronRegistrationKind.Trigger,
            Dialect = CronDialect.Quartz,
        },
        new CronBinding
        {
            ContainingType = QuartzConfigurator,
            MemberName = "WithCronSchedule",
            CronIndex = 0,
            Library = CronLibrary.Quartz,
            Kind = CronRegistrationKind.Trigger,
            Dialect = CronDialect.Quartz,
        },
        new CronBinding
        {
            ContainingType = QuartzCronScheduleBuilder,
            MemberName = "CronSchedule",
            CronIndex = 0,
            Library = CronLibrary.Quartz,
            Kind = CronRegistrationKind.Trigger,
            Dialect = CronDialect.Quartz,
        },
    ];
}
