using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.Cron;

/// <summary>
/// Which reading of a crontab expression applies.
/// </summary>
/// <remarks>
/// Not a cosmetic distinction. <c>0 0 12 * * ?</c> is a valid six-field Quartz expression and a
/// six-field Cronos one, and the day of week it names differs by a day between them: Cronos counts
/// Sunday as 0 and Quartz counts it as 1. An editor that colours and describes such a string
/// without knowing which library reads it is guessing at the one thing the reader came for.
/// </remarks>
internal enum CronDialect
{
    /// <summary>Plain crontab, and the conservative default. Sunday is 0.</summary>
    Standard,

    /// <summary>What Hangfire hands to Cronos: standard plus two extra macros.</summary>
    Hangfire,

    /// <summary>Quartz: seconds are mandatory, Sunday is 1, and <c>?</c> is required.</summary>
    Quartz,
}

/// <summary>One position of an expression, named by what it schedules.</summary>
internal enum CronFieldKind
{
    Second,
    Minute,
    Hour,
    DayOfMonth,
    Month,
    DayOfWeek,
    Year,
}

/// <summary>What one comma-separated piece of a field says.</summary>
internal enum CronTermKind
{
    /// <summary>Could not be read. The problem list says why.</summary>
    Invalid,

    /// <summary><c>*</c> — every value the field has.</summary>
    All,

    /// <summary><c>?</c> — this field defers to the other day field.</summary>
    Any,

    /// <summary>A single value, written as a number or a name.</summary>
    Value,

    /// <summary><c>from-to</c>, which may wrap the end of the field.</summary>
    Range,

    /// <summary><c>L</c>, <c>LW</c> or <c>5L</c> — reckoned from the end of the month.</summary>
    Last,

    /// <summary><c>15W</c> — the weekday nearest a day of the month.</summary>
    Weekday,

    /// <summary><c>6#3</c> — the nth such weekday of the month.</summary>
    Nth,
}

/// <summary>Something the expression says that the dialect reading it will not accept.</summary>
internal readonly record struct CronProblem(TextSpan Span, string Message);

/// <summary>
/// One piece of a field.
/// </summary>
/// <param name="Span">Where it is written, for colouring it and for hovering over it.</param>
/// <param name="Kind">What shape of piece it is.</param>
/// <param name="From">The value, or the start of a range, when the kind has one.</param>
/// <param name="To">The end of a range.</param>
/// <param name="Step">The <c>/n</c> suffix, if any.</param>
/// <param name="Marker">
/// The letter that gave the term its kind — <c>W</c> on a <see cref="CronTermKind.Last"/> term is
/// what tells <c>LW</c> from <c>L</c>. Otherwise the null character.
/// </param>
/// <param name="Nth">The ordinal of a <c>#</c> term.</param>
internal readonly record struct CronTerm(
    TextSpan Span,
    CronTermKind Kind,
    int? From = null,
    int? To = null,
    int? Step = null,
    char Marker = '\0',
    int? Nth = null)
{
    /// <summary>Whether this term places no restriction at all on its field.</summary>
    public bool IsOpen => Kind is CronTermKind.All or CronTermKind.Any && Step is null;
}

/// <summary>One whitespace-separated field, and the terms it was written as.</summary>
internal readonly record struct CronField(
    CronFieldKind Kind, TextSpan Span, ImmutableArray<CronTerm> Terms)
{
    /// <summary>Whether the field restricts nothing — <c>*</c>, or <c>?</c> in a day field.</summary>
    public bool IsOpen => Terms.Length == 1 && Terms[0].IsOpen;
}

