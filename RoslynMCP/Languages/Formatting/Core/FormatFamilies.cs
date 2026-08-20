using Microsoft.CodeAnalysis;
using RoslynMCP.Services.Symbols;

namespace RoslynMCP.Languages.Formatting.Core;

/// <summary>
/// Which grammar a value's type says its format specifier is written in.
/// </summary>
/// <remarks>
/// The only thing a symbol contributes to reading a format string, and it is worth the lookup:
/// <c>MM</c> is a two-digit month on a <c>DateTime</c> and two literal Ms on a <c>decimal</c>, so
/// without the type the editor is guessing at the difference between a date and a nonsense string.
/// </remarks>
internal static class FormatFamilies
{
    /// <summary>
    /// The types outside <see cref="SpecialType"/> that format like a date.
    /// </summary>
    /// <remarks>
    /// <c>TimeSpan</c> is deliberately absent. Its custom specifiers overlap the date ones without
    /// meaning the same thing — <c>d</c> is a count of days rather than a day of the month — and
    /// answering <see cref="FormatFamily.Unknown"/> leaves the reader with the colouring and
    /// without a description that would be wrong.
    /// </remarks>
    private static readonly string[] s_dateTypes =
    [
        "System.DateTimeOffset",
        "System.DateOnly",
        "System.TimeOnly",
    ];

    public static FormatFamily Of(ITypeSymbol? type)
    {
        if (type is null)
            return FormatFamily.Unknown;

        // `DateTime?` formats exactly as `DateTime` does — the hole renders the value, and a null
        // one renders as empty rather than as a different grammar.
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            && nullable.TypeArguments.Length == 1)
        {
            type = nullable.TypeArguments[0];
        }

        // An enum's specifiers are its own small set (G, D, X, F) and none of them is a date or a
        // number pattern, so neither grammar describes it.
        if (type.TypeKind == TypeKind.Enum)
            return FormatFamily.Unknown;

        switch (type.SpecialType)
        {
            case SpecialType.System_DateTime:
                return FormatFamily.Date;

            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Decimal:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
                return FormatFamily.Number;
        }

        return s_dateTypes.Contains(type.ToDisplayString(MemberSignature.DeclarationName))
            ? FormatFamily.Date
            : FormatFamily.Unknown;
    }
}
