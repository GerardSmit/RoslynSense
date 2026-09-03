using System.Globalization;
using System.Text;

namespace RoslynMCP.Languages.Cron;

/// <summary>
/// What a hover over a schedule says.
/// </summary>
/// <remarks>
/// Three things, in the order they answer the question a reader actually has: what this schedule
/// means in words, when it next comes round, and — always — which library's reading that was under.
/// The last one looks like a footnote and is the point: the same six fields mean different days to
/// Hangfire and to Quartz, and a hover that quietly picked one would be as misleading as the string
/// it is explaining.
/// </remarks>
internal static class CronMarkdown
{
    /// <summary>How many fire times are worth showing.</summary>
    /// <remarks>
    /// Three. One does not show the interval, and a longer list stops being read — the sentence
    /// above them is what carries the meaning, and these are the check on it.
    /// </remarks>
    private const int Occurrences = 3;

    /// <summary>The hover for a whole expression.</summary>
    public static string Schedule(CronParse parse, string text, DateTime now)
    {
        var markdown = new StringBuilder();

        if (CronDescription.Describe(parse) is { } sentence)
            markdown.Append("**").Append(sentence).Append("**\n\n");

        markdown.Append("```\n").Append(text).Append("\n```");

        if (Fields(parse) is { Length: > 0 } fields)
            markdown.Append("\n\n").Append(fields);

        var next = CronDescription.Next(parse, now, Occurrences);
        if (next.Length > 0)
        {
            markdown.Append("\n\nNext: ")
                .Append(string.Join(", ", next.Select(When)));
        }
        else if (parse.IsValid)
        {
            // A valid expression with no fire times names a day the calendar never reaches — the
            // thirtieth of February. Saying so is the whole answer.
            markdown.Append("\n\nThis never comes round: no date matches it.");
        }

        markdown.Append("\n\nRead as ").Append(Reading(parse.Dialect)).Append('.');
        return markdown.ToString();
    }

    /// <summary>The hover for one term of one field.</summary>
    public static string Term(CronTerm term, CronFieldKind kind, CronDialect dialect)
    {
        string unit = Cron.Unit(kind);

        string meaning = term switch
        {
            { Kind: CronTermKind.All, Step: { } step } => $"every {Nth(step)} of the {unit}",
            { Kind: CronTermKind.All } => $"every one of the {unit}",
            { Kind: CronTermKind.Any } => "whatever the other day field leaves",
            { Kind: CronTermKind.Value, From: { } value, Step: { } step } =>
                $"{Named(value, kind, dialect)}, then every {Nth(step)}",
            { Kind: CronTermKind.Value, From: { } value } => Named(value, kind, dialect),
            { Kind: CronTermKind.Range, From: { } from, To: { } to, Step: { } step } =>
                $"{Named(from, kind, dialect)} to {Named(to, kind, dialect)}, every {Nth(step)}",
            { Kind: CronTermKind.Range, From: { } from, To: { } to } =>
                $"{Named(from, kind, dialect)} to {Named(to, kind, dialect)}",
            { Kind: CronTermKind.Last, Marker: 'W' } => "the last weekday of the month",
            { Kind: CronTermKind.Last, From: { } day } =>
                $"the last {Named(day, kind, dialect)} of the month",
            { Kind: CronTermKind.Last } => "the last day of the month",
            { Kind: CronTermKind.Weekday, From: { } day } =>
                $"the weekday nearest the {Ordinal(day)}, never crossing into another month",
            { Kind: CronTermKind.Nth, From: { } day, Nth: { } nth } =>
                $"the {Ordinal(nth)} {Named(day, kind, dialect)} of the month",
            _ => "not something this reads",
        };

        var (min, max) = Cron.RangeOf(kind, dialect);

        return $"**{Field(kind)}** — {meaning}\n\n"
            + $"This field counts {unit}, {min} to {max}, under {Reading(dialect)}.";
    }

    /// <summary>Each field and what it holds, which is the positional cue written out.</summary>
    private static string Fields(CronParse parse)
    {
        if (parse.Fields.IsDefaultOrEmpty)
            return string.Empty;

        var rows = new StringBuilder();
        foreach (var field in parse.Fields)
        {
            if (field.IsOpen)
                continue;

            rows.Append("- ").Append(Field(field.Kind)).Append(": `")
                .Append(Text(parse, field)).Append("`\n");
        }

        return rows.ToString().TrimEnd('\n');
    }

    private static string Text(CronParse parse, CronField field) =>
        field.Terms.Length == 0 ? "*" : Join(parse, field);

    private static string Join(CronParse parse, CronField field)
    {
        _ = parse;
        var parts = new List<string>(field.Terms.Length);
        foreach (var term in field.Terms)
            parts.Add(Shape(term));
        return string.Join(",", parts);
    }

    private static string Shape(CronTerm term)
    {
        string body = term switch
        {
            { Kind: CronTermKind.All } => "*",
            { Kind: CronTermKind.Any } => "?",
            { Kind: CronTermKind.Value, From: { } value } => value.ToString(CultureInfo.InvariantCulture),
            { Kind: CronTermKind.Range, From: { } from, To: { } to } => $"{from}-{to}",
            { Kind: CronTermKind.Last, Marker: 'W' } => "LW",
            { Kind: CronTermKind.Last, From: { } day } => $"{day}L",
            { Kind: CronTermKind.Last } => "L",
            { Kind: CronTermKind.Weekday, From: { } day } => $"{day}W",
            { Kind: CronTermKind.Nth, From: { } day, Nth: { } nth } => $"{day}#{nth}",
            _ => "?",
        };

        return term.Step is { } step ? $"{body}/{step}" : body;
    }

    /// <summary>A value with its name beside it, where the field has names.</summary>
    private static string Named(int value, CronFieldKind kind, CronDialect dialect) =>
        Cron.NameOf(kind, value, dialect) is { } name
            ? $"{name} ({value})"
            : value.ToString(CultureInfo.InvariantCulture);

    public static string Field(CronFieldKind kind) => kind switch
    {
        CronFieldKind.Second => "Second",
        CronFieldKind.Minute => "Minute",
        CronFieldKind.Hour => "Hour",
        CronFieldKind.DayOfMonth => "Day of month",
        CronFieldKind.Month => "Month",
        CronFieldKind.DayOfWeek => "Day of week",
        CronFieldKind.Year => "Year",
        _ => "Field",
    };

    /// <summary>
    /// Which library's rules were applied, named so a reader can disagree with it.
    /// </summary>
    /// <remarks>
    /// "a plain crontab" rather than a library name for the default, because that is the honest
    /// description: nothing at the call site said which scheduler reads the string, so the reading
    /// is the conservative one rather than a claim about the code.
    /// </remarks>
    private static string Reading(CronDialect dialect) => dialect switch
    {
        CronDialect.Hangfire => "Hangfire (Cronos): Sunday is 0",
        CronDialect.Quartz => "Quartz: seconds first, Sunday is 1",
        _ => "a plain crontab: Sunday is 0",
    };

    private static string When(DateTime at) =>
        at.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);

    private static string Nth(int step) => step switch
    {
        1 => "one",
        2 => "second",
        3 => "third",
        _ => $"{Ordinal(step)}",
    };

    private static string Ordinal(int value)
    {
        string suffix = (value % 100) is >= 11 and <= 13
            ? "th"
            : (value % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

        return value.ToString(CultureInfo.InvariantCulture) + suffix;
    }
}
