using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Lsp.Completion;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Search;

public enum SearchItemKind
{
    Type,
    Member,
    File,
}

/// <summary>One ranked result. <paramref name="Score"/> is lower-is-better.</summary>
public sealed record SearchHit(
    SearchItemKind Kind,
    string Name,
    string? Container,
    string FilePath,
    int Line,
    int Character,
    int EndLine,
    int EndCharacter,
    int SymbolKind,
    int Score);

/// <summary>
/// Search Everywhere: one query box over types, members and files, ranked the way ReSharper's
/// Go to Everything ranks — an exact type beats an exact member beats a fuzzy type, and the
/// tiering is arithmetic rather than a sort of sorts.
/// </summary>
/// <remarks>
/// Two things carry the design. First, tier arithmetic: a match score is turned into
/// <c>-score - TierUnit * tier</c>, where one tier step outweighs every possible match score, so
/// "which kind of thing is this" strictly dominates "how well does the name match" without either
/// being thrown away. Second, names are matched before symbols exist — the predicate handed to
/// Roslyn runs on declaration names, so only the matches are ever materialised.
/// </remarks>
public static class SearchEverywhere
{
    /// <summary>Bigger than any <see cref="MatcherScore"/>, so a tier can never be out-scored.</summary>
    private const int TierUnit = 0x1000;

    private const int TierExactType = 3;
    private const int TierExactMember = 2;
    private const int TierExactFile = 2;
    private const int TierType = 1;
    private const int TierFile = 1;
    private const int TierMember = 0;

    /// <summary>
    /// Pushes a tool-written file — and anything declared in one — below every hand-written
    /// result, whatever its tier. Bigger than the whole tier range on purpose: a generated exact
    /// type match still belongs under a hand-written fuzzy one.
    /// </summary>
    private const int GeneratedPenalty = TierUnit * 8;

    private static readonly char[] s_wordSeparators = ['.', '/', '\\', ' ', '+'];

    /// <param name="includeFiles">workspace/symbol has no kind for a file, so it asks for
    /// symbols only; the extension's own Search Everywhere wants both.</param>
    public static async Task<IReadOnlyList<SearchHit>> SearchAsync(
        Solution solution, string query, int maxResults, CancellationToken ct,
        bool includeFiles = true)
    {
        var request = SearchQuery.Parse(query, includeFiles);
        if (request is null)
            return [];

        var hits = new List<SearchHit>();

        if (request.IncludesSymbols)
            hits.AddRange(await FindSymbolsAsync(solution, request, ct));

        if (request.IncludesFiles)
            hits.AddRange(await FindFilesAsync(solution, request, ct));

        hits.Sort(Compare);

        // Linked documents (one file in several target frameworks) declare the same symbol once
        // per project; the user wants one row.
        var seen = new HashSet<(string, string, int, int)>();
        var deduped = new List<SearchHit>(Math.Min(hits.Count, maxResults));
        foreach (var hit in hits)
        {
            if (!seen.Add((hit.Name, hit.FilePath, hit.Line, hit.Character)))
                continue;

            deduped.Add(hit);
            if (deduped.Count == maxResults)
                break;
        }

        return deduped;
    }

    /// <summary>
    /// Score first, then the shortest name — <c>List</c> before <c>ListView</c> for the same
    /// query, which is nearly always what was meant.
    /// </summary>
    private static int Compare(SearchHit x, SearchHit y)
    {
        int byScore = x.Score.CompareTo(y.Score);
        if (byScore != 0)
            return byScore;

        int byLength = x.Name.Length.CompareTo(y.Name.Length);
        if (byLength != 0)
            return byLength;

        int byKind = x.Kind.CompareTo(y.Kind);
        if (byKind != 0)
            return byKind;

        int byName = string.CompareOrdinal(x.Name, y.Name);
        return byName != 0 ? byName : string.CompareOrdinal(x.FilePath, y.FilePath);
    }

