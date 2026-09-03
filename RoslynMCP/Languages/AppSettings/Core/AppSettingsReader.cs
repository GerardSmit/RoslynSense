using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.AppSettings.Core;

/// <summary>How a property's value is shaped, which decides what a key can mean: an object is a
/// section, everything else is a leaf the binder converts.</summary>
internal enum AppSettingsValueKind
{
    Object,
    Array,
    String,
    Number,
    Boolean,
    Null,
}

/// <summary>
/// One property in a configuration JSON file, located.
/// </summary>
/// <param name="Path">The configuration path the runtime would use for this property —
/// colon-joined, array elements by index (<c>Logging:LogLevel:Default</c>). Case preserved as
/// written; comparisons are case-insensitive, as the runtime's are.</param>
/// <param name="Name">The property's own name, decoded.</param>
/// <param name="NameSpan">The name in the raw text, quotes excluded.</param>
/// <param name="ValueSpan">The whole value — for an object or array, brace to brace.</param>
internal sealed record AppSettingsKey(
    string Path,
    string Name,
    TextSpan NameSpan,
    TextSpan ValueSpan,
    AppSettingsValueKind Kind,
    int Depth);

/// <summary>
/// Reads the key structure out of an <c>appsettings.json</c>-family file, with spans.
/// </summary>
/// <remarks>
/// Hand-rolled rather than <see cref="System.Text.Json.Utf8JsonReader"/> for the one thing that
/// reader cannot give: UTF-16 offsets into the buffer the editor is holding, which is what every
/// span here has to be. The dialect is what the configuration host accepts — comments, both styles,
/// and trailing commas — and the reading is tolerant the way an editor has to be: a truncated or
/// half-typed document answers with the keys that are recognizable rather than with nothing.
/// </remarks>
internal static class AppSettingsReader
{
    public static ImmutableArray<AppSettingsKey> Read(string text)
    {
        var keys = ImmutableArray.CreateBuilder<AppSettingsKey>();
        int position = 0;

        SkipTrivia(text, ref position);
        if (position < text.Length && text[position] == '{')
            ReadObject(text, ref position, parentPath: null, depth: 0, keys);

        return keys.ToImmutable();
    }

    private static void ReadObject(
        string text, ref int position, string? parentPath, int depth,
        ImmutableArray<AppSettingsKey>.Builder keys)
    {
        position++; // '{'

        while (true)
        {
            SkipTrivia(text, ref position);
            if (position >= text.Length)
                return;

            char c = text[position];
            if (c == '}')
            {
                position++;
                return;
            }

            if (c == ',')
            {
                position++;
                continue;
            }

            if (c != '"')
            {
                // Not a property and not the end: a half-typed name, or damage. Step past one
                // character so a malformed document cannot loop, and try again — the next quote
                // may open a perfectly good property.
                position++;
                continue;
            }

            var (name, nameSpan) = ReadString(text, ref position);

            SkipTrivia(text, ref position);
            if (position >= text.Length || text[position] != ':')
                continue; // A name with no value yet — the property being typed right now.

            position++; // ':'
            SkipTrivia(text, ref position);
            if (position >= text.Length)
                return;

            string path = parentPath is null ? name : parentPath + ":" + name;
            int valueStart = position;
            var kind = ReadValue(text, ref position, path, depth, keys);

            keys.Add(new AppSettingsKey(
                path, name, nameSpan, TextSpan.FromBounds(valueStart, position), kind, depth));
        }
    }

    private static void ReadArray(
        string text, ref int position, string parentPath, int depth,
        ImmutableArray<AppSettingsKey>.Builder keys)
    {
        position++; // '['
        int index = 0;

        while (true)
        {
            SkipTrivia(text, ref position);
            if (position >= text.Length)
                return;

            char c = text[position];
            if (c == ']')
            {
                position++;
                return;
            }

            if (c == ',')
            {
                position++;
                continue;
            }

            // The runtime flattens an element to "<parent>:<index>". The element gets no key
            // entry of its own — there is no name to hang a span on — but an object element's
            // properties do, under the indexed path.
            ReadValue(text, ref position, parentPath + ":" + index, depth, keys);
            index++;
        }
    }

    private static AppSettingsValueKind ReadValue(
        string text, ref int position, string path, int depth,
        ImmutableArray<AppSettingsKey>.Builder keys)
    {
        char c = text[position];

        switch (c)
        {
            case '{':
                ReadObject(text, ref position, path, depth + 1, keys);
                return AppSettingsValueKind.Object;

            case '[':
                ReadArray(text, ref position, path, depth + 1, keys);
                return AppSettingsValueKind.Array;

            case '"':
                ReadString(text, ref position);
                return AppSettingsValueKind.String;

            default:
                int start = position;
                while (position < text.Length && !IsValueTerminator(text[position]))
                    position++;

                string literal = text[start..position].TrimEnd();
                return literal is "true" or "false" ? AppSettingsValueKind.Boolean
                    : literal is "null" ? AppSettingsValueKind.Null
                    : AppSettingsValueKind.Number;
        }
    }

    private static bool IsValueTerminator(char c) =>
        c is ',' or '}' or ']' or '\r' or '\n' or '/';

    /// <summary>Reads a string starting at an opening quote; the span excludes the quotes.</summary>
    private static (string Value, TextSpan Span) ReadString(string text, ref int position)
    {
        position++; // opening '"'
        int start = position;
        StringBuilder? decoded = null;

        while (position < text.Length)
        {
            char c = text[position];

            if (c == '"')
            {
                var span = TextSpan.FromBounds(start, position);
                position++;
                return (decoded?.ToString() ?? text[start..span.End], span);
            }

            if (c == '\\' && position + 1 < text.Length)
            {
                decoded ??= new StringBuilder(text[start..position]);
                position++;
                char escape = text[position];

                decoded.Append(escape switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'b' => '\b',
                    'f' => '\f',
                    'u' when position + 4 < text.Length
                        && ushort.TryParse(text.AsSpan(position + 1, 4),
                            System.Globalization.NumberStyles.HexNumber, null, out ushort code)
                        => (char)code,
                    _ => escape,
                });

                if (escape == 'u' && position + 4 < text.Length)
                    position += 4;

                position++;
                continue;
            }

            if (c is '\r' or '\n')
            {
                // An unterminated string — the line the user is typing on. What was read is the
                // value so far.
                return (decoded?.ToString() ?? text[start..position], TextSpan.FromBounds(start, position));
            }

            decoded?.Append(c);
            position++;
        }

        return (decoded?.ToString() ?? text[start..], TextSpan.FromBounds(start, text.Length));
    }

    /// <summary>Whitespace plus both comment styles — the dialect the configuration host reads.</summary>
    private static void SkipTrivia(string text, ref int position)
    {
        while (position < text.Length)
        {
            char c = text[position];

            if (char.IsWhiteSpace(c))
            {
                position++;
                continue;
            }

            if (c == '/' && position + 1 < text.Length)
            {
                if (text[position + 1] == '/')
                {
                    while (position < text.Length && text[position] is not ('\n' or '\r'))
                        position++;
                    continue;
                }

                if (text[position + 1] == '*')
                {
                    position += 2;
                    while (position + 1 < text.Length
                        && !(text[position] == '*' && text[position + 1] == '/'))
                    {
                        position++;
                    }

                    position = Math.Min(position + 2, text.Length);
                    continue;
                }
            }

            return;
        }
    }
}
