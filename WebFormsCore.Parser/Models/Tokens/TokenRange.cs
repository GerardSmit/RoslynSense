using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace WebFormsCore.Models;

public readonly record struct TokenRange(string File, TokenPosition Start, TokenPosition End)
{
    public override string ToString()
    {
        return $"{Start} - {End}";
    }

    public bool Includes(int line, int column)
    {
        return (line > Start.Line || line == Start.Line && column >= Start.Column) &&
               (line < End.Line || line == End.Line && column <= End.Column);
    }

    public static implicit operator OffsetRange(TokenRange range)
    {
        return new OffsetRange(range.Start.Offset, range.End.Offset);
    }

    public TokenRange WithEnd(TokenPosition end)
    {
        return this with { End = end };
    }

    public static implicit operator TextSpan(TokenRange range) => new(range.Start.Offset, range.End.Offset - range.Start.Offset);

    public static implicit operator LinePositionSpan(TokenRange range) => new(range.Start, range.End);

    /// <summary>
    /// The range as a Roslyn location, or <see cref="Location.None"/> when it belongs to no file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A range with no file is ordinary rather than exceptional. <c>TokenString</c> converts
    /// implicitly from <c>string</c> and gives the result <c>default</c> for its range, so every
    /// synthesized name the parser handles — one it produced itself rather than read out of the
    /// markup — carries a range whose <c>File</c> is null. Reporting a diagnostic against one is
    /// then perfectly reasonable, and <c>Location.Create</c> rejects a null path.
    /// </para>
    /// <para>
    /// Guarded here because this operator is the single place every range becomes a location, so
    /// one check covers every diagnostic the parser can raise. It was found the hard way: a
    /// <c>PropertyNotFound</c> on one attribute threw out of the middle of parsing, which took down
    /// hover, folding, document symbols, semantic tokens, document links, code actions, code lens
    /// and diagnostics for that file at once — and for its code-behind too, since the C# lenses
    /// there ask this pack for markup references.
    /// </para>
    /// </remarks>
    public static implicit operator Location(TokenRange range) =>
        string.IsNullOrEmpty(range.File) ? Location.None : Location.Create(range.File, range, range);

    public TokenRange Slice(int offset)
    {
        return this with
        {
            Start = Start with
            {
                Column = Start.Column + offset,
                Offset = Start.Offset + offset
            }
        };
    }

    public TokenRange Slice(int offset, int length)
    {
        return this with
        {
            Start = Start with
            {
                Column = Start.Column + offset,
                Offset = Start.Offset + offset
            },
            End = Start with
            {
                Column = Start.Column + offset + length,
                Offset = Start.Offset + offset + length
            }
        };
    }
}
