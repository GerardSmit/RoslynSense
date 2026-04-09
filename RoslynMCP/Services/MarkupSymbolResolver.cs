using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Services;

/// <summary>
/// Result of attempting to resolve a markup-targeted symbol in a source file.
/// Use the <see cref="Kind"/> property to discriminate, then read the
/// corresponding strongly-typed properties.
/// </summary>
internal sealed class MarkupResolutionResult
{
    public enum ResultKind
    {
        /// <summary>Symbol resolved successfully.</summary>
        Resolved,
        /// <summary>Snippet matched but no Roslyn symbol was found at the marked position.</summary>
        NoSymbol,
        /// <summary>Snippet matched more than one location in the file.</summary>
        Ambiguous,
        /// <summary>Snippet did not match any location in the file.</summary>
        NoMatch,
        /// <summary>The resolver could not complete because of an input or infrastructure error.</summary>
        Error,
    }

    public ResultKind Kind { get; }

    // --- Resolved / NoSymbol fields ---

    /// <summary>The resolved Roslyn symbol (only set when <see cref="Kind"/> is <see cref="ResultKind.Resolved"/>).</summary>
    public ISymbol? Symbol { get; }

    /// <summary>The document the symbol was resolved in.</summary>
    public Document? Document { get; }

    /// <summary>Absolute position in the source file corresponding to the start of the marked span.</summary>
    public int? Position { get; }

    /// <summary>The <see cref="TextSpan"/> in the real document that the marked span maps to.</summary>
    public TextSpan? MappedSpan { get; }

    // --- Ambiguous fields ---

    /// <summary>
    /// When <see cref="Kind"/> is <see cref="ResultKind.Ambiguous"/>, contains the 1-based line numbers
    /// of every match so the caller can present disambiguation info.
    /// </summary>
    public IReadOnlyList<int>? AmbiguousLineNumbers { get; }

    // --- Diagnostic ---

    /// <summary>Human-readable explanation suitable for returning to an LLM caller.</summary>
    public string Message { get; }

    private MarkupResolutionResult(ResultKind kind, string message,
        ISymbol? symbol = null, Document? document = null,
        int? position = null, TextSpan? mappedSpan = null,
        IReadOnlyList<int>? ambiguousLineNumbers = null)
    {
        Kind = kind;
        Message = message;
        Symbol = symbol;
        Document = document;
        Position = position;
        MappedSpan = mappedSpan;
        AmbiguousLineNumbers = ambiguousLineNumbers;
    }

    public static MarkupResolutionResult Resolved(ISymbol symbol, Document document, int position, TextSpan mappedSpan) =>
        new(ResultKind.Resolved,
            $"Resolved symbol '{symbol.ToDisplayString()}' ({symbol.Kind}).",
            symbol: symbol, document: document, position: position, mappedSpan: mappedSpan);

    public static MarkupResolutionResult NoSymbol(Document document, int position, TextSpan mappedSpan) =>
        new(ResultKind.NoSymbol,
            $"No Roslyn symbol found at position {position} in '{document.FilePath}'.",
            document: document, position: position, mappedSpan: mappedSpan);

    public static MarkupResolutionResult Ambiguous(IReadOnlyList<int> lineNumbers) =>
        new(ResultKind.Ambiguous,
            $"Snippet matched {lineNumbers.Count} locations (lines {string.Join(", ", lineNumbers)}). " +
            "Add surrounding context to the snippet to disambiguate.",
            ambiguousLineNumbers: lineNumbers);

    public static MarkupResolutionResult NoMatch(string snippetPreview) =>
        new(ResultKind.NoMatch,
            $"Snippet not found in the file. Searched for: \"{Truncate(snippetPreview, 120)}\".");

    public static MarkupResolutionResult Error(string message) =>
        new(ResultKind.Error, message);

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : string.Concat(s.AsSpan(0, maxLen - 3), "...");
}

