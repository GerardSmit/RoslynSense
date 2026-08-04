using System.Collections.Immutable;
using RoslynMCP.Lsp.Handlers;

namespace RoslynMCP.Languages;

/// <summary>
/// Every language pack this process has registered. A DI singleton, immutable once built:
/// registration is decided by <c>roslynsense.json</c> and the <c>--no-*</c> flags before anything
/// starts, and it governs the MCP tool surface for the whole daemon.
/// </summary>
/// <remarks>
/// Deliberately not where a pack is switched on and off for an editor. The daemon serves several
/// LSP connections and any number of MCP clients from one container, so a per-window setting
/// lives on <see cref="LanguageSession"/>; putting it here would let one editor's preference
/// remove tools from an AI session attached to the same daemon.
/// </remarks>
internal sealed class LanguageRegistry
{
    /// <summary>Pure C#: no packs at all.</summary>
    public static LanguageRegistry Empty { get; } = new([]);

    /// <summary>
    /// The registry the handlers that run outside DI see. Several LSP handlers are static — they
    /// are reached from the JSON-RPC target, from the diagnostics publisher and from the
    /// watched-file debounce, none of which is handed a service provider — and a host builds
    /// exactly one container, so <see cref="Publish"/> is how they reach the same object DI
    /// hands out.
    /// </summary>
    public static LanguageRegistry Current { get; private set; } = Empty;

    private readonly Dictionary<string, ILanguagePack> _byExtension =
        new(StringComparer.OrdinalIgnoreCase);

    public LanguageRegistry(IEnumerable<ILanguagePack> packs)
    {
        Packs = [.. packs];

        foreach (var pack in Packs)
        {
            foreach (string extension in pack.FileExtensions)
                _byExtension.TryAdd(extension, pack);
        }
    }

    public ImmutableArray<ILanguagePack> Packs { get; }

    /// <summary>Makes this the registry <see cref="Current"/> returns. Called once per host, as
    /// the container hands the registry out for the first time.</summary>
    public LanguageRegistry Publish()
    {
        Current = this;
        return this;
    }

    /// <summary>The pack owning this document, or null when C# should answer.</summary>
    public ILanguagePack? Resolve(string? uriOrPath)
    {
        if (uriOrPath is not { Length: > 0 } candidate || IsVirtual(candidate))
            return null;

        string extension = Path.GetExtension(candidate);
        return extension.Length > 0 && _byExtension.TryGetValue(extension, out var pack) ? pack : null;
    }

    /// <summary>The pack owning this document, if it can answer <typeparamref name="TProvider"/>.</summary>
    public TProvider? Resolve<TProvider>(string? uriOrPath) where TProvider : class =>
        Resolve(uriOrPath) as TProvider;

    /// <summary>Whether any pack generated this document. See
    /// <see cref="ILanguagePack.IsProjectionPath"/> for why this is not extension matching.</summary>
    public bool IsProjectionPath(string? filePath) =>
        Packs.Any(pack => pack.IsProjectionPath(filePath));

    /// <summary>Every registered pack that implements <typeparamref name="T"/>, in registration
    /// order.</summary>
    public IReadOnlyList<T> Contributors<T>() where T : class =>
        [.. Packs.OfType<T>()];

    // ---- MCP tool handlers ---------------------------------------------------------------
    //
    // The tools take these as IEnumerable<T> constructor parameters and are unaware of packs;
    // serving them from here is what keeps one registration gate in front of both front-ends.

    public IReadOnlyList<IGoToDefinitionHandler> GoToDefinitionHandlers =>
        Contributors<IGoToDefinitionHandler>();

    public IReadOnlyList<IFindUsagesHandler> FindUsagesHandlers =>
        Contributors<IFindUsagesHandler>();

    public IReadOnlyList<IOutlineHandler> OutlineHandlers =>
        Contributors<IOutlineHandler>();

    public IReadOnlyList<IRenameHandler> RenameHandlers =>
        Contributors<IRenameHandler>();

    public IReadOnlyList<IDiagnosticsHandler> DiagnosticsHandlers =>
        Contributors<IDiagnosticsHandler>();

    /// <summary>
    /// Documents served from memory rather than from disk — generated sources and decompiled
    /// metadata. Their URIs are not file paths and their extension says C#, so no pack may claim
    /// one; the exclusion lives here so every pack inherits it.
    /// </summary>
    private static bool IsVirtual(string uriOrPath) =>
        uriOrPath.StartsWith(VirtualDocumentHandler.GeneratedScheme + ":", StringComparison.Ordinal)
        || uriOrPath.StartsWith(VirtualDocumentHandler.MetadataScheme + ":", StringComparison.Ordinal);
}
