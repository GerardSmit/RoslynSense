using System.Collections.Immutable;
using System.Globalization;

namespace RoslynMCP.Languages.Cron;

/// <summary>
/// A schedule said in words, and the times it actually comes round to.
/// </summary>
/// <remarks>
/// <para>
/// The sentence is the point of the whole pack. <c>0 22 * * 1-6</c> is five tokens that a reader
/// decodes by counting positions on their fingers, and gets wrong roughly as often as not; "at
/// 22:00, Monday to Saturday" is the same fact in a form nobody has to decode. The fire times
/// underneath it are the check on the sentence — a description can be argued with, three dates
/// cannot.
/// </para>
/// <para>
/// <see cref="Next"/> is the only real arithmetic in the pack, and it is deliberately the dullest
/// implementation that works: walk forward a day at a time, ask whether the day is in the schedule,
/// and if it is, walk its allowed times. It never runs long because it stops at the count asked
/// for, and a schedule whose day never comes gives up after five years rather than looping.
/// </para>
/// </remarks>
internal static class CronDescription
{
    /// <summary>How far ahead <see cref="Next"/> is willing to look before giving up.</summary>
    /// <remarks>
    /// Five years covers a February 29th schedule, which is the longest gap a valid expression can
    /// name. Anything that finds nothing in five years names no day at all — <c>0 0 30 2 *</c>, the
    /// thirtieth of February — and the honest answer for that is no dates rather than a hang.
    /// </remarks>
    private const int Horizon = 5;

    /// <summary>The schedule as a sentence, or null when it could not be read.</summary>
    public static string? Describe(CronParse parse)
    {
        if (!parse.IsValid)
            return null;

        if (parse.Macro is { } macro)
        {
            if (Cron.Expand(macro, parse.Dialect) is not { } expansion)
                return null;

            return Describe(Cron.Parse(expansion, parse.Dialect));
        }

        string time = Time(parse);
        var clauses = new List<string>();

        if (Days(parse) is { } days)
            clauses.Add(days);
        if (Months(parse) is { } months)
            clauses.Add(months);
        if (Years(parse) is { } years)
            clauses.Add(years);

        // "At 03:00" is true but reads as a one-off. Nothing restricted the day, so the schedule
        // is a daily one and saying so is what a reader is actually after.
        if (clauses.Count == 0)
            return time.StartsWith("At ", StringComparison.Ordinal) ? $"Every day at {time[3..]}" : time;

        return $"{time}, {string.Join(", ", clauses)}";
    }

    /// <summary>
    /// The next times the schedule comes round, starting strictly after <paramref name="from"/>.
    /// </summary>
    /// <remarks>
    /// Empty when the expression could not be read, when it names a day that never arrives, or when
    /// it uses a marker this walk does not implement. Callers show nothing in that case: a wrong
    /// fire time is worse than no fire time, because a reader has no way to tell it is wrong.
    /// </remarks>
    public static ImmutableArray<DateTime> Next(CronParse parse, DateTime from, int count)
    {
        if (!parse.IsValid || count <= 0)
            return [];

        if (parse.Macro is { } macro)
        {
            return Cron.Expand(macro, parse.Dialect) is { } expansion
                ? Next(Cron.Parse(expansion, parse.Dialect), from, count)
                : [];
        }

        if (Set(parse, CronFieldKind.Second, [0]) is not { } seconds
            || Set(parse, CronFieldKind.Minute, []) is not { } minutes
            || Set(parse, CronFieldKind.Hour, []) is not { } hours
            || Set(parse, CronFieldKind.Month, []) is not { } months)
        {
            return [];
        }

        var years = Set(parse, CronFieldKind.Year, []);

        var results = ImmutableArray.CreateBuilder<DateTime>(count);

        // Strictly after: a hover taken at exactly 03:00 on a schedule that fires at 03:00 should
        // say when it fires next, not restate the moment the reader is already looking at.
        var start = Truncate(from).AddSeconds(1);
        var day = start.Date;
        var limit = day.AddYears(Horizon);

        while (day < limit && results.Count < count)
        {
            if ((years is null || years.Count == 0 || years.Contains(day.Year))
                && months.Contains(day.Month)
                && Day(parse, day))
            {
                foreach (int hour in hours)
                {
                    foreach (int minute in minutes)
                    {
                        foreach (int second in seconds)
                        {
                            var at = day.AddHours(hour).AddMinutes(minute).AddSeconds(second);
                            if (at < start)
                                continue;

                            results.Add(at);
                            if (results.Count == count)
                                return results.ToImmutable();
                        }
                    }
                }
            }

            day = day.AddDays(1);
        }

        return results.ToImmutable();
    }

