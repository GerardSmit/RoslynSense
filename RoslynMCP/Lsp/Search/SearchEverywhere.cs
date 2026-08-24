using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Completion;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Languages.DotSettings.Core;

namespace RoslynMCP.Lsp.Search;

public enum SearchItemKind
{
    Type,
    Member,
    File,
}

/// <summary>One ranked result. <paramref name="Score"/> is lower-is-better.</summary>
/// <param name="Uri">Set for results with no file behind them (decompiled metadata); when null
/// the client derives the URI from <paramref name="FilePath"/>.</param>
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
    int Score,
    string? Uri = null);

/// <summary>
/// Search Everywhere: one query box over types, members and files, ranked the way ReSharper's
/// Go to Everything ranks — kind before name quality, and the tiering is arithmetic rather than a
/// sort of sorts.
/// </summary>
/// <remarks>
/// Two things carry the design. First, tier arithmetic: a match score is turned into
/// <c>-score - TierUnit * tier</c>, where one tier step outweighs every possible match score, so
/// "which kind of thing is this" strictly dominates "how well does the name match" without either
/// being thrown away. Second, names are matched before symbols exist — the corpus is Roslyn's
/// per-document declaration index, so a search costs a parse at worst and never a compilation.
/// </remarks>
public static class SearchEverywhere
{
    /// <summary>Bigger than any <see cref="MatcherScore"/>, so a tier can never be out-scored.</summary>
    private const int TierUnit = 0x1000;

    private const int TierExactType = 6;

    /// <summary>
    /// A type the query named by a whole run of its words — "ShopController" against
    /// <c>SomePrefixShopController</c>.
    /// </summary>
    /// <remarks>
    /// Above an exact member on purpose, and it is the one ordering here that is not obvious.
    /// Exactness used to win outright, so a property called <c>ShopController</c> came back ahead
    /// of the type whose name merely ends in it — and someone typing a type's name in a codebase
    /// whose types all carry a house prefix could not reach the type at all. Naming most of a type
    /// is a stronger thing to have said than naming all of a property.
    /// </remarks>
    private const int TierWholeWordType = 5;

    /// <summary>
    /// Methods above the members that are not methods, at both levels of exactness.
    /// </summary>
    /// <remarks>
    /// A method is a place code lives; a property or a field is usually a place it is stored. When
    /// both match a query equally well the one worth opening is the method — and a single member
    /// tier meant a type's backing fields sat among the methods that share their name.
    /// </remarks>
    private const int TierExactMethod = 4;

    private const int TierExactMember = 3;
    private const int TierExactFile = 3;
    private const int TierType = 2;
    private const int TierFile = 2;
    private const int TierMethod = 1;
    private const int TierMember = 0;

    /// <summary>
    /// Pushes a tool-written file — and anything declared in one — below every hand-written
    /// result, whatever its tier. Bigger than the whole tier range on purpose: a generated exact
    /// type match still belongs under a hand-written fuzzy one.
    /// </summary>
    private const int GeneratedPenalty = TierUnit * 8;

    /// <summary>
    /// Images, archives, media: named like anything else, but nobody searching "Sho" wants a
    /// screenshot ahead of ShopController. Below generated code — an asset is not even code —
    /// yet still above metadata, because it is at least part of the solution.
    /// </summary>
    private const int BinaryAssetPenalty = TierUnit * 12;

    /// <summary>
    /// Below even generated solution code: a type from a referenced assembly is an answer of
    /// last resort, the way Rider ranks non-project items once they are included at all.
    /// </summary>
    private const int MetadataPenalty = TierUnit * 16;

    private static readonly char[] s_wordSeparators = ['.', '/', '\\', ' ', '+'];

