using System.Text;

namespace RoslynMCP.Languages.DotSettings.Core;

/// <summary>
/// The <c>_XXXX</c> escaping ReSharper puts on every value that appears inside a settings key.
/// </summary>
/// <remarks>
/// <para>
/// A key is a path, and the things named along it — file names, folder names, coverage filters —
/// contain the characters a path is made of. So everything outside <c>[A-Za-z0-9]</c> is written
/// as an underscore followed by the four hex digits of its UTF-16 code unit, which is why a real
/// file turns up in the file as
/// <c>App_005FModule_005FUI_002Ff_003AEditor_002Eascx</c>. Unescaped, that is
/// <c>App_Module_UI/f:Editor.ascx</c>.
/// </para>
/// <para>
/// The underscore encodes itself (<c>_005F</c>), so decoding is unambiguous left to right: an
/// underscore is always the start of an escape. A trailing underscore with fewer than four hex
/// digits after it cannot have been written by ReSharper, and is passed through rather than
/// rejected — these files are hand-edited often enough that a merge artifact should cost one
/// unreadable name, not the whole file.
/// </para>
/// </remarks>
internal static class DotSettingsEscaping
{
    /// <summary>The escaped form back to the text it stands for.</summary>
    public static string Decode(string encoded)
    {
        if (encoded.IndexOf('_') < 0)
            return encoded;

        var builder = new StringBuilder(encoded.Length);

        for (int i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '_'
                && i + 4 < encoded.Length
                && TryHex(encoded.AsSpan(i + 1, 4), out char decoded))
            {
                builder.Append(decoded);
                i += 4;
                continue;
            }

            builder.Append(encoded[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The text as a key segment. Present so a writer round-trips, and so a test can state the
    /// pairing in one direction and assert it in the other.
    /// </summary>
    public static string Encode(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (char ch in text)
        {
            if (char.IsAsciiLetterOrDigit(ch))
                builder.Append(ch);
            else
                builder.Append('_').Append(((int)ch).ToString("X4"));
        }

        return builder.ToString();
    }

    private static bool TryHex(ReadOnlySpan<char> span, out char value)
    {
        int result = 0;

        foreach (char ch in span)
        {
            int digit = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'A' and <= 'F' => ch - 'A' + 10,
                >= 'a' and <= 'f' => ch - 'a' + 10,
                _ => -1,
            };

            if (digit < 0)
            {
                value = '\0';
                return false;
            }

            result = (result << 4) | digit;
        }

        value = (char)result;
        return true;
    }
}
