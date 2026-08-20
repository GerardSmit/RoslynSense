using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.Formatting;

/// <summary>
/// Which grammar a specifier is read with — the same characters mean different things.
/// </summary>
/// <remarks>
/// <c>MM</c> is a two-digit month to a date and literal text to a number; <c>E</c> is scientific
/// notation to a number and literal text to a date. The family is what the value being formatted
/// says, and <see cref="FormatFamily.Unknown"/> is the honest answer when nothing said.
/// </remarks>
internal enum FormatFamily
{
    /// <summary>Nothing named the value. Resolved per specifier — see <see cref="FormatString"/>.</summary>
    Unknown,
    Date,
    Number,
}

/// <summary>What a piece of a format specifier stands for.</summary>
internal enum FormatPartKind
{
    Literal,

    /// <summary>The whole specifier is one of the runtime's named patterns — <c>d</c>, <c>N2</c>.</summary>
    Standard,

    Year,
    Month,
    Day,
    Hour,
    Minute,
    Second,
    SubSecond,
    Meridiem,
    Era,
    TimeZone,
    Digit,
    DecimalSeparator,
    GroupSeparator,
    Percent,
    PerMille,
    Exponent,
    Escape,
}

/// <summary>One run of a specifier that means one thing.</summary>
internal readonly record struct FormatPart(TextSpan Span, FormatPartKind Kind, string Text);

/// <summary>One component a specifier can be built from, as completion offers it.</summary>
internal readonly record struct FormatComponent(string Text, string Description);

/// <summary>
/// One <c>{…}</c> of a composite format string.
/// </summary>
/// <param name="Span">The whole hole, braces included.</param>
/// <param name="Index">The argument it formats, or -1 for a hole whose index is not a number.</param>
/// <param name="NameSpan">Where the index or expression is written, for colouring it.</param>
/// <param name="Specifier">The text after the colon, or an empty span when there is none.</param>
internal readonly record struct FormatHole(
    TextSpan Span, int Index, string Name, TextSpan NameSpan, TextSpan Alignment, TextSpan Specifier);

/// <summary>
/// The grammar of a composite format string and of the specifiers inside it.
/// </summary>
/// <remarks>
/// Written once and driven from both hosts it appears in. <c>DataFormatString="{0:dd-MM-yyyy}"</c>
/// is markup text and <c>$"{DateTime.Now:yyyyMMdd}"</c> is a Roslyn interpolated string; they are
/// different syntax trees holding the same language, and a reader who learns what <c>MM</c> means
/// in one has learned it for the other only if both answer the same way.
/// <para>
/// Deliberately not a validator. What it produces is structure — the colouring and the worked
/// example are what make the structure worth having, and both are useful on a specifier this
/// parser could not have proved correct.
/// </para>
/// </remarks>
internal static class FormatString
{
    /// <summary>
    /// The date every worked example is rendered from.
    /// </summary>
    /// <remarks>
    /// Chosen so that no two components share a value: the day is not the month, the month is not
    /// the hour, and the year's last two digits are neither. An example rendered from a date where
    /// they collide is one a reader cannot check.
    /// </remarks>
    private static readonly DateTime s_reference = new(2026, 3, 27, 14, 5, 9, 123);

    /// <summary>The number every worked example is rendered from, for the same reason.</summary>
    private static readonly decimal s_number = 1234.5678m;

    /// <summary>The integer the specifiers that reject a fraction — <c>D</c>, <c>X</c> — use.</summary>
    private const long IntegerReference = 1234;

    /// <summary>The holes of a composite format string, in the order they are written.</summary>
    public static ImmutableArray<FormatHole> Holes(string text)
    {
        var holes = ImmutableArray.CreateBuilder<FormatHole>();

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '{')
                continue;

            // `{{` is an escaped brace and holds nothing.
            if (i + 1 < text.Length && text[i + 1] == '{')
            {
                i++;
                continue;
            }

            int close = Close(text, i);
            if (close < 0)
                break;