    private static async Task<IEnumerable<SearchHit>> FindSymbolsAsync(
        Solution solution, SearchQuery request, CancellationToken ct)
    {
        var matcher = request.NameMatcher;

        // The predicate sees declaration names, not symbols: everything that fails here costs
        // nothing beyond the match itself.
        var symbols = await SymbolFinder.FindSourceDeclarationsAsync(
            solution, name => matcher.Match(name) is not null, SymbolFilter.TypeAndMember, ct);

        var hits = new List<SearchHit>();
        foreach (var symbol in symbols)
        {
            ct.ThrowIfCancellationRequested();

            if (symbol.IsImplicitlyDeclared || matcher.Match(symbol.Name) is not { } match)
                continue;

            string container = ContainerOf(symbol);
            if (!request.TryScoreContainer(container, out int containerScore))
                continue;

            bool isType = symbol is INamedTypeSymbol;
            if (isType ? !request.IncludesTypes : !request.IncludesMembers)
                continue;

            int tier = (isType, match.Score.IsExactMatch()) switch
            {
                (true, true) => TierExactType,
                (true, false) => TierType,
                (false, true) => TierExactMember,
                (false, false) => TierMember,
            };

            var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (location is null || location.SourceTree?.FilePath is not { Length: > 0 } path)
                continue;

            // Build output is not a place anyone navigates to, and it is where every SDK
            // project's generated AssemblyInfo lives.
            if (SearchFileRules.IsExcluded(path))
                continue;

            var span = location.GetLineSpan();
            hits.Add(new SearchHit(
                isType ? SearchItemKind.Type : SearchItemKind.Member,
                symbol.Name,
                container.Length == 0 ? null : container,
                path,
                span.StartLinePosition.Line,
                span.StartLinePosition.Character,
                span.EndLinePosition.Line,
                span.EndLinePosition.Character,
                LspConverters.ToLspSymbolKind(symbol),
                Score(match.Score, tier, containerScore, SearchFileRules.IsGenerated(path))));
        }

        return hits;
    }

    /// <summary>
    /// Files come from the directory index rather than from the compilation: a <c>.proto</c>, a
    /// <c>.json</c> or a <c>.md</c> is not a Roslyn document, and a search that only knew about
    /// documents could never find one.
    /// </summary>
    private static async Task<IEnumerable<SearchHit>> FindFilesAsync(
        Solution solution, SearchQuery request, CancellationToken ct)
    {
        var matcher = request.NameMatcher;
        var hits = new List<SearchHit>();

        foreach (string path in await SolutionFileIndex.FilesAsync(solution, ct))
        {
            ct.ThrowIfCancellationRequested();

            string fileName = Path.GetFileName(path);
            string directory = Path.GetDirectoryName(path) ?? "";
            int containerScore = 0;
            MatchResult? match;

            if (request.ExtensionQuery is { } extension)
            {
                // ".proto" asks for a kind of file, not for a name: match the extension itself,
                // which no name matcher would ever do — a pattern cannot start on the dot.
                match = extension.Match(Path.GetExtension(path).TrimStart('.'));
                if (match is null)
                    continue;
            }
            else
            {
                // "Calculator.cs" is one file name, not a container plus a name — the dot that
                // separates a namespace from a type separates a file from its extension too, so
                // the whole query gets a shot at the file name before the split version does.
                match = request.FullMatcher.Match(fileName);
                if (match is null)
                {
                    match = matcher.Match(Path.GetFileNameWithoutExtension(path));
                    if (match is null || !request.TryScoreContainer(directory, out containerScore))
                        continue;
                }
            }

            int tier = match.Value.Score.IsExactMatch() ? TierExactFile : TierFile;
            hits.Add(new SearchHit(
                SearchItemKind.File,
                fileName,
                directory.Length == 0 ? null : directory,
                path,
                0,
                0,
                0,
                0,
                LspSymbolKind.File,
                Score(match.Value.Score, tier, containerScore, SearchFileRules.IsGenerated(path))));
        }

        return hits;
    }

    /// <summary>
    /// Tier arithmetic plus the container words. Container words weigh more than the name itself
    /// (ReSharper's <c>W(x) = Σ (i+1)·score</c>): having typed a container, the user has said
    /// something stronger about where the thing lives than about what it is called.
    /// </summary>
    private static int Score(MatcherScore nameScore, int tier, int containerScore, bool isGenerated) =>
        -(int)nameScore - TierUnit * tier + containerScore + (isGenerated ? GeneratedPenalty : 0);

