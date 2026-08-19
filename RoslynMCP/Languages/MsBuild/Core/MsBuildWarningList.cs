using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>
/// The diagnostic codes written into a suppression list, one span at a time.
/// </summary>
/// <remarks>
/// <c>&lt;NoWarn&gt;$(NoWarn);NU1605;CS0168&lt;/NoWarn&gt;</c> is one element value to the parser and
/// three separate things to a reader, only two of which mean anything. Everything a caret-addressed
/// feature needs about such a list — which properties hold one, and which code the caret is on —
/// lives here rather than in the hover handler, because completion and code actions on the same
/// spans are the obvious next things to want.
/// </remarks>
internal static class MsBuildWarningList
{
    /// <summary>
    /// The properties and metadata whose value is a list of diagnostic codes.
    /// </summary>
    /// <remarks>
    /// <c>TreatWarningsAsErrors</c> is deliberately absent: it takes a boolean, not codes.
    /// <c>NoWarn</c> appears both as a project property and as metadata on a
    /// <c>PackageReference</c> or <c>ProjectReference</c>, which needs no special case — the name
    /// is the same wherever it is written.
    /// </remarks>
    private static readonly HashSet<string> s_lists = new(StringComparer.OrdinalIgnoreCase)
    {
        "NoWarn",
        "WarningsAsErrors",
        "WarningsNotAsErrors",
        "MSBuildWarningsAsErrors",
        "MSBuildWarningsAsMessages",
        "MSBuildWarningsNotAsErrors",
    };

    /// <summary>Whether the caret is inside the value of one of those.</summary>
    public static bool IsWarningList(in MsBuildContext context)
    {
        if (context.Is(MsBuildLocationFlags.Attribute | MsBuildLocationFlags.Value))
            return context.AttributeName is { } attribute && s_lists.Contains(attribute);

        return context.Is(MsBuildLocationFlags.Element | MsBuildLocationFlags.Value)
            && s_lists.Contains(context.ElementName);
    }

    /// <summary>
    /// Whether the list is a property rather than metadata on an item.
    /// </summary>
    /// <remarks>
    /// The two are written the same way and reach the build differently. A property applies to the
    /// project — and, from a <c>Directory.Build.props</c>, to everything under it — and can be
    /// lifted for a moment to count what it hides. Metadata on a <c>PackageReference</c> applies to
    /// that one reference, and nothing outside the project file can lift it, so a count taken
    /// against it would be a count with the suppression still in force: a zero that reads as
    /// "delete this line" about a line that is working exactly as written.
    /// </remarks>
    public static bool IsProperty(in MsBuildContext context) =>
        context.Is(MsBuildLocationFlags.Element | MsBuildLocationFlags.Value)
        && !context.Path.Contains("ItemGroup/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The code the caret is on within a list value, and the span it occupies.
    /// </summary>
    /// <param name="text">The buffer.</param>
    /// <param name="value">The span of the whole list value.</param>
    /// <param name="offset">The caret.</param>
    /// <remarks>
    /// Null for <c>$(NoWarn)</c> and for anything else that is not shaped like a code — a property
    /// reference is the most common entry in a real <c>NoWarn</c>, and it means "whatever was
    /// already suppressed", which is a fact about the property rather than about a code. Declining
    /// it leaves the caller free to fall back to whatever it says about the property itself.
    /// </remarks>
    public static (string Code, TextSpan Span)? CodeAt(SourceText text, TextSpan value, int offset)
    {
        if (offset < value.Start || offset > value.End)
            return null;

        int start = offset;
        while (start > value.Start && !IsSeparator(text[start - 1]))
            start--;

        int end = offset;
        while (end < value.End && !IsSeparator(text[end]))
            end++;

        if (end <= start)
            return null;

        string token = text.ToString(TextSpan.FromBounds(start, end));
        return DiagnosticCodeCatalog.IsCode(token) ? (token, TextSpan.FromBounds(start, end)) : null;
    }

    private static bool IsSeparator(char c) => c is ';' or ',' || char.IsWhiteSpace(c);
}