            holes.Add(Hole(text, i, close));
            i = close;
        }

        return holes.ToImmutable();
    }

    /// <summary>The hole containing <paramref name="offset"/>, or null.</summary>
    public static FormatHole? HoleAt(ImmutableArray<FormatHole> holes, int offset)
    {
        foreach (var hole in holes)
        {
            if (offset >= hole.Span.Start && offset <= hole.Span.End)
                return hole;
        }

        return null;
    }

    /// <summary>
    /// The closing brace of the hole opening at <paramref name="open"/>, or -1.
    /// </summary>
    /// <remarks>
    /// Nesting is counted because an interpolated string may hold one inside its own expression —
    /// <c>$"{(flag ? $"{x:d}" : "")}"</c> — and stopping at the first brace would cut the outer
    /// hole in half.
    /// </remarks>
    private static int Close(string text, int open)
    {
        int depth = 0;

        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}' && --depth == 0)
                return i;
        }

        return -1;
    }

    private static FormatHole Hole(string text, int open, int close)
    {
        int colon = -1;
        int comma = -1;
        int depth = 0;

        for (int i = open + 1; i < close; i++)
        {
            char c = text[i];

            if (c is '{' or '(' or '[')
                depth++;
            else if (c is '}' or ')' or ']')
                depth--;
            else if (depth == 0 && c == ',' && comma < 0)
                comma = i;
            else if (depth == 0 && c == ':')
            {
                colon = i;
                break;
            }
        }

        int nameEnd = colon >= 0 ? colon : close;
        if (comma >= 0 && comma < nameEnd)
            nameEnd = comma;

        string name = text[(open + 1)..nameEnd].Trim();

        return new FormatHole(
            TextSpan.FromBounds(open, close + 1),
            int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int index) ? index : -1,
            name,
            TextSpan.FromBounds(open + 1, nameEnd),
            comma >= 0
                ? TextSpan.FromBounds(comma + 1, colon >= 0 ? colon : close)
                : new TextSpan(nameEnd, 0),
            colon >= 0
                ? TextSpan.FromBounds(colon + 1, close)
                : new TextSpan(close, 0));
    }

    /// <summary>
    /// A specifier split into the runs that mean something, with everything else left literal.
    /// </summary>
    /// <remarks>
    /// Offsets are relative to <paramref name="specifier"/>, so a caller holding the specifier's
    /// span in a larger buffer adds its start.
    /// </remarks>
    public static ImmutableArray<FormatPart> Parts(
        string specifier, FormatFamily family = FormatFamily.Unknown)
    {
        if (specifier.Length == 0)
            return [];

        var resolved = Resolve(family, specifier);

        // A standard specifier names a whole pattern rather than describing one, so it has no
        // components to split into: `N2` is not an N and a 2.
        if (Standard(specifier, resolved) is not null)
            return [new FormatPart(new TextSpan(0, specifier.Length), FormatPartKind.Standard, specifier)];

        var parts = ImmutableArray.CreateBuilder<FormatPart>();
        int i = 0;

        while (i < specifier.Length)
        {
            char c = specifier[i];

            // `\` escapes the next character and `'…'` quotes a run of them; both are literal text
            // that happens to be spelled with format characters.
            if (c == '\\' && i + 1 < specifier.Length)
            {
                parts.Add(new FormatPart(new TextSpan(i, 2), FormatPartKind.Escape, specifier[i..(i + 2)]));
                i += 2;
                continue;
            }

            if (c is '\'' or '"')
            {
                int end = specifier.IndexOf(c, i + 1);
                int stop = end < 0 ? specifier.Length : end + 1;
                parts.Add(new FormatPart(
                    TextSpan.FromBounds(i, stop), FormatPartKind.Escape, specifier[i..stop]));
                i = stop;
                continue;
            }

            var kind = Kind(c, resolved);
            if (kind == FormatPartKind.Literal)
            {
                int start = i;
                while (i < specifier.Length
                       && Kind(specifier[i], resolved) == FormatPartKind.Literal
                       && specifier[i] is not ('\\' or '\'' or '"'))
                {
                    i++;
                }

                parts.Add(new FormatPart(
                    TextSpan.FromBounds(start, i), FormatPartKind.Literal, specifier[start..i]));
                continue;
            }

            // A run of one character is one component: `MM` is a two-digit month, not two months.
            int runStart = i;
            while (i < specifier.Length && specifier[i] == c)
                i++;

            parts.Add(new FormatPart(
                TextSpan.FromBounds(runStart, i), kind, specifier[runStart..i]));
        }

        return parts.ToImmutable();
    }

    /// <summary>The component containing <paramref name="offset"/>, or null.</summary>
    public static FormatPart? PartAt(ImmutableArray<FormatPart> parts, int offset)
    {
        foreach (var part in parts)
        {
            if (offset >= part.Span.Start && offset < part.Span.End)
                return part;
        }

        return null;
    }

    /// <summary>
    /// Which grammar to read a specifier with when the value did not say.
    /// </summary>
    /// <remarks>
    /// Date first, because it is the grammar where letters mean something and therefore the one
    /// where a wrong guess costs the reader the most. A specifier holding digit placeholders and no
    /// date letter — <c>#,##0.00</c> — could only be a number, and that is the one case worth
    /// deciding without being told.
    /// </remarks>
    private static FormatFamily Resolve(FormatFamily family, string specifier)
    {
        if (family != FormatFamily.Unknown)
            return family;

        // One character is a standard specifier in both grammars, and inside a composite hole the
        // date reading is overwhelmingly the one meant.
        if (specifier.Length == 1)
            return FormatFamily.Date;

        foreach (char c in specifier)
        {
            if (Kind(c, FormatFamily.Date) != FormatPartKind.Literal)
                return FormatFamily.Date;
        }

        return FormatFamily.Number;
    }

    private static FormatPartKind Kind(char c, FormatFamily family) =>
        family == FormatFamily.Number
            ? c switch
            {
                '0' or '#' => FormatPartKind.Digit,
                '.' => FormatPartKind.DecimalSeparator,
                ',' => FormatPartKind.GroupSeparator,
                '%' => FormatPartKind.Percent,
                '‰' => FormatPartKind.PerMille,
                'E' or 'e' => FormatPartKind.Exponent,
                _ => FormatPartKind.Literal,
            }
            : c switch
            {
                'y' => FormatPartKind.Year,
                'M' => FormatPartKind.Month,
                'd' => FormatPartKind.Day,
                'h' or 'H' => FormatPartKind.Hour,
                'm' => FormatPartKind.Minute,
                's' => FormatPartKind.Second,
                'f' or 'F' => FormatPartKind.SubSecond,
                't' => FormatPartKind.Meridiem,
                'g' => FormatPartKind.Era,
                'K' or 'z' => FormatPartKind.TimeZone,
                _ => FormatPartKind.Literal,
            };

    /// <summary>
    /// The name of the standard pattern <paramref name="specifier"/> is, or null when it is custom.
    /// </summary>
    /// <remarks>
    /// The runtime's own rule, and the reason it matters: a date specifier is standard only when it
    /// is exactly one character, so <c>{0:d}</c> is the short date pattern while <c>{0:dd}</c> is a
    /// two-digit day. Reading both as days would give the first a worked example that is wrong.
    /// </remarks>
    public static string? Standard(string specifier, FormatFamily family = FormatFamily.Unknown)
    {
        if (specifier.Length == 0)
            return null;

        return Resolve(family, specifier) == FormatFamily.Number
            ? NumberStandard(specifier)
            : specifier.Length == 1 ? DateStandard(specifier[0]) : null;
    }

    private static string? DateStandard(char c) => c switch
    {
        'd' => "Short date",
        'D' => "Long date",
        'f' => "Long date, short time",
        'F' => "Long date, long time",
        'g' => "Short date, short time",
        'G' => "Short date, long time",
        'M' or 'm' => "Month and day",
        'O' or 'o' => "Round-trip, ISO 8601",
        'R' or 'r' => "RFC 1123",
        's' => "Sortable, ISO 8601",
        't' => "Short time",
        'T' => "Long time",
        'u' => "Universal sortable",
        'U' => "Universal full",
        'Y' or 'y' => "Year and month",
        _ => null,
    };

    /// <summary>
    /// A standard numeric specifier: one letter and an optional precision.
    /// </summary>
    private static string? NumberStandard(string specifier)
    {
        for (int i = 1; i < specifier.Length; i++)
        {
            if (!char.IsAsciiDigit(specifier[i]))
                return null;
        }

        string? name = char.ToUpperInvariant(specifier[0]) switch
        {
            'C' => "Currency",
            'D' => "Decimal, integers only",
            'E' => "Scientific",
            'F' => "Fixed-point",
            'G' => "General",
            'N' => "Number, with thousands separators",
            'P' => "Per cent, and the value multiplied by 100",
            'R' => "Round-trip",
            'X' => "Hexadecimal",
            'B' => "Binary",
            _ => null,
        };

        if (name is null || specifier.Length == 1)
            return name;

        string precision = specifier[1..];
        return char.ToUpperInvariant(specifier[0]) is 'D' or 'X' or 'B'
            ? $"{name}, padded to {precision} digits"
            : $"{name}, {precision} decimal places";
    }

    /// <summary>What one component stands for, in words.</summary>
    public static string Describe(FormatPart part, FormatFamily family = FormatFamily.Unknown) =>
        part.Kind switch
    {
        FormatPartKind.Standard => Standard(part.Text, family) ?? "Standard pattern",
        FormatPartKind.Year => part.Text.Length >= 4 ? "Year, four digits" : "Year, two digits",
        FormatPartKind.Month => part.Text.Length switch
        {
            1 => "Month, no leading zero",
            2 => "Month, two digits",
            3 => "Month, short name",
            _ => "Month, full name",
        },
        FormatPartKind.Day => part.Text.Length switch
        {
            1 => "Day of the month, no leading zero",
            2 => "Day of the month, two digits",
            3 => "Day of the week, short name",
            _ => "Day of the week, full name",
        },
        FormatPartKind.Hour =>
            (part.Text[0] == 'H' ? "Hour, 24-hour clock" : "Hour, 12-hour clock")
            + (part.Text.Length >= 2 ? ", two digits" : ", no leading zero"),
        FormatPartKind.Minute => part.Text.Length >= 2 ? "Minute, two digits" : "Minute, no leading zero",
        FormatPartKind.Second => part.Text.Length >= 2 ? "Second, two digits" : "Second, no leading zero",
        FormatPartKind.SubSecond => $"Fractional second, {part.Text.Length} digit(s)",
        FormatPartKind.Meridiem => part.Text.Length >= 2 ? "AM or PM" : "A or P",
        FormatPartKind.Era => "Era",
        FormatPartKind.TimeZone => "Time zone offset",
        FormatPartKind.Digit => part.Text[0] == '0' ? "Digit, zero-padded" : "Digit, omitted when zero",
        FormatPartKind.DecimalSeparator => "Decimal separator",
        FormatPartKind.GroupSeparator => "Thousands separator",
        FormatPartKind.Percent => "Per cent, and the value multiplied by 100",
        FormatPartKind.PerMille => "Per mille, and the value multiplied by 1000",
        FormatPartKind.Exponent => "Scientific notation",
        FormatPartKind.Escape => "Literal text",
        _ => "Literal text",
    };

    /// <summary>
    /// The components worth offering for a family, in the order a reader would look for them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-written rather than derived from <see cref="Kind"/>, because what completion offers is
    /// not the alphabet but the <i>useful spellings</i> of it: <c>M</c>, <c>MM</c>, <c>MMM</c> and
    /// <c>MMMM</c> are one character with four meanings, and a list built from the characters would
    /// offer the character and leave the reader to guess how many to type.
    /// </para>
    /// <para>
    /// An unknown family gets the date list. Nothing said what the value is, and a specifier nobody
    /// annotated is a date far more often than it is anything else — offering the numeric
    /// placeholders instead would put <c>#,##0.00</c> in front of someone formatting a timestamp.
    /// </para>
    /// </remarks>
    public static ImmutableArray<FormatComponent> Components(FormatFamily family) =>
        family == FormatFamily.Number ? s_numberComponents : s_dateComponents;

    private static readonly ImmutableArray<FormatComponent> s_dateComponents =
    [
        new("dd", "Day of the month, two digits"),
        new("ddd", "Day of the week, short name"),
        new("dddd", "Day of the week, full name"),
        new("MM", "Month, two digits"),
        new("MMM", "Month, short name"),
        new("MMMM", "Month, full name"),
        new("yy", "Year, two digits"),
        new("yyyy", "Year, four digits"),
        new("HH", "Hour, 24-hour clock, two digits"),
        new("hh", "Hour, 12-hour clock, two digits"),
        new("mm", "Minute, two digits"),
        new("ss", "Second, two digits"),
        new("fff", "Fractional second, three digits"),
        new("tt", "AM or PM"),
        new("zzz", "Time zone offset"),
    ];

    private static readonly ImmutableArray<FormatComponent> s_numberComponents =
    [
        new("0", "Digit, zero-padded"),
        new("#", "Digit, omitted when zero"),
        new("0.00", "Two decimal places, always shown"),
        new("#,##0", "Thousands separators, no decimals"),
        new("#,##0.00", "Thousands separators, two decimal places"),
        new("N2", "Number, with thousands separators, two decimals"),
        new("C2", "Currency, two decimals"),
        new("P1", "Per cent, one decimal"),
        new("F2", "Fixed-point, two decimals"),
        new("D5", "Integer, padded to five digits"),
        new("E3", "Scientific, three decimals"),
    ];

    /// <summary>
    /// What a specifier produces, rendered from a fixed value so the reader can check it.
    /// </summary>
    /// <remarks>
    /// Null when the runtime will not accept the specifier, which is the honest answer: an example
    /// invented for a specifier that throws would be worse than none. The invariant culture is used
    /// deliberately — the example is about the specifier, and a month name in the reader's own
    /// locale would suggest the format string carries one.
    /// </remarks>
    public static string? Example(string specifier, FormatFamily family = FormatFamily.Unknown)
    {
        if (string.IsNullOrEmpty(specifier))
            return null;

        return Render(specifier, Resolve(family, specifier));
    }

    private static string? Render(string specifier, FormatFamily family)
    {
        try
        {
            if (family == FormatFamily.Number)
            {
                try
                {
                    return s_number.ToString(specifier, CultureInfo.InvariantCulture);
                }
                catch (FormatException)
                {
                    // `D` and `X` are defined for integers only, and a fractional reference is not
                    // a reason to tell the reader the specifier is wrong.
                    return IntegerReference.ToString(specifier, CultureInfo.InvariantCulture);
                }
            }

            // A one-character custom specifier is read as a standard one by ToString, which is not
            // what it means inside a composite hole; the runtime's own rule is to prefix `%`.
            string form = specifier.Length == 1 && DateStandard(specifier[0]) is null
                ? "%" + specifier
                : specifier;

            return s_reference.ToString(form, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