    private static string ContainerOf(ISymbol symbol) =>
        symbol.ContainingType?.ToDisplayString()
        ?? (symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "");

    /// <summary>
    /// A parsed query: an optional kind filter, the container words, and the last word — the one
    /// that names the thing being looked for.
    /// </summary>
    private sealed class SearchQuery
    {
        private readonly IdentifierMatcher[] _containerMatchers;

        private readonly bool _allowFiles;

        private SearchQuery(
            IdentifierMatcher nameMatcher,
            IdentifierMatcher fullMatcher,
            IdentifierMatcher[] containerMatchers,
            SearchItemKind? only,
            bool allowFiles)
        {
            NameMatcher = nameMatcher;
            FullMatcher = fullMatcher;
            _containerMatchers = containerMatchers;
            Only = only;
            _allowFiles = allowFiles;
        }

        public IdentifierMatcher NameMatcher { get; }

        /// <summary>The whole query, separators included — file names contain their own dots.</summary>
        public IdentifierMatcher FullMatcher { get; }

        /// <summary>Set when the query is a bare extension (".proto"): match extensions, not names.</summary>
        public IdentifierMatcher? ExtensionQuery { get; private init; }

        public SearchItemKind? Only { get; }

        public bool IncludesTypes => Only is null or SearchItemKind.Type;
        public bool IncludesMembers => Only is null or SearchItemKind.Member;
        public bool IncludesFiles => _allowFiles && Only is null or SearchItemKind.File;
        public bool IncludesSymbols => IncludesTypes || IncludesMembers;

        /// <summary>
        /// Splits "Ns.Type.Member" or "dir/file" into container words plus the final name.
        /// Returns null for a query with nothing to search for.
        /// </summary>
        public static SearchQuery? Parse(string query, bool allowFiles = true)
        {
            query = query.Trim();
            if (query.Length == 0)
                return null;

            SearchItemKind? only = null;
            if (query.Length > 2 && query[1] == ':')
            {
                only = char.ToLowerInvariant(query[0]) switch
                {
                    't' => SearchItemKind.Type,
                    'm' => SearchItemKind.Member,
                    'f' => SearchItemKind.File,
                    _ => null,
                };
                if (only is not null)
                    query = query[2..].Trim();
            }

            var words = query.Split(s_wordSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                return null;

            // A query that is nothing but an extension asks for a kind of file. Symbols are
            // excluded outright: nobody typing ".proto" wants a type called Proto.
            if (query[0] == '.' && words.Length == 1)
            {
                return new SearchQuery(
                    new IdentifierMatcher(words[0]), new IdentifierMatcher(query), [],
                    SearchItemKind.File, allowFiles)
                {
                    ExtensionQuery = new IdentifierMatcher(words[0]),
                };
            }

            var containers = words[..^1]
                .Select(word => new IdentifierMatcher(word))
                .ToArray();

            return new SearchQuery(
                new IdentifierMatcher(words[^1]), new IdentifierMatcher(query), containers, only, allowFiles);
        }

        /// <summary>
        /// Every container word must match a segment of the container, in order — "Ranking.Ext"
        /// matches <c>SampleProject.Ranking.RankingExtensions</c> but not the other way round.
        /// The score is weighted up, so a container hit outranks a better bare-name hit.
        /// </summary>
        public bool TryScoreContainer(string container, out int score)
        {
            score = 0;
            if (_containerMatchers.Length == 0)
                return true;

            var segments = container.Split(s_wordSeparators, StringSplitOptions.RemoveEmptyEntries);
            int segment = 0;

            for (int i = 0; i < _containerMatchers.Length; i++)
            {
                bool matched = false;
                while (segment < segments.Length)
                {
                    if (_containerMatchers[i].Match(segments[segment++]) is { } match)
                    {
                        // Weight grows towards the outer words, mirroring ReSharper's Σ (i+1)·score.
                        score += -(int)match.Score * (_containerMatchers.Length - i + 1);
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                    return false;
            }

            return true;
        }
    }
}
