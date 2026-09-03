using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Resources;

/// <summary>
/// <c>.resx</c> as a pack: the resource catalog behind key navigation in C# and markup, and the
/// owner of the resx buffer itself.
/// </summary>
/// <remarks>
/// A pack rather than a peer service because pack identity is what wires everything the feature
/// needs — contributors are reached through <c>Packs.OfType&lt;T&gt;()</c>, and the same id is the
/// watcher glob, the document selector, and the <c>--no-resources</c> gate.
/// <para>
/// <see cref="Id"/> is the file format and not the feature, because the id has to be the VS Code
/// language id as well: it is the key the document selector and <c>roslynSense.languages.*</c>
/// both use, and a language id of "resources" would name something VS Code has no concept of.
/// The CLI flag and the <c>roslynsense.json</c> section keep the feature's name.
/// </para>
/// </remarks>
internal sealed partial class ResourcesLanguage : ILanguagePack
{
    public ResourcesLanguage(EffectiveSettings settings) => Settings = settings.Resources;

    /// <summary>The lookups, conventions and discovery globs this process resolved — the preset
    /// merged with whatever <c>roslynsense.json</c> declared.</summary>
    public ResourceSettings Settings { get; }

    public string Id => "resx";

    public string DisplayName => ".NET Resources";

    public ImmutableArray<string> FileExtensions { get; } = [".resx"];

    /// <summary>
    /// A <c>.resx</c> is data: nothing in it completes, no command acts on it, and there is no
    /// statement in it to break on. The one thing the pack needs advertised is the file-operation
    /// glob, because renaming a resource file has to carry its translations, its customizations and
    /// its generated designer along with it.
    /// </summary>
    public LanguageCapabilities Capabilities { get; } = new(
        CompletionTriggerCharacters: [],
        SignatureHelpTriggerCharacters: [],
        Commands: [],
        FileOperationGlobs: ["**/*.resx"],
        SemanticTokenTypes: [],
        SemanticTokenModifiers: [],
        SupportsBreakpoints: false);

    /// <summary>
    /// The type every generated <c>*.Designer.cs</c> and every <c>App_GlobalResources</c> class
    /// goes through. Deliberately not a DNN type: gating on
    /// <c>DotNetNuke.Services.Localization.Localization</c> would switch the pack off for stock
    /// ASP.NET resources and for strongly-typed designer classes, neither of which knows DNN
    /// exists.
    /// </summary>
    public ImmutableArray<string> WellKnownTypeNames { get; } = ["System.Resources.ResourceManager"];

    /// <summary>
    /// A resource key is not a symbol, so the only symbols a contributor pass can say anything
    /// about are the strongly-typed wrappers: the generated class and its per-key properties.
    /// </summary>
    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } =
        [SymbolKind.NamedType, SymbolKind.Property];

    /// <summary>A <c>.resx</c> is read, never projected into C#.</summary>
    public bool IsProjectionPath(string? filePath) => false;

    /// <summary>
    /// The project's resource families, under this process's configured globs.
    /// </summary>
    /// <remarks>
    /// Routed through <see cref="ProjectIndexCacheService"/> rather than straight at
    /// <see cref="ResourceCatalogService"/> so that both freshness mechanisms are in front of every
    /// caller: the editor's watched-file notifications, and the <see cref="FileSystemWatcher"/>
    /// that is all an MCP session has.
    /// </remarks>
    public Task<ResourceCatalog> CatalogAsync(Project project, CancellationToken cancellationToken = default) =>
        ProjectIndexCacheService.GetResourceCatalogAsync(project, Settings.Discovery, cancellationToken);
}