    private static DateTime Truncate(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Kind);

    // ---- the sentence -------------------------------------------------------------------------

    private static string Time(CronParse parse)
    {
        var second = parse.Field(CronFieldKind.Second);
        var minute = parse.Field(CronFieldKind.Minute);
        var hour = parse.Field(CronFieldKind.Hour);

        bool secondsOpen = second is null || second.Value.IsOpen;
        bool minutesOpen = minute is { IsOpen: true };
        bool hoursOpen = hour is { IsOpen: true };

        if (second is { } seconds && Every(seconds) is { } everySecond && minutesOpen && hoursOpen)
            return $"Every {Plural(everySecond, "second")}";

        if (second is { IsOpen: true } && minutesOpen && hoursOpen)
            return "Every second";

        if (secondsOpen && minutesOpen && hoursOpen)
            return "Every minute";

        if (minute is { } minutes && Every(minutes) is { } everyMinute && hoursOpen)
            return $"Every {Plural(everyMinute, "minute")}";

        var minuteValues = Values(parse, CronFieldKind.Minute);
        var hourValues = Values(parse, CronFieldKind.Hour);

        if (hour is { } hours && Every(hours) is { } everyHour && minuteValues is { Count: 1 })
            return $"Every {Plural(everyHour, "hour")} at {Past(minuteValues[0])}";

        if (hoursOpen && minuteValues is { Count: > 0 })
            return $"Every hour at {Join(minuteValues.Select(Past))}";

        if (hourValues is { Count: > 0 } && minuteValues is { Count: > 0 })
        {
            // The cross product is what a reader wants to see — "at 06:00, 12:00 and 18:00" — but
            // only while it stays a list. Past a dozen it stops being readable and the two fields
            // said separately are clearer than a wall of times.
            if (hourValues.Count * minuteValues.Count <= 12)
            {
                var times = hourValues
                    .SelectMany(h => minuteValues.Select(m => Clock(h, m)))
                    .OrderBy(t => t, StringComparer.Ordinal);
                return $"At {Join(times)}";
            }

            return $"At {Join(minuteValues.Select(Past))} of {Join(hourValues.Select(Hour24))}";
        }

        return $"At {Field(parse, CronFieldKind.Hour)} hours, {Field(parse, CronFieldKind.Minute)} minutes";
    }

    private static string? Days(CronParse parse)
    {
        var month = parse.Field(CronFieldKind.DayOfMonth);
        var week = parse.Field(CronFieldKind.DayOfWeek);

        bool monthOpen = month is null || month.Value.IsOpen;
        bool weekOpen = week is null || week.Value.IsOpen;

        if (monthOpen && weekOpen)
            return null;

        var clauses = new List<string>();
        if (!monthOpen && month is { } dayOfMonth)
            clauses.Add($"on the {MonthDays(dayOfMonth)}");
        if (!weekOpen && week is { } dayOfWeek)
            clauses.Add($"on {WeekDays(dayOfWeek, parse.Dialect)}");

        // Both restricted is Cronos's "either" reading; Quartz rejects it outright, so by the time
        // a description is asked for the only way to reach this is the reading where it means or.
        return string.Join(" or ", clauses);
    }

