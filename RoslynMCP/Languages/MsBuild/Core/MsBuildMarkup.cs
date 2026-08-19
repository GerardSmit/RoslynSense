using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>What the raw text says about a caret the tree could only place approximately.</summary>
/// <param name="Name">The element the caret is in, or the one being typed.</param>
/// <param name="OnName">Whether the caret is in an element name rather than in content.</param>
/// <param name="Whitespace">Whether the content is a place for a child rather than a value.</param>
/// <param name="Span">What a completion replaces.</param>
internal readonly record struct MsBuildMarkup(string Name, bool OnName, bool Whitespace, TextSpan Span);

/// <summary>
/// Reading a caret out of the characters, for the states a tree cannot describe.
/// </summary>
/// <remarks>
/// <para>
/// Completion runs on a half-typed file by definition, and half-typed XML is not XML. A start tag
/// whose end tag has not been typed yet is not an element to the parser at all: it recovers by
/// treating the text after it as content of the nearest ancestor that <em>does</em> close, so the
/// caret in <c>&lt;PropertyGroup&gt;&lt;LangVersion&gt;|</c> comes back as whitespace inside the
/// PropertyGroup — and the list offered there is every property name, in the one position where a
/// property name is the wrong answer.
/// </para>
/// <para>
/// The scan is deliberately small: back from the caret to the nearest <c>&lt;</c> or <c>&gt;</c>,
/// which is all it takes to tell "inside a tag" from "after one". It only ever runs where the tree
/// already gave up — a caret the parser placed inside real content is never second-guessed.
/// </para>
/// </remarks>
internal static class MsBuildMarkupScan
{
    /// <summary>
    /// What the caret is in, or null when the text says nothing the tree did not already say.
    /// </summary>
    /// <remarks>
    /// Null for the cases where the tree is right and this would be wrong: after an end tag or a
    /// self-closing one the caret really is in the parent's content, and inside a comment or a
    /// processing instruction there is nothing to complete.
    /// </remarks>
    public static MsBuildMarkup? Scan(SourceText text, int offset)
    {
        int i = offset - 1;
        while (i >= 0 && text[i] is not ('<' or '>'))
            i--;

        if (i < 0)
            return null;

        return text[i] == '<' ? InsideTag(text, i, offset) : AfterTag(text, i, offset);
    }

    /// <summary>The caret is between a <c>&lt;</c> and whatever will close it.</summary>
    /// <remarks>
    /// Only the name is claimed. Past the first space the caret is in the attribute region, which
    /// the tree describes perfectly well once the tag has a name — and which needs the element node
    /// this scan does not have.
    /// </remarks>
    private static MsBuildMarkup? InsideTag(SourceText text, int open, int offset)
    {
        for (int i = open + 1; i < offset; i++)
        {
            if (!IsNameChar(text[i]))
                return null;
        }

        // The whole run, not the half before the caret: replacing only the prefix leaves the rest
        // of what was typed welded to the accepted name.
        int end = offset;
        while (end < text.Length && IsNameChar(text[end]))
            end++;

        return new MsBuildMarkup(
            Text(text, open + 1, offset), OnName: true, Whitespace: false,
            TextSpan.FromBounds(open + 1, end));
    }

    /// <summary>The caret is after a <c>&gt;</c>, so it is in something's content.</summary>
    private static MsBuildMarkup? AfterTag(SourceText text, int close, int offset)
    {
        int open = close;
        while (open >= 0 && text[open] != '<')
            open--;

        // An end tag, an empty element, a comment or a declaration: the caret after any of them is
        // in the parent's content, which is exactly what the tree already said.
        if (open < 0 || close - open < 2 || text[open + 1] is '/' or '!' or '?' || text[close - 1] == '/')
            return null;

        int nameEnd = open + 1;
        while (nameEnd < close && IsNameChar(text[nameEnd]))
            nameEnd++;

        if (nameEnd == open + 1)
            return null;

        string name = Text(text, open + 1, nameEnd);
        int start = close + 1;

        // A newline is the whole distinction. Content typed on the tag's own line is its value;
        // content on a later line is where the next child goes, which is what an unclosed
        // <PropertyGroup> is and why offering property names there is right.
        for (int i = start; i < offset; i++)
        {
            if (text[i] is '\n' or '\r')
                return new MsBuildMarkup(name, OnName: false, Whitespace: true, new TextSpan(offset, 0));
        }

        while (start < offset && char.IsWhiteSpace(text[start]))
            start++;

        int end = offset;
        while (end < text.Length && text[end] is not ('<' or '\n' or '\r'))
            end++;

        return new MsBuildMarkup(name, OnName: false, Whitespace: false, TextSpan.FromBounds(start, end));
    }

    /// <summary>The characters an element name is made of, plus the ones MSBuild's names use.</summary>
    private static bool IsNameChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':';

    private static string Text(SourceText text, int start, int end) =>
        text.ToString(TextSpan.FromBounds(start, end));
}
