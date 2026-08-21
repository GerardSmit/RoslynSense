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
        if (Unwrapped(type) is not { } value)
            return FormatFamily.Unknown;

        // An enum's specifiers are its own small set (G, D, X, F) and none of them is a date or a
        // number pattern, so neither grammar describes it.
        if (value.TypeKind == TypeKind.Enum)
            return FormatFamily.Unknown;

        switch (value.SpecialType)
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

        return s_dateTypes.Contains(value.ToDisplayString(MemberSignature.DeclarationName))
            ? FormatFamily.Date
            : FormatFamily.Unknown;
    }

    /// <summary>
    /// What the value can be printed with, within its grammar.
    /// </summary>
    /// <remarks>
    /// The family says which language the specifier is written in; this says which of its words the
    /// value knows. Both distinctions are about the same failure: a specifier the value rejects
    /// does not print oddly, it throws — <c>D5</c> on a <c>double</c> and <c>HH</c> on a
    /// <c>DateOnly</c> are both a <c>FormatException</c> at run time, from a line that compiled.
    /// </remarks>
    public static FormatValueKind KindOf(ITypeSymbol? type)
    {
        if (Unwrapped(type) is not { } value)
            return FormatValueKind.Any;

        if (value.SpecialType is SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64)
        {
            return FormatValueKind.WholeNumber;
        }

        return value.ToDisplayString(MemberSignature.DeclarationName) switch
        {
            "System.DateOnly" => FormatValueKind.WithoutTime,
            "System.TimeOnly" => FormatValueKind.WithoutDate,
            _ => FormatValueKind.Any,
        };
    }

    /// <summary>
    /// The type behind a <c>Nullable&lt;T&gt;</c>, which formats exactly as its <c>T</c> does: the
    /// hole renders the value, and a null one renders as empty rather than as another grammar.
    /// </summary>
    private static ITypeSymbol? Unwrapped(ITypeSymbol? type) =>
        type is INamedTypeSymbol
        {
            OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
            TypeArguments.Length: 1,
        } nullable
            ? nullable.TypeArguments[0]
            : type;
}