    private static string? Months(CronParse parse)
    {
        if (parse.Field(CronFieldKind.Month) is not { IsOpen: false } field)
            return null;

        var names = new List<string>();
        foreach (var term in field.Terms)
        {
            switch (term)
            {
                case { Kind: CronTermKind.Value, From: { } value }:
                    names.Add(Cron.NameOf(CronFieldKind.Month, value, parse.Dialect) ?? value.ToString(CultureInfo.InvariantCulture));
                    break;
                case { Kind: CronTermKind.Range, From: { } from, To: { } to }:
                    names.Add($"{Cron.NameOf(CronFieldKind.Month, from, parse.Dialect)} to {Cron.NameOf(CronFieldKind.Month, to, parse.Dialect)}");
                    break;
                case { Kind: CronTermKind.All, Step: { } step }:
                    names.Add($"every {Ordinal(step)} month");
                    break;
                default:
                    return $"in {Field(parse, CronFieldKind.Month)}";
            }
        }

        return $"in {Join(names)}";
    }

    private static string? Years(CronParse parse) =>
        parse.Field(CronFieldKind.Year) is { IsOpen: false } ? $"in {Field(parse, CronFieldKind.Year)}" : null;

    private static string MonthDays(CronField field)
    {
        var parts = new List<string>();
        foreach (var term in field.Terms)
        {
            parts.Add(term switch
            {
                { Kind: CronTermKind.Last, Marker: 'W' } => "last weekday",
                { Kind: CronTermKind.Last } => "last day",
                { Kind: CronTermKind.Weekday, From: { } day } => $"weekday nearest the {Ordinal(day)}",
                { Kind: CronTermKind.Value, From: { } day } => Ordinal(day),
                { Kind: CronTermKind.Range, From: { } from, To: { } to } => $"{Ordinal(from)} to {Ordinal(to)}",
                { Kind: CronTermKind.All, Step: { } step } => $"every {Ordinal(step)} day",
                _ => "?",
            });
        }

        return Join(parts);
    }

