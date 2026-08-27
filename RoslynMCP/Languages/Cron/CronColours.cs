namespace RoslynMCP.Languages.Cron;

/// <summary>
/// A colour per field of a schedule, from names C# already has.
/// </summary>
/// <remarks>
/// <para>
/// A crontab expression is read by counting positions, and every mistake anyone makes with one is a
/// miscount: <c>0 3 * * 1</c> runs weekly and <c>0 3 1 * *</c> runs monthly, they are one
/// transposition apart, and both look like a schedule. Giving each position its own colour means
/// the two are visibly different shapes before a reader has counted anything — which is the whole
/// trick <see cref="Formatting.FormatColours"/> plays on <c>dd-MM-yyyy</c>, applied to the other
/// language people keep getting off by one.
/// </para>
/// <para>
/// C#'s own token names, so a theme that already distinguishes a method from a class distinguishes
/// a month from a day with no configuration. See <see cref="IEmbeddedSemanticTokensProvider"/> for
/// why a name outside that legend is not an option.
/// </para>
/// </remarks>
internal static class CronColours
{
    /// <summary>
    /// The legend name for a field.
    /// </summary>
    /// <remarks>
    /// Every one differs from every other, unlike the format-specifier table, which can reuse a
    /// name between groups that never appear together. Here they always appear together, and in a
    /// fixed order — the fields of a schedule are all neighbours.
    /// </remarks>
    public static string For(CronFieldKind kind) => kind switch
    {
        CronFieldKind.Second => "number",
        CronFieldKind.Minute => "parameter",
        CronFieldKind.Hour => "enumMember",
        CronFieldKind.DayOfMonth => "class",
        CronFieldKind.Month => "method",
        CronFieldKind.DayOfWeek => "macro",
        CronFieldKind.Year => "property",
        _ => "operator",
    };

    /// <summary>The commas between the terms of one field, which are punctuation.</summary>
    public const string Separator = "operator";

    /// <summary>An <c>@</c> word, which stands for a whole expression rather than a field.</summary>
    public const string Macro = "keyword";
}