/// <summary>
/// A crontab expression, read.
/// </summary>
/// <param name="Dialect">The reading it was parsed under.</param>
/// <param name="Fields">The fields, in the order written. Empty for a macro and for a bad shape.</param>
/// <param name="Problems">What the dialect will not accept. Empty is the good case.</param>
/// <param name="Macro">
/// The whole expression when it was one of the <c>@</c> words, in which case there are no fields.
/// <see cref="Cron.Expand"/> turns it back into an expression for describing and for counting
/// forward from.
/// </param>
internal sealed record CronParse(
    CronDialect Dialect,
    ImmutableArray<CronField> Fields,
    ImmutableArray<CronProblem> Problems,
    string? Macro)
{
    public bool IsValid => Problems.IsEmpty;

    /// <summary>The field an offset into the expression falls in, or null between fields.</summary>
    public CronField? FieldAt(int offset)
    {
        foreach (var field in Fields)
        {
            // Inclusive of the end so that a caret sitting just past a field still belongs to it —
            // which is where completion is asked for, every time.
            if (offset >= field.Span.Start && offset <= field.Span.End)
                return field;
        }

        return null;
    }

    /// <summary>The term an offset falls in, or null.</summary>
    public CronTerm? TermAt(int offset)
    {
        if (FieldAt(offset) is not { } field)
            return null;

        foreach (var term in field.Terms)
        {
            if (offset >= term.Span.Start && offset <= term.Span.End)
                return term;
        }

        return null;
    }

    /// <summary>The field of a given kind, or null when this shape has none.</summary>
    public CronField? Field(CronFieldKind kind)
    {
        foreach (var field in Fields)
        {
            if (field.Kind == kind)
                return field;
        }

        return null;
    }
}

