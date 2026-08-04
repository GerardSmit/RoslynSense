using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.Proto.Core;

/// <summary>What a <see cref="ProtoToken"/> is.</summary>
internal enum ProtoTokenKind
{
    EndOfFile,
    Identifier,
    Number,
    String,
    OpenBrace,
    CloseBrace,
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    Less,
    Greater,
    Semicolon,
    Comma,
    Equals,
    Dot,
    Minus,
    Plus,
    Colon,
    Slash,

    /// <summary>A character that belongs to no proto token. Left for the parser to report, so that
    /// one stray byte produces one diagnostic where it was written instead of a cascade.</summary>
    Unknown,
}

/// <summary>One lexical token.</summary>
/// <param name="Span">The token's own extent, trivia excluded.</param>
/// <param name="LeadingCommentSpan">The comment block attached to this token, or the default span.
/// Only the parser knows which tokens begin declarations, so the lexer records where the comment
/// is and leaves turning it into documentation until something asks.</param>
/// <param name="Value">The decoded text of a string literal, escapes resolved. Null for every
/// other kind: an identifier's text is read from the <see cref="SourceText"/> only where it is
/// actually needed, which keeps keyword matching allocation-free.</param>
internal readonly record struct ProtoToken(
    ProtoTokenKind Kind,
    TextSpan Span,
    TextSpan LeadingCommentSpan,
    string? Value);

/// <summary>
/// Turns <c>.proto</c> source into a flat token array.
/// </summary>
/// <remarks>
/// <para>
/// One pass over the buffer, producing the whole array up front rather than a pull-based stream.
/// The grammar needs lookahead in several places that a single token cannot cover — <c>map</c>
/// is only the map keyword when a <c>&lt;</c> follows, <c>optional</c> is only a label when a type
/// name follows — and an array makes that a bounds-checked index instead of a buffer of pushed-back
/// tokens.
/// </para>
/// <para>
/// Nothing here throws. An unterminated string or block comment is a diagnostic and the token ends
/// where the input does, because the file is being typed and the closing quote is simply not there
/// yet.
/// </para>
/// </remarks>
internal static class ProtoLexer
{
    public static ImmutableArray<ProtoToken> Lex(
        SourceText text, ImmutableArray<ProtoParseDiagnostic>.Builder diagnostics)
    {
        int length = text.Length;

        // Roughly one token every five characters in real protos; sizing up front avoids the
        // repeated array doublings that dominate the parse of a large file.
        var tokens = ImmutableArray.CreateBuilder<ProtoToken>(Math.Max(16, length / 5));

        int position = 0;
        bool atLineStart = true;

        while (true)
        {
            var comment = ScanTrivia(text, diagnostics, ref position, ref atLineStart);

            if (position >= length)
            {
                tokens.Add(new ProtoToken(ProtoTokenKind.EndOfFile, new TextSpan(length, 0), comment, null));
                break;
            }

            tokens.Add(ScanToken(text, diagnostics, ref position) with { LeadingCommentSpan = comment });
            atLineStart = false;
        }

        return tokens.ToImmutable();
    }

    /// <summary>
    /// Every comment in the file, in source order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="Lex"/> because the token stream deliberately keeps only the block
    /// attached to the token after it — that is what documentation is — which leaves out the note
    /// at the end of a field's line and the licence header at the top of the file. Colouring and
    /// folding want all of them.
    /// </para>
    /// <para>
    /// Found in the stretches <i>between</i> tokens, which by construction hold nothing but
    /// whitespace and comments: a <c>//</c> written inside a string literal belongs to that
    /// literal's token, and an option value is very often a URL, so a text search for <c>//</c>
    /// would fold the rest of the file away from the middle of one. An unterminated block runs to
    /// the end of the input, which is where the lexer stopped looking too.
    /// </para>
    /// </remarks>
    public static IEnumerable<TextSpan> Comments(SourceText text) =>
        Comments(text, Lex(text, ImmutableArray.CreateBuilder<ProtoParseDiagnostic>()));

