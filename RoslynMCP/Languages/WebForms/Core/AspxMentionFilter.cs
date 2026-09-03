using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>
/// Decides, without parsing, whether a markup file could possibly mention a symbol.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AspxReferenceService.FindAsync"/> walks every markup file under a project and parses
/// each one to match it against the symbol. On a large site that is the wrong shape twice over: a
/// reference search parses a thousand trees to find the handful of files that name one control, and
/// a code lens does it again per lens. Parsing is by far the expensive half, so the cheap half runs
/// first and decides which files are worth it.
/// </para>
/// <para>
/// The test is a token set per file rather than a substring scan: the set is a fraction of the
/// file's size, so the index for a whole site stays small enough to keep, and a kept index means the
/// second lens on a page pays nothing at all. Tokens are maximal runs of letters, digits and
/// underscores, lowercased — which is what makes <c>Inherits="Site.DefaultPage"</c> yield
/// <c>defaultpage</c> and <c>&lt;asp:Button</c> yield <c>button</c>, matching the way
/// <see cref="AspxSymbolResolver"/> resolves them.
/// </para>
/// <para>
/// Conservative in exactly one direction. Every branch of <c>CollectMarkup</c> — and the binding
/// paths collected beside it, whose segments are tokens too — matches a name that
/// appears in the file as one of these tokens, so a file the filter rejects cannot have produced a
/// result — but anything the filter cannot describe (a symbol whose name is not an identifier, a
/// file that will not read, an open buffer) is admitted and parsed as before. A false positive
/// costs one parse; a false negative would be a missing reference, and a rename applies whatever
/// this search returns.
/// </para>
/// </remarks>
internal readonly struct AspxMentionFilter
{
    private sealed record IndexEntry(DateTime WriteTimeUtc, long Length, FrozenSet<string> Tokens);

    private static readonly ConcurrentDictionary<string, IndexEntry> s_index =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The symbol's own name, lowercased, or null when every file must be parsed.</summary>
    private readonly string? _token;

    /// <summary>
    /// The <c>On…</c> attribute an event is written as — <c>OnClick</c> for the event
    /// <c>Click</c> — which is the one case where a symbol's name reaches markup as part of a token
    /// rather than as the whole of one. See <see cref="AspxSymbolResolver.TryGetEvent"/>.
    /// </summary>
    private readonly string? _eventToken;

    private AspxMentionFilter(string? token, string? eventToken)
    {
        _token = token;
        _eventToken = eventToken;
    }

    public static AspxMentionFilter For(ISymbol symbol)
    {
        // A type is the one kind whose markup mention need not contain its name at all. The tag
        // is written under a registered prefix and tag name — `<uc:Address>` for a control whose
        // class is `AddressFormControl` — and a property element is written under the property's
        // name while `CollectMarkup` matches it against the property's *type*. Neither leaves a
        // token this filter could look for, and the results feed rename, so types opt out of the
        // filter entirely rather than being described approximately.
        if (symbol is ITypeSymbol)
            return new AspxMentionFilter(null, null);

        string name = symbol.Name;

        // A constructor, an indexer or an operator is named `.ctor`, `this[]` or `op_…`, and none
        // of those reaches markup as a token. Rather than reason about which, anything that is not
        // a plain identifier turns the filter off.
        if (name.Length == 0 || !IsTokenChar(name[0]) || char.IsDigit(name[0]))
            return new AspxMentionFilter(null, null);

        foreach (char c in name)
        {
            if (!IsTokenChar(c))
                return new AspxMentionFilter(null, null);
        }

        string token = name.ToLowerInvariant();
        return new AspxMentionFilter(token, symbol is IEventSymbol ? "on" + token : null);
    }

    /// <summary>Whether <paramref name="file"/> is worth parsing for this symbol.</summary>
    public bool MayMention(string file)
    {
        // An unsaved buffer is not what the index was built from, and there are only ever a few of
        // them open. Parsing those is already the memoized path.
        if (_token is null || OpenDocumentStore.IsOpen(file))
            return true;

        var tokens = TokensOf(file);
        return tokens is null
            || tokens.Contains(_token)
            || (_eventToken is not null && tokens.Contains(_eventToken));
    }

    /// <summary>
    /// The file's tokens, re-read when its stamp or its size moves and kept otherwise.
    /// </summary>
    /// <remarks>
    /// Write time and length together rather than write time alone: a branch switch can restore a
    /// file to a timestamp the index has already seen, and a stale token set would silently drop a
    /// file out of every search.
    /// </remarks>
    private static FrozenSet<string>? TokensOf(string file)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(file);
            if (!info.Exists)
                return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        if (s_index.TryGetValue(file, out var cached)
            && cached.WriteTimeUtc == info.LastWriteTimeUtc
            && cached.Length == info.Length)
        {
            return cached.Tokens;
        }

        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var tokens = Tokenize(text);
        s_index[file] = new IndexEntry(info.LastWriteTimeUtc, info.Length, tokens);
        return tokens;
    }

    private static FrozenSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && IsTokenChar(text[i]))
            {
                if (start < 0)
                    start = i;
                continue;
            }

            if (start >= 0)
            {
                tokens.Add(text.AsSpan(start, i - start).ToString().ToLowerInvariant());
                start = -1;
            }
        }

        return tokens.ToFrozenSet(StringComparer.Ordinal);
    }

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Drops the whole index. For tests that need a cold measurement.</summary>
    internal static void Clear() => s_index.Clear();
}
