using System.Collections.Immutable;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages;

/// <summary>
/// The packs one editor connection has switched on, and the semantic-token numbering that
/// follows from that set. Constructed per LSP session, immediately after the client's
/// initialization options have been read and before capabilities are built.
/// </summary>
/// <remarks>
/// Per-connection rather than per-process on purpose. The daemon serves several editor windows
/// and any number of MCP clients from one container, so a language toggle held in process-global
/// state would let one window silently deactivate a pack under another — and the semantic-token
/// legend genuinely differs between sessions, because it is the union of whatever that session
/// enabled. A session with no packs is pure C# fallback, which is also what a directly
/// constructed <c>LspServer</c> gets in tests.
/// </remarks>
internal sealed class LanguageSession
{
    /// <summary>No packs: every request goes to the C# handlers.</summary>
    public static LanguageSession Empty { get; } = new([]);

    private readonly Dictionary<string, ILanguagePack> _byExtension =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _tokenTypeOffsets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _tokenModifierOffsets = new(StringComparer.Ordinal);

    public LanguageSession(IEnumerable<ILanguagePack> packs)
        : this(packs, static _ => true)
    {
    }

    public LanguageSession(IEnumerable<ILanguagePack> packs, Func<ILanguagePack, bool> isEnabled)
    {
        Packs = [.. packs.Where(isEnabled)];

        foreach (var pack in Packs)
        {
            foreach (string extension in pack.FileExtensions)
                _byExtension.TryAdd(extension, pack);
        }

        var types = new List<string>(SemanticTokensHandler.TokenTypes);
        var modifiers = new List<string>(SemanticTokensHandler.TokenModifiers);

        foreach (var pack in Packs)
        {
            _tokenTypeOffsets[pack.Id] = types.Count;
            types.AddRange(pack.Capabilities.SemanticTokenTypes);

            _tokenModifierOffsets[pack.Id] = modifiers.Count;
            modifiers.AddRange(pack.Capabilities.SemanticTokenModifiers);
        }

        Legend = new SemanticTokensLegend([.. types], [.. modifiers]);
    }

    /// <summary>The packs enabled for this connection, in registration order.</summary>
    public ImmutableArray<ILanguagePack> Packs { get; }

    /// <summary>
    /// The combined legend: C#'s types and modifiers first, then each enabled pack's own, in the
    /// order the packs appear. C# keeps the low indices so its numbering is stable no matter what
    /// else is on.
    /// </summary>
    public SemanticTokensLegend Legend { get; }

    public bool IsEnabled(string id) =>
        Packs.Any(pack => pack.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The enabled pack owning this document, or null when C# should answer.</summary>
    public ILanguagePack? Resolve(string? uriOrPath)
    {
        if (uriOrPath is not { Length: > 0 } candidate || IsVirtual(candidate))
            return null;

        string extension = Path.GetExtension(candidate);
        return extension.Length > 0 && _byExtension.TryGetValue(extension, out var pack) ? pack : null;
    }

    /// <summary>The enabled pack owning this document, if it can answer
    /// <typeparamref name="TProvider"/>.</summary>
    public TProvider? Resolve<TProvider>(string? uriOrPath) where TProvider : class =>
        Resolve(uriOrPath) as TProvider;

    /// <summary>Whether an enabled pack generated this document.</summary>
    public bool IsProjectionPath(string? filePath) =>
        Packs.Any(pack => pack.IsProjectionPath(filePath));

    /// <summary>Every enabled pack that implements <typeparamref name="T"/>.</summary>
    public IReadOnlyList<T> Contributors<T>() where T : class =>
        [.. Packs.OfType<T>()];

    /// <summary>
    /// Where this pack's declared token types start in <see cref="Legend"/>. The pack emits
    /// <c>offset + i</c> for the i-th name it declared.
    /// </summary>
    public int TokenTypeOffset(ILanguagePack pack) =>
        _tokenTypeOffsets.TryGetValue(pack.Id, out int offset) ? offset : 0;

    /// <summary>
    /// Where this pack's declared token modifiers start. Modifiers are a bitmask, so this is a
    /// shift count: the pack's i-th modifier is bit <c>offset + i</c>.
    /// </summary>
    public int TokenModifierOffset(ILanguagePack pack) =>
        _tokenModifierOffsets.TryGetValue(pack.Id, out int offset) ? offset : 0;

    /// <summary>
    /// The legend index of a token type C# already defines, or -1. A pack whose token is really a
    /// class or a property uses this instead of declaring the name again, so the client's theme
    /// colours it the way it colours the C# one.
    /// </summary>
    public static int SharedTokenType(string name) =>
        Array.IndexOf(SemanticTokensHandler.TokenTypes, name);

    /// <summary>The bit of a token modifier C# already defines, or -1.</summary>
    public static int SharedTokenModifier(string name) =>
        Array.IndexOf(SemanticTokensHandler.TokenModifiers, name);

    /// <summary>Mirrors <see cref="LanguageRegistry"/>: generated and decompiled documents belong
    /// to no pack whatever their extension looks like.</summary>
    private static bool IsVirtual(string uriOrPath) =>
        uriOrPath.StartsWith(VirtualDocumentHandler.GeneratedScheme + ":", StringComparison.Ordinal)
        || uriOrPath.StartsWith(VirtualDocumentHandler.MetadataScheme + ":", StringComparison.Ordinal);
}
