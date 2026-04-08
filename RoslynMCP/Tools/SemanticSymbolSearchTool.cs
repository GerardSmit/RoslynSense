using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Solution-oriented semantic symbol search with ranked, chainable results.
/// Blends lexical name matching with signature, containing-type, XML-doc, and
/// source-body cues so LLMs can find symbols from partial descriptions.
/// </summary>
[McpServerToolType]
public static class SemanticSymbolSearchTool
{
    private const int DefaultMaxResults = 25;
    private const int MaxAllowedResults = 100;
    private const int MaxQueryTokens = 8;
    private const int MaxMetadataCandidates = 250;

    private static readonly Regex s_tokenRegex = new(
        @"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_xmlTagRegex = new(
        "<.*?>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly HashSet<string> s_stopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "as", "at", "be", "by", "for", "from", "in", "into",
        "is", "it", "of", "on", "or", "that", "the", "this", "to", "with"
    };

    private static readonly Dictionary<string, string[]> s_synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bool"] = ["boolean"],
        ["boolean"] = ["bool"],
        ["int"] = ["integer"],
        ["integer"] = ["int"],
        ["string"] = ["text"],
        ["text"] = ["string"],
        ["upper"] = ["uppercase"],
        ["uppercase"] = ["upper"]
    };

    private static readonly SymbolDisplayFormat s_symbolDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>
    /// Searches for symbols across the loaded project graph with ranked results.
    /// Supports direct name/pattern searches and phrase-like queries such as
    /// "provided text" or "trim first character".
    /// </summary>
    [McpServerTool, Description(
        "Search for symbols across the loaded C# project graph with ranked results. " +
        "Combines symbol-name and camelCase matching with signature terms, containing types, " +
        "XML-doc text, and source-body cues so phrase-style queries can still find relevant symbols. " +
        "Returns file paths or metadata origins plus match reasons for chaining with GoToDefinition and FindUsages.")]
    public static async Task<string> SemanticSymbolSearch(
        [Description("Path to any file in the project/solution to search.")] string filePath,
        [Description("Search query: symbol name, pattern, or short description of intent.")] string query,
        [Description("Maximum results to return (default 25, capped at 100).")] int maxResults = DefaultMaxResults,
        [Description("When true, also searches referenced-assembly types and members visible from the selected project.")]
        bool includeReferencedAssemblies = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
                return "Error: query cannot be empty.";

            var errors = new StringBuilder();
            var fileCtx = await ToolHelper.ResolveFileAsync(filePath, errors, cancellationToken);
            if (fileCtx is null)
                return errors.ToString();

            maxResults = Math.Clamp(maxResults <= 0 ? DefaultMaxResults : maxResults, 1, MaxAllowedResults);

            var project = fileCtx.Project;
            var searchQuery = SearchQuery.Create(query);
            var scored = await SearchAsync(project, searchQuery, includeReferencedAssemblies, cancellationToken);

            var ranked = scored
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.IsSource ? 0 : 1)
                .ThenBy(item => item.Symbol.Name, StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .ToList();

            int projectCount = project.Solution.Projects.Count(p => p.Language == LanguageNames.CSharp);

            return FormatResults(
                ranked,
                searchQuery.Raw,
                projectCount,
                includeReferencedAssemblies,
                totalBeforeCap: scored.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SemanticSymbolSearch] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<List<ScoredSymbol>> SearchAsync(
        Project rootProject,
        SearchQuery query,
        bool includeReferencedAssemblies,
        CancellationToken cancellationToken)
    {
        var scored = new Dictionary<string, ScoredSymbol>(StringComparer.Ordinal);

        foreach (var project in rootProject.Solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
                continue;

            foreach (var symbol in EnumerateSourceSymbols(compilation.Assembly.GlobalNamespace))
                TryAddCandidate(scored, symbol, query, isSource: true, origin: project.Name);
        }

        if (includeReferencedAssemblies)
        {
            var compilation = await rootProject.GetCompilationAsync(cancellationToken);
            if (compilation is not null)
            {
                foreach (var symbol in compilation
                             .GetSymbolsWithName(query.MatchesReferenceName, SymbolFilter.Type | SymbolFilter.Member)
                             .Take(MaxMetadataCandidates))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TryAddCandidate(
                        scored,
                        symbol,
                        query,
                        isSource: false,
                        origin: symbol.ContainingAssembly?.Name ?? "metadata");
                }
            }
        }

        return scored.Values.ToList();
    }

    private static void TryAddCandidate(
        Dictionary<string, ScoredSymbol> scored,
        ISymbol symbol,
        SearchQuery query,
        bool isSource,
        string origin)
    {
        if (ShouldSkip(symbol, isSource))
            return;

        var candidate = ScoreSymbol(symbol, query, isSource, origin);
        if (candidate is null)
            return;

        string key = BuildDeduplicationKey(symbol);
        if (!scored.TryGetValue(key, out var existing) ||
            candidate.Score > existing.Score ||
            (candidate.Score == existing.Score && candidate.IsSource && !existing.IsSource))
        {
            scored[key] = candidate;
        }
    }

    private static IEnumerable<ISymbol> EnumerateSourceSymbols(INamespaceSymbol namespaceSymbol)
    {
        foreach (var member in namespaceSymbol.GetMembers())
        {
            if (member is INamespaceSymbol nestedNamespace)
            {
                foreach (var nested in EnumerateSourceSymbols(nestedNamespace))
                    yield return nested;

                continue;
            }

            if (member is INamedTypeSymbol namedType)
            {
                foreach (var nested in EnumerateTypeAndMembers(namedType))
                    yield return nested;
            }
        }
    }

    private static IEnumerable<ISymbol> EnumerateTypeAndMembers(INamedTypeSymbol type)
    {
        yield return type;

        foreach (var nestedType in type.GetTypeMembers())
        {
            foreach (var nested in EnumerateTypeAndMembers(nestedType))
                yield return nested;
        }

        foreach (var member in type.GetMembers())
        {
            if (member is INamedTypeSymbol)
                continue;

            yield return member;
        }
    }

    internal static ScoredSymbol? ScoreSymbol(
        ISymbol symbol,
        SearchQuery query,
        bool isSource,
        string origin)
    {
        double score = 0;
        var reasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string searchName = GetSearchName(symbol);
        string containingType = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "";
        string namespaceName = symbol.ContainingNamespace is { IsGlobalNamespace: false }
            ? symbol.ContainingNamespace.ToDisplayString()
            : "";
        string signature = GetSignatureText(symbol);

        score += ScoreName(searchName, query, matchedTerms, matchedFields, reasons);
        score += ScoreTextField("containing type", containingType, query, 14, 18, matchedTerms, matchedFields, reasons);
        score += ScoreTextField("signature", signature, query, 12, 16, matchedTerms, matchedFields, reasons);
        score += ScoreTextField("namespace", namespaceName, query, 4, 8, matchedTerms, matchedFields, reasons);

        string documentation = ExtractDocumentationText(symbol);
        double documentationScore = ScoreTextField("in docs", documentation, query, 9, 16, matchedTerms, matchedFields, reasons);
        score += documentationScore;

        if (isSource && (query.PrimaryTokens.Count > 1 || score > 0 || documentationScore > 0))
        {
            string sourceCues = GetSourceCueText(symbol);
            score += ScoreTextField("source cues", sourceCues, query, 7, 12, matchedTerms, matchedFields, reasons);
        }

        if (matchedTerms.Count == 0)
            return null;

        if (query.PrimaryTokens.Count > 1 && matchedTerms.Count == query.PrimaryTokens.Count)
        {
            score += 18;
            reasons.Add("all query terms");
        }

        if (matchedFields.Count > 1)
        {
            score += 10 + ((matchedFields.Count - 2) * 4);
            reasons.Add("cross-field");
        }

        score += KindScore(symbol);
        score += AccessibilityScore(symbol);
        score += isSource ? 6 : 2;

        string reasonText = string.Join("; ", reasons.OrderBy(reason => reason, StringComparer.OrdinalIgnoreCase));
        return new ScoredSymbol(symbol, score, reasonText, isSource, origin);
    }

    private static double ScoreName(
        string name,
        SearchQuery query,
        HashSet<string> matchedTerms,
        HashSet<string> matchedFields,
        HashSet<string> reasons)
    {
        double score = 0;
        var nameTokens = TokenizeToSet(name, includeStopWords: false, expandSynonyms: true);
        int overlap = TrackOverlap(query.PrimaryTokens, nameTokens, matchedTerms);
        double lexicalScore = 0;
        string? lexicalReason = null;

        if (string.Equals(name, query.Raw, StringComparison.OrdinalIgnoreCase))
        {
            lexicalScore = 120;
            lexicalReason = "exact name";
        }
        else
        {
            if (!query.Raw.Contains(' ') && name.StartsWith(query.Raw, StringComparison.OrdinalIgnoreCase))
            {
                lexicalScore = 90;
                lexicalReason = "prefix";
            }
            else if (!query.Raw.Contains(' ') && IsCamelCaseMatch(name, query.Raw))
            {
                lexicalScore = 84;
                lexicalReason = "camelCase";
            }
            else if (name.Contains(query.Raw, StringComparison.OrdinalIgnoreCase))
            {
                lexicalScore = 60;
                lexicalReason = "substring";
            }
        }

        if (lexicalReason is not null)
        {
            score += lexicalScore;
            reasons.Add(lexicalReason);
            matchedFields.Add("name");
            foreach (var token in query.PrimaryTokens)
                matchedTerms.Add(token);
        }

        if (ContainsNormalizedPhrase(name, query.NormalizedPhrase))
        {
            score += 28;
            reasons.Add("name phrase");
            matchedFields.Add("name");
            foreach (var token in query.PrimaryTokens)
                matchedTerms.Add(token);
        }

        if (overlap > 0)
        {
            matchedFields.Add("name");
            reasons.Add("name");
            score += overlap * 24;

            if (query.PrimaryTokens.Count > 1 && overlap == query.PrimaryTokens.Count)
                score += 24;
        }

        return score;
    }

    private static double ScoreTextField(
        string reason,
        string text,
        SearchQuery query,
        double perTokenWeight,
        double phraseBonus,
        HashSet<string> matchedTerms,
        HashSet<string> matchedFields,
        HashSet<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var tokens = TokenizeToSet(text, includeStopWords: false, expandSynonyms: true);
        int overlap = TrackOverlap(query.PrimaryTokens, tokens, matchedTerms);
        bool phraseMatch = ContainsNormalizedPhrase(text, query.NormalizedPhrase);

        if (overlap == 0 && !phraseMatch)
            return 0;

        matchedFields.Add(reason);
        reasons.Add(reason);

        double score = overlap * perTokenWeight;
        if (phraseMatch)
            score += phraseBonus;

        if (query.PrimaryTokens.Count > 1 && overlap == query.PrimaryTokens.Count)
            score += perTokenWeight;

        return score;
    }

    internal static bool IsCamelCaseMatch(string name, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > name.Length)
            return false;

        int patternIndex = 0;
        for (int nameIndex = 0; nameIndex < name.Length && patternIndex < pattern.Length; nameIndex++)
        {
            bool isWordStart = nameIndex == 0
                || char.IsUpper(name[nameIndex])
                || name[nameIndex - 1] == '_';

            if (isWordStart &&
                char.ToLowerInvariant(name[nameIndex]) == char.ToLowerInvariant(pattern[patternIndex]))
            {
                patternIndex++;
            }
        }

        return patternIndex == pattern.Length;
    }

    private static double KindScore(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { IsRecord: true } => 10,
        INamedTypeSymbol { TypeKind: TypeKind.Class } => 9,
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => 8,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => 7,
        IMethodSymbol => 8,
        IPropertySymbol => 6,
        IFieldSymbol => 5,
        IEventSymbol => 5,
        _ => 4
    };

    private static double AccessibilityScore(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Accessibility.Public => 8,
        Accessibility.Protected => 6,
        Accessibility.ProtectedOrInternal => 5,
        Accessibility.ProtectedAndInternal => 5,
        Accessibility.Internal => 4,
        Accessibility.Private => 2,
        _ => 3
    };

    private static bool ShouldSkip(ISymbol symbol, bool isSource)
    {
        if (symbol.IsImplicitlyDeclared)
            return true;

        if (symbol is IFieldSymbol { AssociatedSymbol: not null })
            return true;

        if (symbol is IMethodSymbol method)
        {
            if (method.AssociatedSymbol is not null)
                return true;

            if (method.MethodKind is MethodKind.StaticConstructor or MethodKind.Destructor)
                return true;
        }

        if (symbol is not INamedTypeSymbol and not IMethodSymbol and not IPropertySymbol and not IFieldSymbol and not IEventSymbol)
            return true;

        if (!isSource &&
            symbol.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal)
        {
            return true;
        }

        return false;
    }

    private static string GetSearchName(ISymbol symbol) =>
        symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor
            ? constructor.ContainingType.Name
            : symbol.Name;

    private static string GetSignatureText(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method => $"{method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {GetSearchName(method)}({string.Join(", ", method.Parameters.Select(static p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}"))})",
            IPropertySymbol property => $"{property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {property.Name}",
            IFieldSymbol field => $"{field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {field.Name}",
            IEventSymbol @event => $"{@event.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {@event.Name}",
            INamedTypeSymbol type => BuildTypeSignature(type),
            _ => symbol.ToDisplayString(s_symbolDisplayFormat)
        };
    }

    private static string BuildTypeSignature(INamedTypeSymbol type)
    {
        var parts = new List<string> { type.TypeKind.ToString(), type.Name };

        if (type.BaseType is not null && type.BaseType.SpecialType != SpecialType.System_Object)
            parts.Add(type.BaseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));

        foreach (var interfaceType in type.Interfaces)
            parts.Add(interfaceType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));

        return string.Join(' ', parts);
    }

    private static string ExtractDocumentationText(ISymbol symbol)
    {
        string? xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return string.Empty;

        string withoutTags = s_xmlTagRegex.Replace(xml, " ");
        return WebUtility.HtmlDecode(withoutTags);
    }

    private static string GetSourceCueText(ISymbol symbol)
    {
        var syntaxReference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxReference is null)
            return string.Empty;

        var node = syntaxReference.GetSyntax();
        var tokens = node.DescendantTokens()
            .Where(token => token.RawKind == (int)SyntaxKind.IdentifierToken ||
                            token.RawKind == (int)SyntaxKind.StringLiteralToken)
            .Select(token => token.ValueText)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(128);

        return string.Join(' ', tokens);
    }

    private static int TrackOverlap(
        IReadOnlyList<string> queryTokens,
        HashSet<string> fieldTokens,
        HashSet<string> matchedTerms)
    {
        int overlap = 0;
        foreach (var token in queryTokens)
        {
            if (fieldTokens.Contains(token))
            {
                matchedTerms.Add(token);
                overlap++;
            }
        }

        return overlap;
    }

    private static bool ContainsNormalizedPhrase(string text, string normalizedPhrase)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(normalizedPhrase))
            return false;

        return NormalizePhrase(text).Contains(normalizedPhrase, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDeduplicationKey(ISymbol symbol)
    {
        var sourceLocation = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        if (sourceLocation is not null)
        {
            var lineSpan = sourceLocation.GetLineSpan();
            return $"{symbol.ToDisplayString(s_symbolDisplayFormat)}|{lineSpan.Path}:{lineSpan.StartLinePosition.Line}:{lineSpan.StartLinePosition.Character}";
        }

        return $"{symbol.ContainingAssembly?.Name}|{symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(s_symbolDisplayFormat)}";
    }

    private static HashSet<string> TokenizeToSet(string text, bool includeStopWords, bool expandSynonyms)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in s_tokenRegex.Matches(text))
        {
            string token = NormalizeTokenForSearch(match.Value);
            if (token.Length == 0)
                continue;

            if (!includeStopWords && s_stopWords.Contains(token))
                continue;

            tokens.Add(token);

            if (!expandSynonyms || !s_synonyms.TryGetValue(token, out var synonyms))
                continue;

            foreach (var synonym in synonyms)
                tokens.Add(synonym);
        }

        return tokens;
    }

    private static string NormalizePhrase(string text) =>
        string.Join(' ', TokenizeToSet(text, includeStopWords: false, expandSynonyms: false));

    internal static string NormalizeTokenForSearch(string token)
    {
        token = token.ToLowerInvariant();

        if (token.Length > 4 &&
            token.EndsWith('s') &&
            !token.EndsWith("ss", StringComparison.Ordinal) &&
            !token.EndsWith("is", StringComparison.Ordinal) &&
            !token.EndsWith("us", StringComparison.Ordinal) &&
            !token.EndsWith("as", StringComparison.Ordinal))
        {
            token = token[..^1];
        }

        return token;
    }

    private static string FormatResults(
        List<ScoredSymbol> ranked,
        string query,
        int projectCount,
        bool searchedReferences,
        int totalBeforeCap)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Semantic Symbol Search: \"{MarkdownHelper.EscapeTableCell(query)}\"");
        sb.AppendLine();
        sb.Append($"Searched **{projectCount}** loaded C# source project(s)");
        if (searchedReferences)
            sb.Append(" plus referenced assemblies from the selected project");
        sb.AppendLine(".");

        if (ranked.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("No matching symbols found.");
            return sb.ToString();
        }

        sb.AppendLine();
        sb.Append($"Found **{totalBeforeCap}** result(s)");
        if (ranked.Count < totalBeforeCap)
            sb.Append($" (showing top {ranked.Count})");
        sb.AppendLine(".");
        sb.AppendLine();
        sb.AppendLine("| # | Symbol | Kind | Project/Assembly | Location | Why |");
        sb.AppendLine("|---|--------|------|------------------|----------|-----|");

        for (int index = 0; index < ranked.Count; index++)
        {
            var item = ranked[index];
            sb.AppendLine(
                $"| {index + 1} " +
                $"| {MarkdownHelper.EscapeTableCell(item.Symbol.ToDisplayString(s_symbolDisplayFormat))} " +
                $"| {MarkdownHelper.EscapeTableCell(GetKindDisplay(item.Symbol))} " +
                $"| {MarkdownHelper.EscapeTableCell(item.Origin)} " +
                $"| {MarkdownHelper.EscapeTableCell(FormatLocation(item))} " +
                $"| {MarkdownHelper.EscapeTableCell(item.MatchReason)} |");
        }

        sb.AppendLine();
        sb.AppendLine(
            "_Tip: take a source result's file path and symbol text into `GoToDefinition` or `FindUsages` with a `[| |]` markup snippet for follow-up._");

        return sb.ToString();
    }

    private static string GetKindDisplay(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol { IsRecord: true } => "NamedType (Record)",
            INamedTypeSymbol namedType => $"NamedType ({namedType.TypeKind})",
            IMethodSymbol { MethodKind: MethodKind.Constructor } => "Constructor",
            _ => symbol.Kind.ToString()
        };
    }

    private static string FormatLocation(ScoredSymbol item)
    {
        if (!item.IsSource)
            return "metadata";

        var location = item.Symbol.Locations.FirstOrDefault(source => source.IsInSource);
        if (location is null)
            return "—";

        var lineSpan = location.GetLineSpan();
        return $"{lineSpan.Path}:{lineSpan.StartLinePosition.Line + 1}";
    }

    internal sealed record SearchQuery(
        string Raw,
        IReadOnlyList<string> PrimaryTokens,
        IReadOnlySet<string> ExpandedTokens,
        string NormalizedPhrase)
    {
        public static SearchQuery Create(string query)
        {
            var primaryTokens = TokenizeToSet(query, includeStopWords: false, expandSynonyms: false)
                .Take(MaxQueryTokens)
                .ToArray();

            if (primaryTokens.Length == 0)
            {
                primaryTokens = TokenizeToSet(query, includeStopWords: true, expandSynonyms: false)
                    .Take(MaxQueryTokens)
                    .ToArray();
            }

            var expandedTokens = new HashSet<string>(primaryTokens, StringComparer.OrdinalIgnoreCase);
            foreach (var token in primaryTokens)
            {
                if (!s_synonyms.TryGetValue(token, out var synonyms))
                    continue;

                foreach (var synonym in synonyms)
                    expandedTokens.Add(synonym);
            }

            return new SearchQuery(
                query.Trim(),
                primaryTokens,
                expandedTokens,
                string.Join(' ', primaryTokens));
        }

        public bool MatchesReferenceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            if (ContainsNormalizedPhrase(name, NormalizedPhrase))
                return true;

            foreach (var token in ExpandedTokens)
            {
                if (string.Equals(name, token, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(token, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                    IsCamelCaseMatch(name, token))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal sealed record ScoredSymbol(
        ISymbol Symbol,
        double Score,
        string MatchReason,
        bool IsSource,
        string Origin);
}
