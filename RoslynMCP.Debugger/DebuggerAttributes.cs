using System.Runtime.InteropServices;
using System.Text;
using ClrDebug;

namespace RoslynMCP.Debugger;

/// <summary>How <c>DebuggerBrowsableAttribute</c> says a member should appear.</summary>
/// <remarks>Mirrors <c>System.Diagnostics.DebuggerBrowsableState</c>. Declared here rather than
/// used from the BCL because the values arrive as raw integers out of the debuggee's metadata,
/// and naming them at the parse boundary is what makes the rest of the code readable.</remarks>
public enum BrowsableState
{
    Never = 0,

    /// <summary>Listed, expanded on demand. The default for everything with no attribute.</summary>
    Collapsed = 2,

    /// <summary>The member itself is not listed; its children are listed in its place. How
    /// <c>List&lt;T&gt;</c> shows its elements instead of an <c>_items</c> array.</summary>
    RootHidden = 3,
}

/// <summary>
/// Reads the <c>System.Diagnostics</c> debugger attributes out of a debuggee's own metadata.
/// </summary>
/// <remarks>
/// <para>
/// The attributes cannot be read reflectively — they live in the target process, not this one —
/// so each is fetched as a raw custom-attribute blob through <c>IMetaDataImport</c> and decoded
/// according to ECMA-335 II.23.3. Only the shapes these particular attributes use are decoded:
/// a single string or int32 constructor argument, plus named string arguments.
/// </para>
/// <para>
/// Every lookup is by attribute name rather than by resolved type, because the attribute's type
/// lives in another assembly and resolving a TypeRef across modules would cost far more than a
/// string comparison the metadata engine already indexes.
/// </para>
/// </remarks>
public static class DebuggerAttributes
{
    public const string Display = "System.Diagnostics.DebuggerDisplayAttribute";
    public const string TypeProxy = "System.Diagnostics.DebuggerTypeProxyAttribute";
    public const string Browsable = "System.Diagnostics.DebuggerBrowsableAttribute";
    public const string StepThrough = "System.Diagnostics.DebuggerStepThroughAttribute";
    public const string Hidden = "System.Diagnostics.DebuggerHiddenAttribute";
    public const string NonUserCode = "System.Diagnostics.DebuggerNonUserCodeAttribute";

    /// <summary>The attributes that mark a method as somebody else's code.</summary>
    public static readonly string[] StepOverMarkers = [StepThrough, Hidden, NonUserCode];

    /// <summary>Whether <paramref name="token"/> carries the named attribute at all.</summary>
    public static bool Has(MetaDataImport metadata, mdToken token, string attributeName) =>
        metadata.TryGetCustomAttributeByName(token, attributeName, out _) == HRESULT.S_OK;

    /// <summary>
    /// The named attribute's raw blob, or null when the attribute is absent or carries no
    /// arguments.
    /// </summary>
    public static byte[]? Blob(MetaDataImport metadata, mdToken token, string attributeName)
    {
        if (metadata.TryGetCustomAttributeByName(token, attributeName, out var result) != HRESULT.S_OK)
            return null;
        if (result.ppData == IntPtr.Zero || result.pcbData <= 0)
            return null;

        var blob = new byte[result.pcbData];
        Marshal.Copy(result.ppData, blob, 0, result.pcbData);
        return blob;
    }

    /// <summary>The single string constructor argument of an attribute like
    /// <c>DebuggerDisplay</c> or <c>DebuggerTypeProxy</c>.</summary>
    public static string? StringArgument(MetaDataImport metadata, mdToken token, string attributeName)
    {
        var blob = Blob(metadata, token, attributeName);
        return blob is null ? null : ReadStringArgument(blob);
    }

    /// <summary>A <c>DebuggerDisplay</c> in full: the value format, plus the <c>Name</c> and
    /// <c>Type</c> named arguments a collection view uses to relabel its entry rows.</summary>
    public sealed record DisplayAttribute(string? Value, string? Name, string? Type);