    /// <inheritdoc cref="Comments(SourceText)"/>
    /// <param name="tokens">The lex of <paramref name="text"/>, for a caller that already has one.
    /// Semantic tokens colours the comments and the keywords in one pass and would otherwise lex
    /// the buffer twice per keystroke.</param>
    public static IEnumerable<TextSpan> Comments(SourceText text, ImmutableArray<ProtoToken> tokens)
    {
        int gap = 0;

        foreach (var token in tokens)
        {
            for (int position = gap; position < token.Span.Start; )
            {
                if (text[position] != '/' || position + 1 >= token.Span.Start)
                {
                    position++;
                    continue;
                }

                int end = CommentEnd(text, position, token.Span.Start);
                if (end < 0)
                {
                    position++;
                    continue;
                }

                yield return TextSpan.FromBounds(position, end);
                position = end;
            }

            gap = token.Span.End;
        }
    }

    /// <summary>Where the comment starting at <paramref name="position"/> ends, or -1 when the
    /// slash starts no comment at all.</summary>
    private static int CommentEnd(SourceText text, int position, int limit)
    {
        int end = position + 2;

        switch (text[position + 1])
        {
            case '/':
                while (end < limit && text[end] != '\n')
                    end++;

                return end;

            case '*':
                while (end < limit && !(text[end] == '*' && end + 1 < limit && text[end + 1] == '/'))
                    end++;

                return Math.Min(end + 2, limit);

            default:
                return -1;
        }
    }

    /// <summary>
    /// Turns an attached comment block into documentation: comment markers gone, one leading space
    /// gone with them, blank edges trimmed. <c>null</c> when nothing survives.
    /// </summary>
    public static string? ExtractDocumentation(SourceText text, TextSpan span)
    {
        if (span.IsEmpty)
            return null;

        string raw = text.ToString(span);
        var builder = new StringBuilder(raw.Length);

        int start = 0;
        while (start <= raw.Length)
        {
            int newLine = raw.IndexOf('\n', start);
            int end = newLine < 0 ? raw.Length : newLine;

            var line = raw.AsSpan(start, end - start).TrimEnd('\r').TrimStart();
            line = StripMarkers(line);

            // One leading space and no more: it is the space that separates `//` from the text,
            // and stripping further would flatten the indentation of a code sample in the comment.
            if (line.Length > 0 && line[0] == ' ')
                line = line[1..];

            if (builder.Length > 0)
                builder.Append('\n');

            builder.Append(line.TrimEnd());

            if (newLine < 0)
                break;

            start = newLine + 1;
        }

        string documentation = builder.ToString().Trim();
        return documentation.Length == 0 ? null : documentation;
    }

    /// <summary>
    /// Strips one line's comment markers. The closing <c>*/</c> goes first, because a line that is
    /// nothing but the closer would otherwise be read as a <c>*</c> continuation marker followed by
    /// a stray slash.
    /// </summary>
    private static ReadOnlySpan<char> StripMarkers(ReadOnlySpan<char> line)
    {
        if (line.EndsWith("*/"))
            line = line[..^2].TrimEnd();

        if (line.StartsWith("///"))
            line = line[3..];
        else if (line.StartsWith("//"))
            line = line[2..];
        else if (line.StartsWith("/**"))
            line = line[3..];
        else if (line.StartsWith("/*"))
            line = line[2..];
        else if (line.StartsWith("*"))
            line = line[1..];

        return line;
    }