    /// <param name="includeFiles">workspace/symbol has no kind for a file, so it asks for
    /// symbols only; the extension's own Search Everywhere wants both.</param>
    /// <param name="only">Restricts to one kind — the panel's Classes/Files/Symbols tabs. Wins
    /// over a <c>t:</c>/<c>m:</c>/<c>f:</c> prefix in the query.</param>
    /// <param name="includeMetadata">Also searches the public types of referenced assemblies —
    /// Rider's "include non-solution items". Those hits open as decompiled documents.</param>
    public static async Task<IReadOnlyList<SearchHit>> SearchAsync(
        Solution solution, string query, int maxResults, CancellationToken ct,
        bool includeFiles = true, SearchItemKind? only = null, bool includeMetadata = false)
    {
        var request = SearchQuery.Parse(query, includeFiles, only);
        if (request is null)
            return [];

        var hits = new List<SearchHit>();

        if (request.IncludesSymbols)
            hits.AddRange(await FindSymbolsAsync(solution, request, ct));

        if (request.IncludesFiles)
            hits.AddRange(await FindFilesAsync(solution, request, ct));

        if (includeMetadata && request.IncludesTypes)
            hits.AddRange(await Task.Run(() => FindMetadataTypes(solution, request, ct), ct));

        hits.Sort(Compare);

        // Linked documents (one file in several target frameworks) declare the same symbol once
        // per project; the user wants one row. The Uri is part of the key because metadata hits
        // all share position 0 in the same assembly: Func`1..Func`17 and every nested Builder
        // differ only in the reflection name their Uri carries.
        var seen = new HashSet<(string, string?, string, int, int, string?)>();
        var deduped = new List<SearchHit>(Math.Min(hits.Count, maxResults));
        foreach (var hit in hits)
        {
            if (!seen.Add((hit.Name, hit.Container, hit.FilePath, hit.Line, hit.Character, hit.Uri)))
                continue;

            deduped.Add(hit);
            if (deduped.Count == maxResults)
                break;
        }

        return deduped;
    }

