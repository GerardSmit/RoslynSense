using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.EmbeddedLanguages.LanguageServices;
using Microsoft.CodeAnalysis.EmbeddedLanguages;

namespace RoslynMCP.Languages;

/// <summary>
/// The one place that talks to Roslyn's embedded-language detection. Everything else in the
/// server sees <see cref="EmbeddedStringContext"/> and never a Roslyn internal, so a Roslyn
/// upgrade that moves <c>EmbeddedLanguageDetector</c> breaks this file at build and nothing else.
/// </summary>
/// <remarks>
/// Called directly rather than exported into Roslyn's MEF catalog. Composition only sees
/// assemblies the host put in its catalog and Roslyn does not go looking for others, so an
/// <c>IEmbeddedLanguageClassifier</c> of ours would simply never be found; a direct call also
/// skips the <c>Lazy&lt;T, TMetadata&gt;</c> resolution and ordering pass, which matters on a
/// path that runs per keystroke.
/// <para>
/// The detector is built per set of registered identifiers and reused. Construction compiles a
/// regular expression for the <c>// lang=</c> syntax, so building one per request would be the
/// expensive part of an otherwise cheap check.
/// </para>
/// </remarks>
internal sealed class RoslynEmbeddedLanguages
{
    private static readonly EmbeddedLanguageInfo s_info = CSharpEmbeddedLanguagesProvider.Info;

    private static ImmutableArray<IEmbeddedStringLanguage> s_standalone = [];
    private static Memo? s_memo;

    private readonly Dictionary<string, IEmbeddedStringLanguage> _byIdentifier =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly EmbeddedLanguageDetector _detector;

    /// <summary>The languages that claim tokens themselves, in registration order.</summary>
    private readonly ImmutableArray<IConfiguredStringLanguage> _configured;

    public RoslynEmbeddedLanguages(IEnumerable<IEmbeddedStringLanguage> languages)
    {
        Languages = [.. languages];
        _configured = [.. Languages.OfType<IConfiguredStringLanguage>()];

        foreach (var language in Languages)
        {
            foreach (string identifier in language.StringSyntaxIdentifiers)
                _byIdentifier.TryAdd(identifier, language);
        }

        // The comment detector matches only identifiers it was built with, so it has to see the
        // union: a "// lang=graphql" comment is invisible to a detector built for "route" alone.
        var identifiers = ImmutableArray.CreateRange(_byIdentifier.Keys);
        _detector = new EmbeddedLanguageDetector(
            s_info, identifiers, new EmbeddedLanguageCommentDetector(identifiers));
    }

    /// <summary>
    /// The languages in play for this process: every registered pack that also claims string
    /// literals, plus whatever <see cref="Register"/> added.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="LanguageRegistry.Current"/> rather than held beside it, so a pack
    /// that grows an <see cref="IEmbeddedStringLanguage"/> implementation needs no second
    /// registration site. Memoized on the registry's identity — which is fixed once the host has
    /// built its container — because this is read on every completion and definition request.
    /// </remarks>
    public static RoslynEmbeddedLanguages Current
    {
        get
        {
            var registry = LanguageRegistry.Current;
            var standalone = s_standalone;

            if (s_memo is { } memo
                && ReferenceEquals(memo.Registry, registry)
                && memo.Standalone == standalone)
            {
                return memo.Languages;
            }

            var built = new RoslynEmbeddedLanguages(
                [.. registry.Contributors<IEmbeddedStringLanguage>(), .. standalone]);
            s_memo = new Memo(registry, standalone, built);
            return built;
        }
    }

    /// <summary>The registered languages, in registration order.</summary>
    public ImmutableArray<IEmbeddedStringLanguage> Languages { get; }

    /// <summary>Whether any language claims string literals at all.</summary>
    public bool IsEmpty => Languages.IsEmpty;

    /// <summary>
    /// Adds a language that owns no files and therefore cannot be a pack — ASP.NET Core route
    /// templates being the case this exists for.
    /// </summary>
    public static void Register(IEmbeddedStringLanguage language) =>
        s_standalone = s_standalone.Add(language);