    /// <summary>
    /// Skips whitespace and comments, and returns the comment block that belongs to the token that
    /// follows.
    /// </summary>
    /// <remarks>
    /// Two rules decide what "belongs" means, and both exist because the alternative attaches the
    /// wrong text to a declaration. A comment that starts on a line where code has already been
    /// written is a trailing note about that code — <c>int64 id = 1; // the key</c> — so it never
    /// becomes the next declaration's documentation. And a blank line between the comment and the
    /// declaration breaks the association, which is how a file-header comment stops being read as
    /// documentation for whatever happens to be declared first.
    /// </remarks>
    private static TextSpan ScanTrivia(
        SourceText text,
        ImmutableArray<ProtoParseDiagnostic>.Builder diagnostics,
        ref int position,
        ref bool atLineStart)
    {
        int length = text.Length;
        int commentStart = -1;
        int commentEnd = -1;
        int newLines = 0;
        bool lineStart = atLineStart;

        while (position < length)
        {
            char c = text[position];

            if (c == '\n')
            {
                position++;
                lineStart = true;

                if (commentStart >= 0 && ++newLines > 1)
                {
                    commentStart = -1;
                    commentEnd = -1;
                    newLines = 0;
                }

                continue;
            }

            if (c is ' ' or '\t' or '\r' or '\f' or '\v')
            {
                position++;
                continue;
            }

            if (c != '/' || position + 1 >= length)
                break;

            char next = text[position + 1];
            int start = position;
            bool attaches = lineStart;

            if (next == '/')
            {
                position += 2;
                while (position < length && text[position] != '\n')
                    position++;
            }
            else if (next == '*')
            {
                position += 2;
                bool closed = false;

                while (position < length)
                {
                    if (text[position] == '*' && position + 1 < length && text[position + 1] == '/')
                    {
                        position += 2;
                        closed = true;
                        break;
                    }

                    position++;
                }

                if (!closed)
                {
                    diagnostics.Add(new ProtoParseDiagnostic(
                        ProtoDiagnosticIds.UnterminatedComment,
                        "Unterminated block comment.",
                        TextSpan.FromBounds(start, length),
                        ProtoDiagnosticSeverity.Error));
                }

                lineStart = false;
            }
            else
            {
                break;
            }

            if (attaches)
            {
                if (commentStart < 0)
                    commentStart = start;

                commentEnd = position;
                newLines = 0;
            }
        }

        atLineStart = lineStart;
        return commentStart < 0 ? default : TextSpan.FromBounds(commentStart, commentEnd);
    }

    private static ProtoToken ScanToken(
        SourceText text, ImmutableArray<ProtoParseDiagnostic>.Builder diagnostics, ref int position)
    {
        int length = text.Length;
        int start = position;
        char c = text[position];

        if (IsIdentifierStart(c))
        {
            position++;
            while (position < length && IsIdentifierPart(text[position]))
                position++;

            return Token(ProtoTokenKind.Identifier, start, position);
        }

        if (c is >= '0' and <= '9')
        {
            ScanNumber(text, ref position);
            return Token(ProtoTokenKind.Number, start, position);
        }

        if (c is '"' or '\'')
        {
            string value = ScanString(text, diagnostics, ref position);
            return new ProtoToken(ProtoTokenKind.String, TextSpan.FromBounds(start, position), default, value);
        }

        position++;

        var kind = c switch
        {
            '{' => ProtoTokenKind.OpenBrace,
            '}' => ProtoTokenKind.CloseBrace,
            '(' => ProtoTokenKind.OpenParen,
            ')' => ProtoTokenKind.CloseParen,
            '[' => ProtoTokenKind.OpenBracket,
            ']' => ProtoTokenKind.CloseBracket,
            '<' => ProtoTokenKind.Less,
            '>' => ProtoTokenKind.Greater,
            ';' => ProtoTokenKind.Semicolon,
            ',' => ProtoTokenKind.Comma,
            '=' => ProtoTokenKind.Equals,
            '.' => ProtoTokenKind.Dot,
            '-' => ProtoTokenKind.Minus,
            '+' => ProtoTokenKind.Plus,
            ':' => ProtoTokenKind.Colon,
            '/' => ProtoTokenKind.Slash,
            _ => ProtoTokenKind.Unknown,
        };

        return Token(kind, start, position);
    }

    private static ProtoToken Token(ProtoTokenKind kind, int start, int end) =>
        new(kind, TextSpan.FromBounds(start, end), default, null);

    /// <summary>
    /// Consumes a numeric literal: decimal, <c>0x…</c> hex, leading-zero octal, and floats with an
    /// exponent. The value is left in the source — only field numbers are ever read as integers,
    /// and reading those from the buffer costs no allocation.
    /// </summary>
    private static void ScanNumber(SourceText text, ref int position)
    {
        int length = text.Length;

        if (text[position] == '0' && position + 1 < length && (text[position + 1] is 'x' or 'X'))
        {
            position += 2;
            while (position < length && IsHexDigit(text[position]))
                position++;

            return;
        }

        while (position < length && text[position] is >= '0' and <= '9')
            position++;

        if (position < length && text[position] == '.')
        {
            position++;
            while (position < length && text[position] is >= '0' and <= '9')
                position++;
        }

        if (position < length && text[position] is 'e' or 'E')
        {
            int exponent = position + 1;

            if (exponent < length && text[exponent] is '+' or '-')
                exponent++;

            // Only an exponent that actually has digits belongs to the number; `1e` is a number
            // followed by an identifier, and swallowing the `e` would lose the identifier.
            if (exponent < length && text[exponent] is >= '0' and <= '9')
            {
                position = exponent;
                while (position < length && text[position] is >= '0' and <= '9')
                    position++;
            }
        }
    }