/// <summary>
/// Resolves a markup-annotated snippet (<c>[| |]</c>) to a Roslyn <see cref="ISymbol"/>
/// within an open workspace project.
/// <para>
/// Designed as a reusable primitive: callers (tools, future FindUsages evolution) supply
/// the <see cref="Workspace"/>, <see cref="Project"/>, and <see cref="Document"/>;
/// this class handles snippet location, span mapping, and symbol lookup.
/// </para>
/// </summary>
internal static class MarkupSymbolResolver
{
    /// <summary>
    /// High-level convenience: given a file path and a markup snippet, opens the project,
    /// locates the snippet, and resolves the symbol.
    /// </summary>
    public static async Task<MarkupResolutionResult> ResolveFromFileAsync(
        string filePath, MarkupString markup, CancellationToken cancellationToken = default,
        int? hintLine = null)
    {
        string systemPath = PathHelper.NormalizePath(filePath);

        if (!File.Exists(systemPath))
            return MarkupResolutionResult.Error($"File not found: {systemPath}");

        string? projectPath = await WorkspaceService.FindContainingProjectAsync(systemPath, cancellationToken);
        if (string.IsNullOrEmpty(projectPath))
            return MarkupResolutionResult.Error("Could not find a project containing the file.");

        var (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, targetFilePath: systemPath, cancellationToken: cancellationToken);
        var document = WorkspaceService.FindDocumentInProject(project, systemPath);

        if (document == null)
            return MarkupResolutionResult.Error("File not found in project documents.");

        return await ResolveAsync(document, workspace, markup, cancellationToken, hintLine);
    }

    /// <summary>
    /// Core resolution: given a Roslyn <see cref="Document"/>, locates the snippet
    /// inside its text and resolves the marked span to a symbol.
    /// </summary>
    public static async Task<MarkupResolutionResult> ResolveAsync(
        Document document, Workspace workspace, MarkupString markup,
        CancellationToken cancellationToken = default, int? hintLine = null)
    {
        var sourceText = await document.GetTextAsync(cancellationToken);
        string fileText = sourceText.ToString();

        // Find all occurrences of the plain-text snippet in the file
        var matches = FindAllOccurrences(fileText, markup.PlainText);

        if (matches.Count == 0)
            return MarkupResolutionResult.NoMatch(markup.PlainText);

        SnippetMatch match;
        if (matches.Count > 1)
        {
            if (hintLine.HasValue)
            {
                // Pick the match closest to hintLine (1-based)
                match = matches.OrderBy(m =>
                    Math.Abs(sourceText.Lines.GetLineFromPosition(m.FileOffset).LineNumber + 1 - hintLine.Value))
                    .First();
            }
            else
            {
                var lineNumbers = matches
                    .Select(m => sourceText.Lines.GetLineFromPosition(m.FileOffset).LineNumber + 1)
                    .ToList();
                return MarkupResolutionResult.Ambiguous(lineNumbers);
            }
        }
        else
        {
            match = matches[0];
        }
        int markedStartInFile = MapSnippetOffsetToFile(fileText, match, markup.PlainText, markup.SpanStart);
        int markedEndInFile = MapSnippetOffsetToFile(fileText, match, markup.PlainText, markup.SpanStart + markup.SpanLength);
        var mappedSpan = new TextSpan(markedStartInFile, markedEndInFile - markedStartInFile);

        // Resolve at the start of the marked span (typical for identifiers)
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
            return MarkupResolutionResult.Error("Unable to obtain semantic model for the target document.");

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
             semanticModel, markedStartInFile, workspace, cancellationToken);

        if (symbol == null)
        {
            var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
            if (syntaxRoot == null)
                return MarkupResolutionResult.Error("Unable to obtain syntax root for the target document.");

            symbol = ResolveSymbolFromMarkedSpan(semanticModel, syntaxRoot, mappedSpan, markedStartInFile);
        }

        if (symbol == null)
            return MarkupResolutionResult.NoSymbol(document, markedStartInFile, mappedSpan);

