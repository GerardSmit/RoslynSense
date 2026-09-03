namespace RoslynMCP.Languages.Formatting;

/// <summary>
/// A colour per component of a format specifier, from names C# already has.
/// </summary>
/// <remarks>
/// <para>
/// The point is contrast rather than meaning. <c>dd-MM-yyyy</c> and <c>dd-mm-yyyy</c> are one
/// keystroke apart and produce a date and a nonsense date respectively, and the second is one of
/// those mistakes that survives every review because both look like a format string. Giving the
/// day, the month and the year three different colours makes the pair visibly different before
/// anyone reads them; what colour each one gets does not matter, only that its neighbours differ.
/// </para>
/// <para>
/// C#'s own token names, so a theme that already colours a method differently from a class colours
/// a month differently from a day with no configuration. See
/// <see cref="IEmbeddedSemanticTokensProvider"/> for why a name outside that legend is not an
/// option here, and <c>WebFormsLanguage.SemanticTokens</c> for the markup side, which reads the
/// same table so that a page and the code behind it colour a specifier alike.
/// </para>
/// </remarks>
internal static class FormatColours
{
    /// <summary>
    /// The legend name for a component, or null to leave it the colour of the string around it.
    /// </summary>
    /// <remarks>
    /// Neighbours are what the assignment is built around: the three date components that appear
    /// together take three separate names, and so do the three time components. Reuse across the
    /// two groups is deliberate and safe — a specifier writes <c>dd-MM-yyyy</c> or <c>HH:mm:ss</c>,
    /// and the pairs that would collide are never adjacent.
    /// </remarks>
    public static string? For(FormatPartKind kind) => kind switch
    {
        FormatPartKind.Standard => "keyword",

        FormatPartKind.Year => "number",
        FormatPartKind.Month => "method",
        FormatPartKind.Day => "class",

        FormatPartKind.Hour => "enumMember",
        FormatPartKind.Minute => "parameter",
        FormatPartKind.Second => "method",
        FormatPartKind.SubSecond => "class",

        FormatPartKind.Meridiem => "macro",
        FormatPartKind.Era => "macro",
        FormatPartKind.TimeZone => "macro",

        FormatPartKind.Digit => "number",
        FormatPartKind.Exponent => "macro",
        FormatPartKind.DecimalSeparator => "operator",
        FormatPartKind.GroupSeparator => "operator",
        FormatPartKind.Percent => "keyword",
        FormatPartKind.PerMille => "keyword",

        // Literal text prints as itself, and the string colour already says so.
        _ => null,
    };

    /// <summary>The braces and the colon of a hole, which are punctuation in both hosts.</summary>
    public const string Punctuation = "operator";

    /// <summary>The index or expression a hole names.</summary>
    public const string Value = "property";
}