    /// <summary>
    /// The methods a query names, best first — the same ranking as the search box, restricted to
    /// methods, and answering with symbols rather than with places in files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For naming a method in configuration, where what is known is the method and what is not is
    /// the namespace it lives in. <c>GetString</c> finds every one of them;
    /// <c>DotNetNuke.GetString</c> keeps the ones whose container says so — the query grammar is
    /// the search box's, so anyone who has used Ctrl+T already knows it.
    /// </para>
    /// <para>
    /// Only <see cref="MethodKind.Ordinary"/>. A constructor is reached by its type's name and an
    /// accessor by its property's, and neither is a thing configuration can name.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<IMethodSymbol>> FindMethodsAsync(
        Solution solution, string query, int maxResults, CancellationToken ct) =>
        [.. (await FindMembersAsync(
            solution, query, maxResults,
            symbol => symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary }
                && !symbol.IsImplicitlyDeclared,
            ct)).Cast<IMethodSymbol>()];

    /// <summary>
    /// The same, for whichever kinds of member the caller accepts — a property or a field as
    /// readily as a method.
    /// </summary>
    /// <remarks>
    /// Configuration names members that are not methods: a set of allowed string values is carried
    /// as often by an entity's <c>Code</c> property as by a call taking one. The predicate rather
    /// than a kind flag because the callers already own the question — a setting that accepts a
    /// property but not an indexer is answering it more precisely than an enum here could.
    /// </remarks>
    public static async Task<IReadOnlyList<ISymbol>> FindMembersAsync(
        Solution solution, string query, int maxResults, Func<ISymbol, bool> accept,
        CancellationToken ct)
    {
        var request = SearchQuery.Parse(query, allowFiles: false, forcedOnly: SearchItemKind.Member);
        if (request is null)
            return [];

        var matcher = request.NameMatcher;

        // The predicate sees declaration names, not symbols: everything that fails here costs
        // nothing beyond the match itself.
        var symbols = await SymbolFinder.FindSourceDeclarationsAsync(
            solution, name => matcher.Match(name) is not null, SymbolFilter.Member, ct);

        var found = new List<(int Score, ISymbol Member)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbol in symbols)
        {
            ct.ThrowIfCancellationRequested();

            if (!accept(symbol)
                || matcher.Match(symbol.Name) is not { } match
                || !request.TryScoreContainer(
                    symbol.ContainingType?.ToDisplayString()
                        ?? symbol.ContainingNamespace?.ToDisplayString()
                        ?? "",
                    out int containerScore))
            {
                continue;
            }

            var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (location?.SourceTree?.FilePath is not { Length: > 0 } path
                || SearchFileRules.IsExcluded(path)
                || DotSettingsExclusions.IsExcluded(path))
            {
                continue;
            }

            // One file compiled for several target frameworks declares the same member once per
            // project, and a list offering the same call three times is a list nobody trusts.
            if (!seen.Add(symbol.ToDisplayString()))
                continue;

            found.Add((
                Score(
                    match.Score,
                    Tier(DeclaredKind(symbol), SearchItemKind.Member, match.Score),
                    containerScore,
                    SearchFileRules.IsGenerated(path)),
                symbol));
        }

        found.Sort((x, y) => x.Score != y.Score
            ? x.Score.CompareTo(y.Score)
            : string.CompareOrdinal(x.Member.ToDisplayString(), y.Member.ToDisplayString()));

        return [.. found.Take(maxResults).Select(entry => entry.Member)];
    }

    /// <summary>
    /// The bound symbol said in the terms the ranking uses, which are the parser's — so a method
    /// keeps the tier a method has and a property does not borrow it.
    /// </summary>
    private static DeclaredSymbolInfoKind DeclaredKind(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } =>
            DeclaredSymbolInfoKind.Constructor,
        IMethodSymbol { IsExtensionMethod: true } => DeclaredSymbolInfoKind.ExtensionMethod,
        IMethodSymbol => DeclaredSymbolInfoKind.Method,
        IPropertySymbol { IsIndexer: true } => DeclaredSymbolInfoKind.Indexer,
        IPropertySymbol => DeclaredSymbolInfoKind.Property,
        IFieldSymbol { IsConst: true } => DeclaredSymbolInfoKind.Constant,
        IFieldSymbol => DeclaredSymbolInfoKind.Field,
        IEventSymbol => DeclaredSymbolInfoKind.Event,
        _ => DeclaredSymbolInfoKind.Field,
    };

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

    /// <summary>
    /// Declarations, matched out of Roslyn's per-document index rather than out of its
    /// compilations — the corpus NavigateTo searches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SymbolFinder.FindSourceDeclarationsAsync</c> read the same names, but it walked projects
    /// one at a time and built each one's <c>Compilation</c> to do it, then bound a real
    /// <c>ISymbol</c> for every hit. That is a solution-wide compile inside a keystroke's budget:
    /// seven seconds cold on a mid-sized solution, and paid again on every character until the
    /// caches were full. <see cref="TopLevelSyntaxTreeIndex"/> holds the declared names of one
    /// document, derived from its syntax alone, persisted between sessions, and buildable for one
    /// document without touching another — so the sweep is a parallel loop over documents that
    /// never asks for a compilation at all.
    /// </para>
    /// <para>
    /// The price is that a <see cref="DeclaredSymbolInfo"/> is what the parser saw, not what the
    /// compiler resolved: the kind comes from the declaration's syntax, and the container is the
    /// qualified name written around it. Nothing here needed more than that — the ranking has
    /// always run on names.
    /// </para>
    /// </remarks>
    private static async Task<IEnumerable<SearchHit>> FindSymbolsAsync(
        Solution solution, SearchQuery request, CancellationToken ct)
    {
        var documents = new List<Document>();
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                // Build output is not a place anyone navigates to, and it is where every SDK
                // project's generated AssemblyInfo lives. A .DotSettings layer adds whatever the
                // team excluded on top of that. Asked once per document rather than once per hit,
                // which is also what keeps the excluded documents from being indexed at all.
                if (document.FilePath is not { Length: > 0 } path
                    || SearchFileRules.IsExcluded(path)
                    || DotSettingsExclusions.IsExcluded(path))
                {
                    continue;
                }

                documents.Add(document);
            }
        }

        var perDocument = new ConcurrentBag<List<SearchHit>>();

        await Parallel.ForEachAsync(
            documents,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Environment.ProcessorCount,
            },
            async (document, token) =>
            {
                var hits = await MatchDeclarationsAsync(document, request, token);
                if (hits.Count > 0)
                    perDocument.Add(hits);
            });

        return perDocument.SelectMany(hits => hits);
    }

    /// <summary>One document's declarations, matched and scored.</summary>
    private static async Task<List<SearchHit>> MatchDeclarationsAsync(
        Document document, SearchQuery request, CancellationToken ct)
    {
        TopLevelSyntaxTreeIndex? index;
        try
        {
            index = await TopLevelSyntaxTreeIndex.GetIndexAsync(document, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A document that will not parse — a stale generated file, a file the workspace has
            // since lost — is one missing row, never a failed search.
            return [];
        }

        if (index is null)
            return [];

        var matcher = request.NameMatcher;
        List<(DeclaredSymbolInfo Info, SearchItemKind Kind, MatcherScore Score, int Container)>? matched = null;

        foreach (var info in index.DeclaredSymbolInfos)
        {
            ct.ThrowIfCancellationRequested();

            if (KindOf(info.Kind) is not { } kind)
                continue;

            if (kind == SearchItemKind.Type ? !request.IncludesTypes : !request.IncludesMembers)
                continue;

            if (matcher.Match(info.Name) is not { } match)
                continue;

            if (!request.TryScoreContainer(info.FullyQualifiedContainerName, out int containerScore))
                continue;

            (matched ??= []).Add((info, kind, match.Score, containerScore));
        }

        if (matched is null)
            return [];

        // Only a document that matched something pays for its text. The index stores spans as
        // offsets, and a line-and-column is what a client can open.
        SourceText text;
        try
        {
            text = await document.GetTextAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same contract as the index above: a file deleted out from under the workspace is
            // this document's rows lost, not the search's.
            return [];
        }
        string path = document.FilePath!;
        bool isGenerated = SearchFileRules.IsGenerated(path);

        var hits = new List<SearchHit>(matched.Count);
        foreach (var (info, kind, score, containerScore) in matched)
        {
            // An index restored from disk can outlive the text it described by a moment; a span
            // past the end of the file would throw rather than merely point somewhere odd.
            if (info.Span.End > text.Length)
                continue;

            var span = text.Lines.GetLinePositionSpan(info.Span);
            string container = info.FullyQualifiedContainerName;

            hits.Add(new SearchHit(
                kind,
                info.Name,
                container.Length == 0 ? null : container,
                path,
                span.Start.Line,
                span.Start.Character,
                span.End.Line,
                span.End.Character,
                ToLspSymbolKind(info.Kind),
                Score(score, Tier(info.Kind, kind, score), containerScore, isGenerated)));
        }

        return hits;
    }

    /// <summary>
    /// Which of the two symbol rows a declaration is, or null for the kinds Search Everywhere
    /// never offers — a namespace is not something anyone navigates to by name.
    /// </summary>
    private static SearchItemKind? KindOf(DeclaredSymbolInfoKind kind) => kind switch
    {
        DeclaredSymbolInfoKind.Namespace => null,
        DeclaredSymbolInfoKind.Class
            or DeclaredSymbolInfoKind.Delegate
            or DeclaredSymbolInfoKind.Enum
            or DeclaredSymbolInfoKind.Interface
            or DeclaredSymbolInfoKind.Module
            or DeclaredSymbolInfoKind.Record
            or DeclaredSymbolInfoKind.RecordStruct
            or DeclaredSymbolInfoKind.Struct
            or DeclaredSymbolInfoKind.Union => SearchItemKind.Type,
        _ => SearchItemKind.Member,
    };

    private static int ToLspSymbolKind(DeclaredSymbolInfoKind kind) => kind switch
    {
        DeclaredSymbolInfoKind.Interface => LspSymbolKind.Interface,
        DeclaredSymbolInfoKind.Struct or DeclaredSymbolInfoKind.RecordStruct => LspSymbolKind.Struct,
        DeclaredSymbolInfoKind.Enum => LspSymbolKind.Enum,
        DeclaredSymbolInfoKind.Delegate => LspSymbolKind.Function,
        DeclaredSymbolInfoKind.Constructor => LspSymbolKind.Constructor,
        DeclaredSymbolInfoKind.Method
            or DeclaredSymbolInfoKind.ExtensionMethod
            or DeclaredSymbolInfoKind.Operator => LspSymbolKind.Method,
        DeclaredSymbolInfoKind.Property or DeclaredSymbolInfoKind.Indexer => LspSymbolKind.Property,
        DeclaredSymbolInfoKind.EnumMember => LspSymbolKind.EnumMember,
        DeclaredSymbolInfoKind.Constant => LspSymbolKind.Constant,
        DeclaredSymbolInfoKind.Field => LspSymbolKind.Field,
        DeclaredSymbolInfoKind.Event => LspSymbolKind.Event,
        _ => LspSymbolKind.Class,
    };

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
                request.TargetLine ?? 0,
                request.TargetColumn ?? 0,
                request.TargetLine ?? 0,
                request.TargetColumn ?? 0,
                LspSymbolKind.File,
                Score(match.Value.Score, tier, containerScore, SearchFileRules.IsGenerated(path))
                    + (SearchFileRules.IsBinaryAsset(path) ? BinaryAssetPenalty : 0)));
        }

        return hits;
    }

    /// <summary>
    /// Public types of every referenced assembly, matched against the same query. Synchronous on
    /// purpose — the index is metadata-table reads and string matching, no compilation involved.
    /// </summary>
    private static List<SearchHit> FindMetadataTypes(
        Solution solution, SearchQuery request, CancellationToken ct)
    {
        var matcher = request.NameMatcher;
        var hits = new List<SearchHit>();

        foreach (var (assemblyPath, types) in MetadataTypeIndex.ForSolution(solution, ct))
        {
            ct.ThrowIfCancellationRequested();

            foreach (var type in types)
            {
                if (matcher.Match(type.Name) is not { } match)
                    continue;

                if (!request.TryScoreContainer(type.Namespace, out int containerScore))
                    continue;

                int tier = match.Score.IsExactMatch() ? TierExactType : TierType;
                hits.Add(new SearchHit(
                    SearchItemKind.Type,
                    type.Name,
                    type.Namespace.Length == 0 ? null : type.Namespace,
                    assemblyPath,
                    0,
                    0,
                    0,
                    0,
                    LspSymbolKind.Class,
                    Score(match.Score, tier, containerScore, isGenerated: false) + MetadataPenalty,
                    Handlers.VirtualDocumentHandler.UriFor(
                        Handlers.VirtualDocumentHandler.MetadataScheme, assemblyPath, type.ReflectionName)));
            }
        }

        return hits;
    }

    /// <summary>What kind of answer this is, ranked — see the tier constants for why.</summary>
    private static int Tier(DeclaredSymbolInfoKind kind, SearchItemKind item, MatcherScore score)
    {
        if (item == SearchItemKind.Type)
        {
            if (score.IsExactMatch())
                return TierExactType;

            return score.IsWholeWordMatch() ? TierWholeWordType : TierType;
        }

        // Constructors are reached by their type's name, which is the better answer anyway — they
        // carry that name, so leaving them out of the method tier keeps the type ahead of them.
        bool isMethod = kind
            is DeclaredSymbolInfoKind.Method
            or DeclaredSymbolInfoKind.ExtensionMethod
            or DeclaredSymbolInfoKind.Operator;

        if (score.IsExactMatch())
            return isMethod ? TierExactMethod : TierExactMember;

        return isMethod ? TierMethod : TierMember;
    }

    /// <summary>
    /// Tier arithmetic plus the container words. Container words weigh more than the name itself
    /// (ReSharper's <c>W(x) = Σ (i+1)·score</c>): having typed a container, the user has said
    /// something stronger about where the thing lives than about what it is called.
    /// </summary>
    private static int Score(MatcherScore nameScore, int tier, int containerScore, bool isGenerated) =>
        -(int)nameScore - TierUnit * tier + containerScore + (isGenerated ? GeneratedPenalty : 0);

    /// <summary>
    /// A parsed query: an optional kind filter, the container words, and the last word — the one
    /// that names the thing being looked for.
    /// </summary>
    private sealed class SearchQuery
    {
        /// <summary>"line" in the languages .NET localises compiler messages to.</summary>
        private const string LineWords =
            "line|regel|zeile|ligne|línea|linea|riga|linha|linia|wiersz|rad|linje|rivi|satır|строка|行";

        /// <summary>
        /// A trailing line reference, the shapes a pasted stack trace or compiler message uses:
        /// <c>Customer.cs:851</c>, <c>Customer.cs:851:12</c>, <c>Customer.cs(851,12)</c>, and the
        /// worded form <c>Customer.cs:line 851</c> / <c>:regel 851</c>. The word is required when
        /// only a space separates it — "Form 12" is a name, "Form line 12" is a location.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex s_lineReference = new(
            $@"^(?<rest>.*?)(?:[:(]\s*(?<word>{LineWords})?\s*(?<line>\d+)(?:\s*[:,]\s*(?<column>\d+))?\s*\)?|\s+(?<word>{LineWords})\s*(?<line>\d+)(?:\s*[:,]\s*(?<column>\d+))?)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant
            | System.Text.RegularExpressions.RegexOptions.Compiled);
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

        /// <summary>0-based line from a trailing <c>:851</c> / <c>:line 851</c>; file hits open there.</summary>
        public int? TargetLine { get; private init; }

        public int? TargetColumn { get; private init; }

        public SearchItemKind? Only { get; }

        public bool IncludesTypes => Only is null or SearchItemKind.Type;
        public bool IncludesMembers => Only is null or SearchItemKind.Member;
        public bool IncludesFiles => _allowFiles && Only is null or SearchItemKind.File;
        public bool IncludesSymbols => IncludesTypes || IncludesMembers;

        /// <summary>
        /// Splits "Ns.Type.Member" or "dir/file" into container words plus the final name.
        /// Returns null for a query with nothing to search for.
        /// </summary>
        public static SearchQuery? Parse(string query, bool allowFiles = true, SearchItemKind? forcedOnly = null)
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

            // A tab in the panel is a stronger statement than a prefix in the query.
            if (forcedOnly is not null)
                only = forcedOnly;

            int? targetLine = null;
            int? targetColumn = null;
            if (allowFiles
                && only is null or SearchItemKind.File
                && s_lineReference.Match(query) is { Success: true } lineRef
                && lineRef.Groups["rest"].Value.Trim() is { Length: > 0 } beforeLine
                && int.TryParse(lineRef.Groups["line"].Value, out int oneBasedLine))
            {
                targetLine = Math.Max(0, oneBasedLine - 1);
                if (int.TryParse(lineRef.Groups["column"].Value, out int oneBasedColumn))
                    targetColumn = Math.Max(0, oneBasedColumn - 1);
                query = beforeLine;

                // "Customer.cs:851" is a navigation, not a name: the line applies to files, so
                // the search narrows to them. But "Parse(0" or "Foo:2" names no file — without a
                // dot or a "line" word, the trailing digits still strip (so the symbol is found)
                // while the symbol results stay.
                if (lineRef.Groups["word"].Success || beforeLine.Contains('.'))
                    only = SearchItemKind.File;
            }

            var words = query.Split(s_wordSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                return null;

            // A query that is nothing but an extension asks for a kind of file. Symbols are
            // excluded outright: nobody typing ".proto" wants a type called Proto — unless a
            // symbols-only tab is forced, where a file answer would be the wrong kind entirely.
            if (query[0] == '.' && words.Length == 1 && only is null or SearchItemKind.File)
            {
                return new SearchQuery(
                    new IdentifierMatcher(words[0]), new IdentifierMatcher(query), [],
                    SearchItemKind.File, allowFiles)
                {
                    ExtensionQuery = new IdentifierMatcher(words[0]),
                    TargetLine = targetLine,
                    TargetColumn = targetColumn,
                };
            }

            var containers = words[..^1]
                .Select(word => new IdentifierMatcher(word))
                .ToArray();

            return new SearchQuery(
                new IdentifierMatcher(words[^1]), new IdentifierMatcher(query), containers, only, allowFiles)
            {
                TargetLine = targetLine,
                TargetColumn = targetColumn,
            };
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