/// <summary>
/// The grammar of a crontab expression, for both dialects.
/// </summary>
/// <remarks>
/// <para>
/// One parser rather than two. The dialects are the same grammar over a different field table, and
/// the whole value of reading them here is being able to say that the same six words mean different
/// things in each — which two parsers, written apart and drifting apart, would eventually stop
/// being able to say.
/// </para>
/// <para>
/// Everything dialect-dependent is a table: <see cref="Shape"/>, <see cref="RangeOf"/>,
/// <see cref="Allows"/> and the macro lists. The tokenizer below knows nothing about either library.
/// </para>
/// </remarks>
internal static class Cron
{
    /// <summary>Names accepted in the month field, in order from January.</summary>
    private static readonly string[] s_monthNames =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    /// <summary>Names accepted in the day-of-week field, in order from Sunday.</summary>
    private static readonly string[] s_dayNames =
        ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private static readonly string[] s_monthWords =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    private static readonly string[] s_dayWords =
        ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    /// <summary>The macros every dialect understands, and what each is short for.</summary>
    private static readonly Dictionary<string, string> s_macros = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@yearly"] = "0 0 1 1 *",
        ["@annually"] = "0 0 1 1 *",
        ["@monthly"] = "0 0 1 * *",
        ["@weekly"] = "0 0 * * 0",
        ["@daily"] = "0 0 * * *",
        ["@midnight"] = "0 0 * * *",
        ["@hourly"] = "0 * * * *",
    };

    /// <summary>The two Hangfire adds on top, which no other dialect accepts.</summary>
    private static readonly Dictionary<string, string> s_extras = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@every_minute"] = "* * * * *",
        ["@every_second"] = "* * * * * *",
    };

    /// <summary>
    /// Which fields an expression of this length has, in the order they are written — the whole of
    /// what separates the dialects.
    /// </summary>
    /// <remarks>
    /// A table rather than a chain of conditions, because the interesting case is the one where the
    /// same count means different things: six fields is seconds-first in both readings, seven is
    /// Quartz alone, and five is never Quartz at all.
    /// </remarks>
    public static ImmutableArray<CronFieldKind> Shape(CronDialect dialect, int count) =>
        (dialect, count) switch
        {
            (CronDialect.Quartz, 6) =>
            [
                CronFieldKind.Second, CronFieldKind.Minute, CronFieldKind.Hour,
                CronFieldKind.DayOfMonth, CronFieldKind.Month, CronFieldKind.DayOfWeek,
            ],
            (CronDialect.Quartz, 7) =>
            [
                CronFieldKind.Second, CronFieldKind.Minute, CronFieldKind.Hour,
                CronFieldKind.DayOfMonth, CronFieldKind.Month, CronFieldKind.DayOfWeek,
                CronFieldKind.Year,
            ],
            (CronDialect.Quartz, _) => [],

            (_, 5) =>
            [
                CronFieldKind.Minute, CronFieldKind.Hour,
                CronFieldKind.DayOfMonth, CronFieldKind.Month, CronFieldKind.DayOfWeek,
            ],
            (_, 6) =>
            [
                CronFieldKind.Second, CronFieldKind.Minute, CronFieldKind.Hour,
                CronFieldKind.DayOfMonth, CronFieldKind.Month, CronFieldKind.DayOfWeek,
            ],
            _ => [],
        };

    /// <summary>
    /// What a field counts from and to.
    /// </summary>
    /// <remarks>
    /// The day of week is the one that differs, and it differs by one: Cronos numbers Sunday 0 and
    /// accepts 7 for it as well, Quartz numbers Sunday 1. Every wrong-day-of-the-week bug in a
    /// solution that uses both libraries comes back to this line.
    /// </remarks>
    public static (int Min, int Max) RangeOf(CronFieldKind kind, CronDialect dialect) => kind switch
    {
        CronFieldKind.Second => (0, 59),
        CronFieldKind.Minute => (0, 59),
        CronFieldKind.Hour => (0, 23),
        CronFieldKind.DayOfMonth => (1, 31),
        CronFieldKind.Month => (1, 12),
        CronFieldKind.DayOfWeek => dialect == CronDialect.Quartz ? (1, 7) : (0, 7),
        CronFieldKind.Year => (1970, 2099),
        _ => (0, 0),
    };

    /// <summary>Whether a marker character means anything in this field.</summary>
    /// <remarks>
    /// Both readings accept <c>L</c>, <c>W</c> and <c>#</c> — Cronos grew them to match Quartz — so
    /// the table is about the field rather than the library. <c>?</c> is the exception: Quartz
    /// requires exactly one of the two day fields to carry it, and Cronos merely tolerates it as
    /// another spelling of <c>*</c>.
    /// </remarks>
    public static bool Allows(CronFieldKind kind, char marker) => (kind, marker) switch
    {
        (CronFieldKind.DayOfMonth, 'L') => true,
        (CronFieldKind.DayOfMonth, 'W') => true,
        (CronFieldKind.DayOfWeek, 'L') => true,
        (CronFieldKind.DayOfWeek, '#') => true,
        (CronFieldKind.DayOfMonth or CronFieldKind.DayOfWeek, '?') => true,
        _ => false,
    };

    /// <summary>The expression a macro stands for, or null when it is not one.</summary>
    public static string? Expand(string macro, CronDialect dialect)
    {
        if (s_macros.TryGetValue(macro, out string? standard))
            return standard;

        return dialect == CronDialect.Hangfire && s_extras.TryGetValue(macro, out string? extra)
            ? extra
            : null;
    }

    /// <summary>Every macro this dialect accepts, for completion to offer.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Macros(CronDialect dialect) =>
        dialect == CronDialect.Hangfire ? s_macros.Concat(s_extras) : s_macros;

    /// <summary>The English name of a value in a field, or null for a field that has none.</summary>
    public static string? NameOf(CronFieldKind kind, int value, CronDialect dialect) => kind switch
    {
        CronFieldKind.Month when value is >= 1 and <= 12 => s_monthWords[value - 1],
        CronFieldKind.DayOfWeek when InRange(value, kind, dialect) => s_dayWords[(int)DayOf(value, dialect)],
        _ => null,
    };

    /// <summary>The calendar day a day-of-week value names under this dialect.</summary>
    public static DayOfWeek DayOf(int value, CronDialect dialect) => dialect == CronDialect.Quartz
        ? (DayOfWeek)((value - 1) % 7)
        : (DayOfWeek)(value % 7);

    /// <summary>The value that names a calendar day under this dialect — the inverse of the above.</summary>
    public static int ValueOf(DayOfWeek day, CronDialect dialect) =>
        dialect == CronDialect.Quartz ? (int)day + 1 : (int)day;

    /// <summary>Read an expression. Never throws; what it cannot accept it reports.</summary>
    public static CronParse Parse(string text, CronDialect dialect)
    {
        var problems = ImmutableArray.CreateBuilder<CronProblem>();

        int start = 0;
        int end = text.Length;
        while (start < end && char.IsWhiteSpace(text[start]))
            start++;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;

        if (start >= end)
        {
            problems.Add(new CronProblem(new TextSpan(0, text.Length), "A schedule cannot be empty."));
            return new CronParse(dialect, [], problems.ToImmutable(), null);
        }

        if (text[start] == '@')
            return Macro(text, start, end, dialect, problems);

        var raw = Split(text, start, end);
        var shape = Shape(dialect, raw.Count);

        if (shape.IsEmpty)
        {
            problems.Add(new CronProblem(
                TextSpan.FromBounds(start, end),
                $"{Name(dialect)} reads {Counts(dialect)}, and this has {raw.Count}."));
            return new CronParse(dialect, [], problems.ToImmutable(), null);
        }

        var fields = ImmutableArray.CreateBuilder<CronField>(shape.Length);
        for (int i = 0; i < shape.Length; i++)
            fields.Add(Field(text, raw[i], shape[i], dialect, problems));

        Days(fields, dialect, problems);
        return new CronParse(dialect, fields.ToImmutable(), problems.ToImmutable(), null);
    }

    private static CronParse Macro(
        string text, int start, int end, CronDialect dialect,
        ImmutableArray<CronProblem>.Builder problems)
    {
        string macro = text[start..end];
        if (Expand(macro, dialect) is null)
        {
            problems.Add(new CronProblem(
                TextSpan.FromBounds(start, end), $"{Name(dialect)} has no macro named '{macro}'."));
        }

        return new CronParse(dialect, [], problems.ToImmutable(), macro);
    }

    /// <summary>
    /// Quartz's rule that exactly one of the two day fields defers to the other.
    /// </summary>
    /// <remarks>
    /// Checked after the fields are read because it is the only rule about two of them at once.
    /// Cronos has no such rule — it reads two restricted day fields as "either" — so this is
    /// reported only where it is an error rather than everywhere it looks unusual.
    /// </remarks>
    private static void Days(
        ImmutableArray<CronField>.Builder fields, CronDialect dialect,
        ImmutableArray<CronProblem>.Builder problems)
    {
        if (dialect != CronDialect.Quartz)
            return;

        CronField? month = null;
        CronField? week = null;
        foreach (var field in fields)
        {
            if (field.Kind == CronFieldKind.DayOfMonth)
                month = field;
            else if (field.Kind == CronFieldKind.DayOfWeek)
                week = field;
        }

        if (month is not { } dayOfMonth || week is not { } dayOfWeek)
            return;

        bool monthDefers = Defers(dayOfMonth);
        bool weekDefers = Defers(dayOfWeek);

        if (!monthDefers && !weekDefers)
        {
            problems.Add(new CronProblem(
                dayOfWeek.Span,
                "Quartz reads only one of the day fields. Write '?' here or in the day of month."));
        }
        else if (monthDefers && weekDefers && Both(dayOfMonth, dayOfWeek))
        {
            problems.Add(new CronProblem(
                dayOfWeek.Span, "Both day fields say '?', so neither names a day. One of them must."));
        }

        static bool Defers(CronField field) =>
            field.Terms.Length == 1
            && field.Terms[0].Kind is CronTermKind.Any or CronTermKind.All
            && field.Terms[0].Step is null;

        static bool Both(CronField month, CronField week) =>
            month.Terms[0].Kind == CronTermKind.Any && week.Terms[0].Kind == CronTermKind.Any;
    }

    private static List<TextSpan> Split(string text, int start, int end)
    {
        var spans = new List<TextSpan>(8);
        int i = start;

        while (i < end)
        {
            while (i < end && char.IsWhiteSpace(text[i]))
                i++;
            if (i >= end)
                break;

            int from = i;
            while (i < end && !char.IsWhiteSpace(text[i]))
                i++;
            spans.Add(TextSpan.FromBounds(from, i));
        }

        return spans;
    }

    private static CronField Field(
        string text, TextSpan span, CronFieldKind kind, CronDialect dialect,
        ImmutableArray<CronProblem>.Builder problems)
    {
        var terms = ImmutableArray.CreateBuilder<CronTerm>();

        int i = span.Start;
        while (true)
        {
            int from = i;
            while (i < span.End && text[i] != ',')
                i++;

            terms.Add(Term(text, TextSpan.FromBounds(from, i), kind, dialect, problems));

            if (i >= span.End)
                break;
            i++;
        }

        return new CronField(kind, span, terms.ToImmutable());
    }

    private static CronTerm Term(
        string text, TextSpan span, CronFieldKind kind, CronDialect dialect,
        ImmutableArray<CronProblem>.Builder problems)
    {
        string s = text.Substring(span.Start, span.Length);

        if (s.Length == 0)
        {
            problems.Add(new CronProblem(span, "There is nothing between these commas."));
            return new CronTerm(span, CronTermKind.Invalid);
        }

        int? step = null;
        int slash = s.IndexOf('/');
        if (slash >= 0)
        {
            string after = s[(slash + 1)..];
            if (!int.TryParse(after, out int every) || every <= 0)
            {
                problems.Add(new CronProblem(
                    span, $"'{after}' is not a number of {Unit(kind)} to step by."));
                return new CronTerm(span, CronTermKind.Invalid);
            }

            var (min, max) = RangeOf(kind, dialect);
            if (every > max - min + 1)
            {
                problems.Add(new CronProblem(
                    span,
                    $"Stepping by {every} never comes round again; {Unit(kind)} run {min} to {max}."));
            }

            step = every;
            s = s[..slash];
        }

        return Base(span, s, kind, dialect, step, problems);
    }

    private static CronTerm Base(
        TextSpan span, string s, CronFieldKind kind, CronDialect dialect, int? step,
        ImmutableArray<CronProblem>.Builder problems)
    {
        if (s == "*")
            return new CronTerm(span, CronTermKind.All, Step: step);

        if (s == "?")
        {
            if (!Allows(kind, '?'))
            {
                problems.Add(new CronProblem(
                    span,
                    $"'?' means 'the other day field decides', which {Unit(kind)} have no say in."));
                return new CronTerm(span, CronTermKind.Invalid);
            }

            return new CronTerm(span, CronTermKind.Any, Step: step);
        }

        if (s.EndsWith("W", StringComparison.OrdinalIgnoreCase)
            && !s.Equals("LW", StringComparison.OrdinalIgnoreCase))
        {
            return Weekday(span, s, kind, dialect, problems);
        }

        if (s.EndsWith("L", StringComparison.OrdinalIgnoreCase)
            || s.Equals("LW", StringComparison.OrdinalIgnoreCase))
        {
            return Last(span, s, kind, dialect, problems);
        }

        int hash = s.IndexOf('#');
        if (hash >= 0)
            return Nth(span, s, hash, kind, dialect, problems);

        // From index 1, so a negative year — which nothing accepts, but which someone will write —
        // is read as one bad value rather than as an empty range.
        int dash = s.IndexOf('-', 1);
        if (dash > 0)
            return Range(span, s, dash, kind, dialect, step, problems);

        if (Value(s, kind, dialect) is not { } value)
        {
            problems.Add(new CronProblem(span, $"'{s}' is not {Article(kind)}."));
            return new CronTerm(span, CronTermKind.Invalid);
        }

        Check(span, value, kind, dialect, problems);
        return new CronTerm(span, CronTermKind.Value, From: value, Step: step);
    }

    private static CronTerm Weekday(
        TextSpan span, string s, CronFieldKind kind, CronDialect dialect,
        ImmutableArray<CronProblem>.Builder problems)
    {
        if (!Allows(kind, 'W'))
        {
            problems.Add(new CronProblem(
                span, "'W' means 'the nearest weekday to', which only a day of the month has."));
            return new CronTerm(span, CronTermKind.Invalid);
        }

        if (Value(s[..^1], kind, dialect) is not { } day)
        {
            problems.Add(new CronProblem(span, $"'{s}' is not a day of the month followed by 'W'."));
            return new CronTerm(span, CronTermKind.Invalid);
        }

        Check(span, day, kind, dialect, problems);
        return new CronTerm(span, CronTermKind.Weekday, From: day, Marker: 'W');
    }

    private static CronTerm Last(
        TextSpan span, string s, CronFieldKind kind, CronDialect dialect,
        ImmutableArray<CronProblem>.Builder problems)
    {
        if (!Allows(kind, 'L'))
        {
            problems.Add(new CronProblem(
                span,
                $"'L' counts back from the end of the month, which {Unit(kind)} do not."));
            return new CronTerm(span, CronTermKind.Invalid);
        }

        if (s.Equals("LW", StringComparison.OrdinalIgnoreCase))
        {
            if (kind != CronFieldKind.DayOfMonth)
            {
                problems.Add(new CronProblem(span, "'LW' is a day of the month, not a day of the week."));
                return new CronTerm(span, CronTermKind.Invalid);
            }

            return new CronTerm(span, CronTermKind.Last, Marker: 'W');
        }

        if (s.Length == 1)
            return new CronTerm(span, CronTermKind.Last);

        // '5L' is the last Friday of the month, and only the day-of-week field can say it: 'L' in a
        // day of month already means the last day, so a number in front of it would name two days.
        if (kind != CronFieldKind.DayOfWeek)
        {
            problems.Add(new CronProblem(
                span, $"'{s}' would name both a day and the last day. Write 'L' on its own."));
            return new CronTerm(span, CronTermKind.Invalid);
        }

        if (Value(s[..^1], kind, dialect) is not { } day)
        {
            problems.Add(new CronProblem(span, $"'{s}' is not a day of the week followed by 'L'."));
            return new CronTerm(span, CronTermKind.Invalid);
        }

        Check(span, day, kind, dialect, problems);
        return new CronTerm(span, CronTermKind.Last, From: day, Marker: 'L');
    }

    private static CronTerm Nth(
        TextSpan span, string s, int hash, CronFieldKind kind, CronDialect dialect,
        ImmutableArray<CronProblem>.Builder problems)
    {
        if (!Allows(kind, '#'))
        {
            problems.Add(new CronProblem(
                span, $"'#' counts weeks within a month, which {Unit(kind)} do not."));
            return new CronTerm(span, CronTermKind.Invalid);
        }

        if (Value(s[..hash], kind, dialect) is not { } day
            || !int.TryParse(s[(hash + 1)..], out int nth))
        {
            problems.Add(new CronProblem(
                span, $"'{s}' is not a day of the week followed by '#' and a week."));
            return new CronTerm(span, CronTermKind.Invalid);
        }

        Check(span, day, kind, dialect, problems);

        // Five, not four: a month can hold five of any weekday. A '#5' in a month that holds only
        // four simply passes over, which is the behaviour rather than an error.
        if (nth is < 1 or > 5)
        {
            problems.Add(new CronProblem(
                span, $"A month has at most five of any weekday, so '#{nth}' never comes."));
        }

        return new CronTerm(span, CronTermKind.Nth, From: day, Nth: nth, Marker: '#');
    }

    private static CronTerm Range(
        TextSpan span, string s, int dash, CronFieldKind kind, CronDialect dialect, int? step,
        ImmutableArray<CronProblem>.Builder problems)
    {
        if (Value(s[..dash], kind, dialect) is not { } from
            || Value(s[(dash + 1)..], kind, dialect) is not { } to)
        {
            problems.Add(new CronProblem(span, $"'{s}' is not a range of {Unit(kind)}."));
            return new CronTerm(span, CronTermKind.Invalid);
        }

        Check(span, from, kind, dialect, problems);
        Check(span, to, kind, dialect, problems);

        // No complaint when the start is past the end: 'NOV-FEB' is a winter, and both readings
        // wrap it round the year rather than treating it as a mistake.
        return new CronTerm(span, CronTermKind.Range, From: from, To: to, Step: step);
    }

    /// <summary>A number, or one of the three-letter names the field accepts.</summary>
    private static int? Value(string s, CronFieldKind kind, CronDialect dialect)
    {
        if (s.Length == 0)
            return null;

        if (int.TryParse(s, out int number))
            return number;

        string[] names = kind switch
        {
            CronFieldKind.Month => s_monthNames,
            CronFieldKind.DayOfWeek => s_dayNames,
            _ => [],
        };

        for (int i = 0; i < names.Length; i++)
        {
            if (!s.Equals(names[i], StringComparison.OrdinalIgnoreCase))
                continue;

            // Names are written from January and from Sunday; what those are numbered is the
            // dialect's business, and for the day of week the two readings disagree.
            return kind == CronFieldKind.DayOfWeek ? ValueOf((DayOfWeek)i, dialect) : i + 1;
        }

        return null;
    }

    private static void Check(
        TextSpan span, int value, CronFieldKind kind, CronDialect dialect,
        ImmutableArray<CronProblem>.Builder problems)
    {
        if (!InRange(value, kind, dialect))
        {
            var (min, max) = RangeOf(kind, dialect);
            problems.Add(new CronProblem(
                span, $"{Capitalise(Unit(kind))} run {min} to {max}, and this says {value}."));
        }
    }

    private static bool InRange(int value, CronFieldKind kind, CronDialect dialect)
    {
        var (min, max) = RangeOf(kind, dialect);
        return value >= min && value <= max;
    }

    /// <summary>What the field counts, plural, for a message that reads as a sentence.</summary>
    public static string Unit(CronFieldKind kind) => kind switch
    {
        CronFieldKind.Second => "seconds",
        CronFieldKind.Minute => "minutes",
        CronFieldKind.Hour => "hours",
        CronFieldKind.DayOfMonth => "days of the month",
        CronFieldKind.Month => "months",
        CronFieldKind.DayOfWeek => "days of the week",
        CronFieldKind.Year => "years",
        _ => "values",
    };

    private static string Article(CronFieldKind kind) => kind switch
    {
        CronFieldKind.Second => "a second",
        CronFieldKind.Minute => "a minute",
        CronFieldKind.Hour => "an hour",
        CronFieldKind.DayOfMonth => "a day of the month",
        CronFieldKind.Month => "a month",
        CronFieldKind.DayOfWeek => "a day of the week",
        CronFieldKind.Year => "a year",
        _ => "a value",
    };

    /// <summary>The library's name, for a message that says whose rule was broken.</summary>
    public static string Name(CronDialect dialect) => dialect switch
    {
        CronDialect.Hangfire => "Hangfire",
        CronDialect.Quartz => "Quartz",
        _ => "A crontab schedule",
    };

    private static string Counts(CronDialect dialect) =>
        dialect == CronDialect.Quartz ? "six or seven fields" : "five or six fields";

    private static string Capitalise(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
