using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Languages;

/// <summary>
/// One language beyond C#, owning its files across both front-ends: the LSP session routes a
/// request here when the document is one of <see cref="FileExtensions"/>, and the MCP tools
/// resolve the same pack out of <see cref="LanguageRegistry"/>.
/// </summary>
/// <remarks>
/// The interface carries identity only. What a pack can actually answer is expressed by which
/// <c>ILanguage*Provider</c> and <c>ILanguage*Contributor</c> interfaces it also implements —
/// dispatch pattern-matches on those, so a pack that implements none is valid and every request
/// about its files falls through to the C# handlers. C# itself is the host language, not a pack.
/// </remarks>
internal interface ILanguagePack
{
    /// <summary>
    /// Stable lowercase id. It is the key under <c>roslynSense.languages.*</c> that enables the
    /// pack for one editor connection, and the value a completion or code-action item puts in its
    /// <c>data</c> payload so a resolve request — which carries no document — can still be routed
    /// back here.
    /// </summary>
    string Id { get; }

    /// <summary>Human-readable name, for progress messages and diagnostics.</summary>
    string DisplayName { get; }

    /// <summary>Extensions this pack owns, each with its leading dot. Matched case-insensitively.</summary>
    ImmutableArray<string> FileExtensions { get; }

    /// <summary>
    /// Whole file names this pack owns, matched case-insensitively and ahead of
    /// <see cref="FileExtensions"/>.
    /// </summary>
    /// <remarks>
    /// For the file types whose extension says less than their name does. <c>packages.config</c> and
    /// <c>nuget.config</c> are NuGet's, while <c>web.config</c> and <c>app.config</c> beside them
    /// belong to the binding-redirect handler — a pack claiming <c>.config</c> would take all four.
    /// Empty for a pack whose extensions already say everything, which is most of them.
    /// </remarks>
    ImmutableArray<string> FileNames => [];

    /// <summary>
    /// Whether this pack owns a file whose name neither <see cref="FileNames"/> nor
    /// <see cref="FileExtensions"/> can express — a family like <c>appsettings*.json</c>, where
    /// the extension would claim every JSON file and no list of exact names can cover the
    /// environment variants. Consulted after exact names and before extensions.
    /// </summary>
    bool OwnsFileName(string fileName) => false;

    /// <summary>What the pack adds to the server's advertised capabilities when it is enabled.</summary>
    LanguageCapabilities Capabilities { get; }

    /// <summary>
    /// Fully-qualified metadata names that must resolve in a compilation before this pack has
    /// anything to say about it. Resolving them once per compilation and skipping the pack when
    /// none are present is what keeps a pack free in a solution that does not use it — the same
    /// gate Roslyn's own analyzers apply from <c>RegisterCompilationStartAction</c>.
    /// </summary>
    ImmutableArray<string> WellKnownTypeNames { get; }

    /// <summary>
    /// The symbol kinds a contributor pass has to look at. A contributor asked about a symbol of
    /// any other kind can be skipped without loading the pack's index.
    /// </summary>
    ImmutableArray<SymbolKind> InterestingSymbolKinds { get; }

    /// <summary>
    /// Whether this path is one of the pack's synthetic documents — the C# the pack projects its
    /// markup into, which Roslyn treats as a real document but no editor can open.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="FileExtensions"/>: a projection is a <c>.cs</c> file
    /// as far as its extension goes, so extension matching would send requests about it back to
    /// the pack that generated it instead of to Roslyn, which is exactly wrong. The two questions
    /// are "do you own this file type" and "did you invent this file".
    /// </remarks>
    bool IsProjectionPath(string? filePath);
}

/// <summary>
/// A pack's contribution to <c>initialize</c>. Kept declarative rather than letting a pack build
/// its own capability objects, because several packs and the C# host have to be merged into one
/// answer — trigger characters union, the semantic-token legend concatenates, and the offsets
/// that result are per-connection state on <see cref="LanguageSession"/>.
/// </summary>
internal sealed record LanguageCapabilities(
    ImmutableArray<string> CompletionTriggerCharacters,
    ImmutableArray<string> SignatureHelpTriggerCharacters,
    ImmutableArray<string> Commands,
    ImmutableArray<string> FileOperationGlobs,
    ImmutableArray<string> SemanticTokenTypes,
    ImmutableArray<string> SemanticTokenModifiers,
    bool SupportsBreakpoints)
{
    /// <summary>A pack that adds nothing to the C# capabilities.</summary>
    public static LanguageCapabilities None { get; } =
        new([], [], [], [], [], [], SupportsBreakpoints: false);
}