    /// <summary>
    /// Consumes a quoted literal and returns its decoded value. The closing quote must match the
    /// opening one, and a newline ends the literal: a run-on string would otherwise swallow the
    /// rest of the file the moment a quote is typed.
    /// </summary>
    private static string ScanString(
        SourceText text, ImmutableArray<ProtoParseDiagnostic>.Builder diagnostics, ref int position)
    {
        int length = text.Length;
        char quote = text[position];
        int start = position;
        position++;

        int literalStart = position;
        StringBuilder? decoded = null;

        while (position < length)
        {
            char c = text[position];

            if (c == quote)
            {
                string value = decoded?.ToString()
                    ?? text.ToString(TextSpan.FromBounds(literalStart, position));

                position++;
                return value;
            }

            if (c == '\n')
                break;

            if (c != '\\')
            {
                decoded?.Append(c);
                position++;
                continue;
            }

            decoded ??= AppendPrefix(text, literalStart, position);
            position++;
            AppendEscape(text, decoded, ref position);
        }

        diagnostics.Add(new ProtoParseDiagnostic(
            ProtoDiagnosticIds.UnterminatedString,
            "Unterminated string literal.",
            TextSpan.FromBounds(start, position),
            ProtoDiagnosticSeverity.Error));

        return decoded?.ToString() ?? text.ToString(TextSpan.FromBounds(literalStart, position));
    }

    private static StringBuilder AppendPrefix(SourceText text, int literalStart, int position)
    {
        var builder = new StringBuilder(position - literalStart + 16);

        for (int i = literalStart; i < position; i++)
            builder.Append(text[i]);

        return builder;
    }

    private static void AppendEscape(SourceText text, StringBuilder decoded, ref int position)
    {
        int length = text.Length;

        if (position >= length)
            return;

        char c = text[position++];

        switch (c)
        {
            case 'a': decoded.Append('\a'); return;
            case 'b': decoded.Append('\b'); return;
            case 'f': decoded.Append('\f'); return;
            case 'n': decoded.Append('\n'); return;
            case 'r': decoded.Append('\r'); return;
            case 't': decoded.Append('\t'); return;
            case 'v': decoded.Append('\v'); return;
            case '\\': decoded.Append('\\'); return;
            case '\'': decoded.Append('\''); return;
            case '"': decoded.Append('"'); return;
            case '?': decoded.Append('?'); return;

            case 'x' or 'X':
                decoded.Append((char)ReadHex(text, ref position, maxDigits: 2));
                return;

            case 'u':
                decoded.Append((char)ReadHex(text, ref position, maxDigits: 4));
                return;

            case 'U':
            {
                int value = ReadHex(text, ref position, maxDigits: 8);
                if (value is >= 0 and <= 0x10FFFF)
                    decoded.Append(char.ConvertFromUtf32(value));
                return;
            }

            case >= '0' and <= '7':
            {
                int value = c - '0';
                for (int i = 0; i < 2 && position < length && text[position] is >= '0' and <= '7'; i++)
                    value = (value * 8) + (text[position++] - '0');

                decoded.Append((char)value);
                return;
            }

            default:
                // An unknown escape keeps both characters. protoc rejects it, but the parser's job
                // here is to survive and let the diagnostic come from somewhere that knows better.
                decoded.Append('\\').Append(c);
                return;
        }
    }

    private static int ReadHex(SourceText text, ref int position, int maxDigits)
    {
        int length = text.Length;
        int value = 0;
        int digits = 0;

        while (digits < maxDigits && position < length && IsHexDigit(text[position]))
        {
            value = (value * 16) + HexValue(text[position]);
            position++;
            digits++;
        }

        return value;
    }

    private static bool IsIdentifierStart(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_';

    private static bool IsIdentifierPart(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_';

    private static bool IsHexDigit(char c) =>
        c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static int HexValue(char c) =>
        c <= '9' ? c - '0' : (char.ToLowerInvariant(c) - 'a') + 10;
}