    /// <summary>
    /// The embedded language at <paramref name="position"/>, or null when the caret is not in a
    /// string literal or the literal belongs to no registered language.
    /// </summary>
    /// <remarks>
    /// Ordered so the common answer costs nothing: no registered language returns before the
    /// document is touched, a caret outside a literal returns after a syntax-only lookup, and the
    /// semantic model — the expensive part, since resolving <c>[StringSyntax]</c> means binding
    /// the enclosing call — is only asked for once a literal is actually under the caret.
    /// </remarks>
    public async Task<EmbeddedStringContext?> DetectAsync(
        Document document, int position, CancellationToken ct)
    {
        if (IsEmpty || await document.GetSyntaxRootAsync(ct) is not { } root)
            return null;

        var token = root.FindToken(position);
        if (!IsCandidate(token) || !token.Span.Contains(position))
            return null;

        return await DetectAtAsync(document, token, position, ct);
    }

    /// <summary>
    /// Every embedded literal in the document, for the passes that are about a file rather than a
    /// caret — diagnostics above all.
    /// </summary>
    /// <remarks>
    /// This walks every token, which the caret path deliberately does not. Keep it off interactive
    /// requests.
    /// </remarks>
    public async Task<IReadOnlyList<EmbeddedStringContext>> DetectAllAsync(
        Document document, CancellationToken ct)
    {
        if (IsEmpty || await document.GetSyntaxRootAsync(ct) is not { } root)
            return [];

        var candidates = root.DescendantTokens().Where(IsCandidate).ToList();
        if (candidates.Count == 0)
            return [];

        var found = new List<EmbeddedStringContext>();
        foreach (var token in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (await DetectAtAsync(document, token, token.SpanStart, ct) is { } context)
                found.Add(context);
        }

        return found;
    }

    private async Task<EmbeddedStringContext?> DetectAtAsync(
        Document document, SyntaxToken token, int position, CancellationToken ct)
    {
        if (await document.GetSemanticModelAsync(ct) is not { } semanticModel)
            return null;

        if (!_detector.IsEmbeddedLanguageToken(
                token, semanticModel, ct, out string? identifier, out var options)
            || !_byIdentifier.TryGetValue(identifier, out var language))
        {
            return Claimed(document, semanticModel, token, position, ct);
        }

        return new EmbeddedStringContext(
            language,
            identifier,
            options is null ? [] : [.. options],
            document,
            semanticModel,
            token,
            position);
    }

    /// <summary>
    /// The token as an <see cref="IConfiguredStringLanguage"/> claims it, for the literals Roslyn
    /// declined.
    /// </summary>
    /// <remarks>
    /// Here rather than in the callers because this is the one place a token becomes a context, so
    /// the caret path and the document-wide pass both inherit it and no call site moves. Roslyn
    /// first, always: an attribute or a <c>// lang=</c> comment says outright what the literal is,
    /// where a configured language is inferring it from the call around it.
    /// </remarks>
    private EmbeddedStringContext? Claimed(
        Document document, SemanticModel semanticModel, SyntaxToken token, int position,
        CancellationToken ct)
    {
        foreach (var language in _configured)
        {
            if (language.Detect(token, semanticModel, ct) is { } identifier)
            {
                return new EmbeddedStringContext(
                    language, identifier, [], document, semanticModel, token, position);
            }
        }

        return null;
    }

    /// <summary>
    /// The token kinds the detector will even look at: any flavour of string literal, plus the
    /// text of an interpolation's format clause, which is how <c>$"{value:pattern}"</c> reaches an
    /// annotated <c>IFormattable.ToString</c>. Asking the same question the detector asks keeps
    /// the cheap gate from rejecting something it would have accepted.
    /// </summary>
    private static bool IsCandidate(SyntaxToken token) =>
        s_info.IsAnyStringLiteral(token.RawKind)
        || token.RawKind == s_info.SyntaxKinds.InterpolatedStringTextToken;

    private sealed record Memo(
        LanguageRegistry Registry,
        ImmutableArray<IEmbeddedStringLanguage> Standalone,
        RoslynEmbeddedLanguages Languages);
}