    /// <summary>Decodes a token's <c>DebuggerDisplay</c>, named arguments included.</summary>
    public static DisplayAttribute? DisplayOf(MetaDataImport metadata, mdToken token)
    {
        var blob = Blob(metadata, token, Display);
        if (blob is null || blob.Length < 3 || blob[0] != 0x01 || blob[1] != 0x00)
            return null;

        var offset = 2;
        var value = ReadSerString(blob, ref offset);

        string? name = null;
        string? type = null;
        if (offset + 2 <= blob.Length)
        {
            // NumNamed (u2), then per named argument: kind byte (0x53 field / 0x54 property),
            // element type byte (0x0E = string), then the name and value SerStrings.
            var named = blob[offset] | (blob[offset + 1] << 8);
            offset += 2;
            for (var i = 0; i < named && offset + 2 <= blob.Length; i++)
            {
                var kind = blob[offset++];
                var element = blob[offset++];
                if (kind is not (0x53 or 0x54) || element != 0x0E)
                    break;
                var argumentName = ReadSerString(blob, ref offset);
                var argumentValue = ReadSerString(blob, ref offset);
                if (argumentName == "Name")
                    name = argumentValue;
                else if (argumentName == "Type")
                    type = argumentValue;
            }
        }

        return new DisplayAttribute(value, name, type);
    }

    /// <summary>
    /// The browsable state declared on a field or property, defaulting to
    /// <see cref="BrowsableState.Collapsed"/> when the attribute is absent.
    /// </summary>
    public static BrowsableState BrowsableOf(MetaDataImport metadata, mdToken token)
    {
        var blob = Blob(metadata, token, Browsable);
        if (blob is null || blob.Length < 6)
            return BrowsableState.Collapsed;

        // Prolog (2 bytes) then the enum's int32 value.
        var state = BitConverter.ToInt32(blob, 2);
        return state switch
        {
            0 => BrowsableState.Never,
            3 => BrowsableState.RootHidden,
            _ => BrowsableState.Collapsed,
        };
    }

    /// <summary>
    /// Decodes the leading <c>SerString</c> constructor argument of a custom-attribute blob.
    /// </summary>
    /// <remarks>
    /// A <c>Type</c>-typed argument — which is how <c>[DebuggerTypeProxy(typeof(View))]</c> is
    /// usually written — is stored exactly the same way, as the type's name in a string, so both
    /// constructor overloads decode through this one path.
    /// </remarks>
    public static string? ReadStringArgument(byte[] blob)
    {
        // 0x0001 prolog, then the argument.
        if (blob.Length < 3 || blob[0] != 0x01 || blob[1] != 0x00)
            return null;

        var offset = 2;
        return ReadSerString(blob, ref offset);
    }

    /// <summary>
    /// Reads an ECMA-335 <c>SerString</c>: a compressed length, then UTF-8 bytes. Advances
    /// <paramref name="offset"/> past it.
    /// </summary>
    private static string? ReadSerString(byte[] blob, ref int offset)
    {
        if (offset >= blob.Length)
            return null;

        // 0xFF is the null string, distinct from a zero-length one.
        if (blob[offset] == 0xFF)
        {
            offset++;
            return null;
        }

        if (!TryReadCompressedUInt(blob, ref offset, out var length))
            return null;
        if (length < 0 || offset + length > blob.Length)
            return null;

        var value = Encoding.UTF8.GetString(blob, offset, length);
        offset += length;
        return value;
    }

