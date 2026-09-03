namespace RoslynMCP.Debugger;

/// <summary>
/// Reads what is needed from a method's metadata signature blob.
/// </summary>
/// <remarks>
/// <para>
/// Written because the metadata interfaces hand back a signature as raw bytes and no reader for
/// them. Only the parameter count is decoded, which is all it takes to tell overloads apart —
/// <c>Assembly.LoadFrom</c> has one form taking a path and another taking a path and a hash, and
/// calling the wrong one in a debuggee fails in a way that looks like the assembly being at fault.
/// </para>
/// <para>
/// Resolving a full signature would mean walking types, which is a great deal of work to answer a
/// question that a single count already answers.
/// </para>
/// </remarks>
public static class MethodSignature
{
    /// <summary>Not a real count: what is reported when the blob cannot be read.</summary>
    public const int Unknown = -1;

    /// <summary>Set in the calling-convention byte when a generic parameter count follows it.</summary>
    private const byte Generic = 0x10;

    /// <summary>
    /// How many parameters a method signature declares.
    /// </summary>
    /// <remarks>
    /// ECMA-335 II.23.2.1: the calling convention, then the generic parameter count if the calling
    /// convention says there is one, then the parameter count. Both counts are compressed
    /// integers; only the one-byte form is read, because a method with 128 or more parameters is
    /// not one anybody is looking for here and <see cref="Unknown"/> declines rather than guesses.
    /// </remarks>
    /// <param name="signature">The blob, from the start of the calling convention.</param>
    public static int ParameterCount(ReadOnlySpan<byte> signature)
    {
        if (signature.Length < 2)
            return Unknown;

        var at = 1;
        if ((signature[0] & Generic) != 0)
        {
            if (at >= signature.Length || !IsSingleByte(signature[at]))
                return Unknown;
            at++;
        }

        if (at >= signature.Length || !IsSingleByte(signature[at]))
            return Unknown;

        return signature[at];
    }

    /// <summary>A compressed integer is one byte when its top bit is clear.</summary>
    private static bool IsSingleByte(byte value) => (value & 0x80) == 0;
}