    private static string WeekDays(CronField field, CronDialect dialect)
    {
        var parts = new List<string>();
        foreach (var term in field.Terms)
        {
            parts.Add(term switch
            {
                { Kind: CronTermKind.Last, From: { } day } =>
                    $"the last {Name(day, dialect)}",
                { Kind: CronTermKind.Nth, From: { } day, Nth: { } nth } =>
                    $"the {Ordinal(nth)} {Name(day, dialect)}",
                { Kind: CronTermKind.Value, From: { } day } => Name(day, dialect),
                { Kind: CronTermKind.Range, From: { } from, To: { } to } =>
                    $"{Name(from, dialect)} to {Name(to, dialect)}",
                { Kind: CronTermKind.All, Step: { } step } => $"every {Ordinal(step)} day",
                _ => "?",
            });
        }

        return Join(parts);

        static string Name(int value, CronDialect dialect) =>
            Cron.NameOf(CronFieldKind.DayOfWeek, value, dialect) ?? value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The raw text of a field, for the shapes the sentence has no words for.</summary>
    private static string Field(CronParse parse, CronFieldKind kind) =>
        parse.Field(kind) is { } field && field.Terms.Length > 0
            ? string.Join(", ", field.Terms.Select(Describe))
            : "*";

    private static string Describe(CronTerm term) => term switch
    {
        { Kind: CronTermKind.All, Step: { } step } => $"every {step}",
        { Kind: CronTermKind.All } => "every",
        { Kind: CronTermKind.Value, From: { } value } => value.ToString(CultureInfo.InvariantCulture),
        { Kind: CronTermKind.Range, From: { } from, To: { } to } => $"{from}-{to}",
        _ => "?",
    };

    /// <summary>The <c>n</c> of a bare <c>*/n</c> field, or null when it says anything else.</summary>
    private static int? Every(CronField field) =>
        field.Terms is [{ Kind: CronTermKind.All, Step: { } step }] ? step : null;

    // ---- the walk -----------------------------------------------------------------------------

    /// <summary>
    /// The values a field allows, in ascending order — or null when it uses a marker that is about
    /// the calendar rather than about a number, which only the day fields may.
    /// </summary>
    private static List<int>? Set(CronParse parse, CronFieldKind kind, List<int> whenAbsent)
    {
        if (parse.Field(kind) is not { } field)
            return whenAbsent;

        var (min, max) = Cron.RangeOf(kind, parse.Dialect);
        var values = new SortedSet<int>();

        foreach (var term in field.Terms)
        {
            switch (term)
            {
                case { Kind: CronTermKind.All or CronTermKind.Any }:
                    Add(values, min, max, term.Step ?? 1, min, max);
                    break;

                case { Kind: CronTermKind.Value, From: { } value }:
                    // '5/10' is Quartz's "from 5, every 10 to the end of the field". A bare value
                    // is the same statement with no step, which the loop below handles as one pass.
                    Add(values, value, term.Step is null ? value : max, term.Step ?? 1, min, max);
                    break;

                case { Kind: CronTermKind.Range, From: { } from, To: { } to }:
                    Add(values, from, to, term.Step ?? 1, min, max);
                    break;

                default:
                    return null;
            }
        }

        return [.. values];

        static void Add(SortedSet<int> into, int from, int to, int step, int min, int max)
        {
            if (from < min || from > max)
                return;

            // A range whose start is past its end wraps the field — 'NOV-FEB' is one winter, not
            // an empty set — so the walk is over a length rather than between two bounds.
            int length = to >= from ? to - from : max - min + 1 - (from - to);
            for (int i = 0; i <= length; i += step)
            {
                int value = from + i;
                if (value > max)
                    value = min + (value - max - 1);
                into.Add(value);
            }
        }
    }

    /// <summary>Whether a calendar day is one the schedule names.</summary>
    private static bool Day(CronParse parse, DateTime day)
    {
        var month = parse.Field(CronFieldKind.DayOfMonth);
        var week = parse.Field(CronFieldKind.DayOfWeek);

        bool monthOpen = month is null || month.Value.IsOpen;
        bool weekOpen = week is null || week.Value.IsOpen;

        if (monthOpen && weekOpen)
            return true;
        if (weekOpen)
            return InMonth(month!.Value, day);
        if (monthOpen)
            return InWeek(week!.Value, day, parse.Dialect);

        // Both fields naming days is Cronos's "either" reading. Quartz rejects it before this is
        // ever asked, so there is one behaviour here rather than a dialect switch.
        return InMonth(month!.Value, day) || InWeek(week!.Value, day, parse.Dialect);
    }

    private static bool InMonth(CronField field, DateTime day)
    {
        int last = DateTime.DaysInMonth(day.Year, day.Month);

        foreach (var term in field.Terms)
        {
            bool hit = term switch
            {
                { Kind: CronTermKind.All or CronTermKind.Any, Step: { } step } => (day.Day - 1) % step == 0,
                { Kind: CronTermKind.All or CronTermKind.Any } => true,
                { Kind: CronTermKind.Value, From: { } value } => Stepped(day.Day, value, last, term.Step),
                { Kind: CronTermKind.Range, From: { } from, To: { } to } => InRange(day.Day, from, to, term.Step ?? 1, 1, last),
                { Kind: CronTermKind.Last, Marker: 'W' } => day.Day == LastWeekday(day.Year, day.Month),
                { Kind: CronTermKind.Last } => day.Day == last,
                { Kind: CronTermKind.Weekday, From: { } target } => day.Day == NearestWeekday(day.Year, day.Month, target),
                _ => false,
            };

            if (hit)
                return true;
        }

        return false;
    }

    private static bool InWeek(CronField field, DateTime day, CronDialect dialect)
    {
        int last = DateTime.DaysInMonth(day.Year, day.Month);

        foreach (var term in field.Terms)
        {
            bool hit = term switch
            {
                { Kind: CronTermKind.All or CronTermKind.Any, Step: { } step } => (int)day.DayOfWeek % step == 0,
                { Kind: CronTermKind.All or CronTermKind.Any } => true,
                { Kind: CronTermKind.Value, From: { } value } => Cron.DayOf(value, dialect) == day.DayOfWeek,
                { Kind: CronTermKind.Range, From: { } from, To: { } to } => InWeekRange(day.DayOfWeek, from, to, dialect),

                // 'the last Friday' is any Friday with no Friday after it in the month, which is
                // the same as being in the final seven days.
                { Kind: CronTermKind.Last, From: { } value } =>
                    Cron.DayOf(value, dialect) == day.DayOfWeek && day.Day > last - 7,

                { Kind: CronTermKind.Nth, From: { } value, Nth: { } nth } =>
                    Cron.DayOf(value, dialect) == day.DayOfWeek && ((day.Day - 1) / 7) + 1 == nth,

                _ => false,
            };

            if (hit)
                return true;
        }

        return false;
    }

    private static bool Stepped(int day, int from, int max, int? step) =>
        step is null ? day == from : day >= from && (day - from) % step.Value == 0 && day <= max;

    private static bool InRange(int value, int from, int to, int step, int min, int max)
    {
        int length = to >= from ? to - from : max - min + 1 - (from - to);
        for (int i = 0; i <= length; i += step)
        {
            int at = from + i;
            if (at > max)
                at = min + (at - max - 1);
            if (at == value)
                return true;
        }

        return false;
    }

    private static bool InWeekRange(DayOfWeek day, int from, int to, CronDialect dialect)
    {
        // Walked as days rather than as numbers, because the two dialects number them differently
        // and 'FRI-MON' wraps under both.
        var at = Cron.DayOf(from, dialect);
        var end = Cron.DayOf(to, dialect);

        for (int i = 0; i < 7; i++)
        {
            var current = (DayOfWeek)(((int)at + i) % 7);
            if (current == day)
                return true;
            if (current == end)
                break;
        }

        return false;
    }

    private static int LastWeekday(int year, int month)
    {
        int day = DateTime.DaysInMonth(year, month);
        while (new DateTime(year, month, day).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            day--;
        return day;
    }

    /// <summary>
    /// The weekday a <c>15W</c> actually lands on.
    /// </summary>
    /// <remarks>
    /// Nearest, but never across a month boundary: a Saturday the 1st moves forward to the 3rd
    /// rather than back into the previous month, which is the rule both libraries implement.
    /// </remarks>
    private static int NearestWeekday(int year, int month, int target)
    {
        int last = DateTime.DaysInMonth(year, month);
        int day = Math.Min(target, last);

        return new DateTime(year, month, day).DayOfWeek switch
        {
            DayOfWeek.Saturday => day > 1 ? day - 1 : day + 2,
            DayOfWeek.Sunday => day < last ? day + 1 : day - 2,
            _ => day,
        };
    }

    // ---- wording ------------------------------------------------------------------------------

    private static List<int>? Values(CronParse parse, CronFieldKind kind) => Set(parse, kind, []);

    private static string Clock(int hour, int minute) =>
        $"{hour:00}:{minute:00}";

    private static string Hour24(int hour) => $"{hour:00}:00";

    private static string Past(int minute) => $":{minute:00}";

    private static string Plural(int count, string unit) =>
        count == 1 ? unit : $"{count} {unit}s";

    private static string Ordinal(int value)
    {
        string suffix = (value % 100) is >= 11 and <= 13
            ? "th"
            : (value % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

        return value.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    /// <summary>A list said the way a person says one: commas, and an "and" before the last.</summary>
    private static string Join(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        return list.Count switch
        {
            0 => string.Empty,
            1 => list[0],
            2 => $"{list[0]} and {list[1]}",
            _ => $"{string.Join(", ", list.Take(list.Count - 1))} and {list[^1]}",
        };
    }
}