    private static bool TryReadCompressedUInt(byte[] blob, ref int offset, out int value)
    {
        value = 0;
        if (offset >= blob.Length)
            return false;

        var first = blob[offset];
        if ((first & 0x80) == 0)
        {
            value = first;
            offset += 1;
            return true;
        }
        if ((first & 0xC0) == 0x80)
        {
            if (offset + 1 >= blob.Length)
                return false;
            value = ((first & 0x3F) << 8) | blob[offset + 1];
            offset += 2;
            return true;
        }
        if ((first & 0xE0) == 0xC0)
        {
            if (offset + 3 >= blob.Length)
                return false;
            value = ((first & 0x1F) << 24) | (blob[offset + 1] << 16) | (blob[offset + 2] << 8) | blob[offset + 3];
            offset += 4;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Strips a stored type name down to the namespace-qualified name <c>IMetaDataImport</c>
    /// indexes: no assembly qualification, no generic argument list.
    /// </summary>
    /// <remarks>
    /// <c>[DebuggerTypeProxy(typeof(Mine&lt;&gt;.View))]</c> stores something like
    /// <c>"Ns.Mine`1+View, MyAsm, Version=..."</c>; the metadata table holds <c>Ns.Mine`1+View</c>
    /// keyed on the nested name alone, so both tails have to come off.
    /// </remarks>
    public static string NormalizeTypeName(string typeName)
    {
        var name = typeName.Trim();

        // Assembly qualification: the first comma that is not inside a generic argument list.
        var depth = 0;
        for (var i = 0; i < name.Length; i++)
        {
            switch (name[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    name = name[..i];
                    i = name.Length;
                    break;
            }
        }

        // Generic argument list, which the TypeDef name never carries.
        var bracket = name.IndexOf('[');
        if (bracket >= 0)
            name = name[..bracket];

        return name.Trim();
    }
}

/// <summary>One piece of a parsed <c>DebuggerDisplay</c> format string.</summary>
/// <param name="Text">Literal text, or the expression when <paramref name="IsExpression"/>.</param>
/// <param name="IsExpression">Whether <paramref name="Text"/> must be evaluated in the debuggee.</param>
/// <param name="NoQuotes">The <c>,nq</c> suffix: render a resulting string without its quotes.</param>
public readonly record struct DisplayPart(string Text, bool IsExpression, bool NoQuotes);

/// <summary>
/// Splits a <c>DebuggerDisplay</c> format string into literals and the expressions between
/// braces.
/// </summary>
/// <remarks>
/// The format is not composite formatting: <c>{Count}</c> names a member of the object being
/// displayed, and the only format specifier the debugger recognises is a comma-prefixed suffix,
/// of which <c>nq</c> ("no quotes") is the one that changes the output. Doubled braces escape.
/// </remarks>
public static class DebuggerDisplayFormat
{
    public static IReadOnlyList<DisplayPart> Parse(string format)
    {
        var parts = new List<DisplayPart>();
        var literal = new StringBuilder();

        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];

            if (c == '{' && i + 1 < format.Length && format[i + 1] == '{')
            {
                literal.Append('{');
                i++;
                continue;
            }
            if (c == '}' && i + 1 < format.Length && format[i + 1] == '}')
            {
                literal.Append('}');
                i++;
                continue;
            }

            if (c != '{')
            {
                literal.Append(c);
                continue;
            }

            var close = FindClosingBrace(format, i);
            if (close < 0)
            {
                // Unbalanced: the rest is literal, which is what a user staring at the string
                // would expect to see rather than the whole display silently disappearing.
                literal.Append(format[i..]);
                break;
            }

            if (literal.Length > 0)
            {
                parts.Add(new DisplayPart(literal.ToString(), IsExpression: false, NoQuotes: false));
                literal.Clear();
            }

            var (expression, noQuotes) = SplitSpecifier(format[(i + 1)..close]);
            if (expression.Length > 0)
                parts.Add(new DisplayPart(expression, IsExpression: true, noQuotes));
            i = close;
        }

        if (literal.Length > 0)
            parts.Add(new DisplayPart(literal.ToString(), IsExpression: false, NoQuotes: false));

        return parts;
    }

    /// <summary>Matches nesting, so <c>{Items[0].Name}</c> and a braced lambda both survive.</summary>
    private static int FindClosingBrace(string format, int open)
    {
        var depth = 0;
        for (var i = open; i < format.Length; i++)
        {
            if (format[i] == '{')
                depth++;
            else if (format[i] == '}' && --depth == 0)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Separates the expression from its trailing format specifier, honouring <c>nq</c> and
    /// discarding the rest (<c>d</c>, <c>hidden</c>, <c>raw</c> — display hints this engine has
    /// nothing to do with).
    /// </summary>
    private static (string Expression, bool NoQuotes) SplitSpecifier(string inner)
    {
        var depth = 0;
        for (var i = inner.Length - 1; i >= 0; i--)
        {
            switch (inner[i])
            {
                case ']':
                case ')':
                    depth++;
                    break;
                case '[':
                case '(':
                    depth--;
                    break;
                case ',' when depth == 0:
                    var specifier = inner[(i + 1)..].Trim();
                    return (inner[..i].Trim(),
                        specifier.Equals("nq", StringComparison.OrdinalIgnoreCase));
            }
        }
        return (inner.Trim(), false);
    }
}