        return MarkupResolutionResult.Resolved(symbol, document, markedStartInFile, mappedSpan);
    }

    private static ISymbol? ResolveSymbolFromMarkedSpan(
        SemanticModel semanticModel,
        SyntaxNode syntaxRoot,
        TextSpan mappedSpan,
        int position)
    {
        var token = syntaxRoot.FindToken(position);
        if (token.Parent == null)
            return null;

        foreach (var node in token.Parent.AncestorsAndSelf().OrderBy(node => node.Span.Length))
        {
            if (!node.FullSpan.Contains(mappedSpan))
                continue;

            var declared = semanticModel.GetDeclaredSymbol(node);
            if (declared != null)
                return declared;

            var symbolInfo = semanticModel.GetSymbolInfo(node);
            if (symbolInfo.Symbol != null)
                return symbolInfo.Symbol;

            if (symbolInfo.CandidateSymbols.Length == 1)
                return symbolInfo.CandidateSymbols[0];

            var typeInfo = semanticModel.GetTypeInfo(node);
            if (typeInfo.Type != null && typeInfo.Type.TypeKind != TypeKind.Error)
                return typeInfo.Type;

            if (typeInfo.ConvertedType != null && typeInfo.ConvertedType.TypeKind != TypeKind.Error)
                return typeInfo.ConvertedType;
        }

        return null;
    }

    /// <summary>
    /// Represents a snippet match: where it starts in the file and how long the
    /// matching region is in the original file text.
    /// </summary>
    internal readonly record struct SnippetMatch(int FileOffset, int FileLength, bool IsExact);

    /// <summary>
    /// Finds all positions where <paramref name="snippet"/> occurs in
    /// <paramref name="text"/> using ordinal comparison.
    /// Tries exact matching first; falls back to whitespace-normalized matching.
    /// </summary>
    internal static List<SnippetMatch> FindAllOccurrences(string text, string snippet)
    {
        // First try exact matching (fast path)
        var exactMatches = FindAllExact(text, snippet);
        if (exactMatches.Count > 0)
            return exactMatches;

        // Fall back to whitespace-normalized matching
        return FindAllNormalized(text, snippet);
    }

    private static List<SnippetMatch> FindAllExact(string text, string snippet)
    {
        var results = new List<SnippetMatch>();
        int searchStart = 0;
        while (searchStart <= text.Length - snippet.Length)
        {
            int index = text.IndexOf(snippet, searchStart, StringComparison.Ordinal);
            if (index < 0) break;
            results.Add(new SnippetMatch(index, snippet.Length, IsExact: true));
            searchStart = index + 1;
        }
        return results;
    }

    /// <summary>
    /// Normalized matching: compresses runs of whitespace on both sides to single spaces,
    /// then maps matched positions back to original file offsets.
    /// </summary>
    private static List<SnippetMatch> FindAllNormalized(string text, string snippet)
    {
        var (normalizedText, offsetMap) = NormalizeWhitespace(text);
        var normalizedSnippet = NormalizeSnippet(snippet);

        if (normalizedSnippet.Length == 0)
            return new List<SnippetMatch>();

        var results = new List<SnippetMatch>();
        int searchStart = 0;
        while (searchStart <= normalizedText.Length - normalizedSnippet.Length)
        {
            int index = normalizedText.IndexOf(normalizedSnippet, searchStart, StringComparison.Ordinal);
            if (index < 0) break;

            int fileStart = offsetMap[index];
            int fileEnd = offsetMap[index + normalizedSnippet.Length - 1] + 1;
            results.Add(new SnippetMatch(fileStart, fileEnd - fileStart, IsExact: false));
            searchStart = index + 1;
        }
        return results;
    }

    /// <summary>
    /// Maps a snippet-relative offset to a file-absolute position.
    /// For exact matches this is simple addition; for normalized matches we walk
    /// the matched file region character-by-character to account for whitespace
    /// differences between the snippet and the file.
    /// </summary>
    internal static int MapSnippetOffsetToFile(string fileText, SnippetMatch match, string snippetPlainText, int snippetOffset)
    {
        if (match.IsExact)
            return match.FileOffset + snippetOffset;

        // Walk both the snippet and file region in parallel, skipping extra whitespace
        int snippetPos = 0;
        int filePos = match.FileOffset;
        int fileEnd = match.FileOffset + match.FileLength;

        while (snippetPos < snippetOffset && filePos < fileEnd)
        {
            char sc = snippetPlainText[snippetPos];
            char fc = fileText[filePos];

            if (char.IsWhiteSpace(sc) && char.IsWhiteSpace(fc))
            {
                // Consume whitespace runs in both
                while (snippetPos < snippetOffset && snippetPos < snippetPlainText.Length && char.IsWhiteSpace(snippetPlainText[snippetPos]))
                    snippetPos++;
                while (filePos < fileEnd && char.IsWhiteSpace(fileText[filePos]))
                    filePos++;
            }
            else
            {
                snippetPos++;
                filePos++;
            }
        }

        // If the snippet offset lands inside a whitespace run, skip file whitespace too
        if (snippetPos == snippetOffset && filePos < fileEnd && snippetOffset > 0
            && char.IsWhiteSpace(snippetPlainText[snippetOffset - 1]))
        {
            while (filePos < fileEnd && char.IsWhiteSpace(fileText[filePos]))
                filePos++;
        }

        return filePos;
    }

    /// <summary>
    /// Normalizes whitespace in the text, returning the normalized string
    /// and a mapping from each normalized-string index to its original index.
    /// </summary>
    private static (string normalized, int[] offsetMap) NormalizeWhitespace(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        bool lastWasSpace = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    map.Add(i);
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                map.Add(i);
                lastWasSpace = false;
            }
        }

        return (sb.ToString(), map.ToArray());
    }

    private static string NormalizeSnippet(string snippet)
    {
        var sb = new System.Text.StringBuilder(snippet.Length);
        bool lastWasSpace = false;

        foreach (char c in snippet)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        return sb.ToString();
    }
}
