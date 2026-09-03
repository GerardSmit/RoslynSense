using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.MsBuild.Core;

namespace RoslynMCP.Languages.MsBuild;

/// <summary>
/// Project files — <c>.csproj</c> and its siblings, the <c>.props</c> and <c>.targets</c> they
/// import, and NuGet's own <c>packages.config</c> and <c>nuget.config</c>.
/// </summary>
/// <remarks>
/// <para>
/// The daemon already knows everything these files say. It evaluates them, it knows which packages
/// are outdated, and it knows which versions carry advisories — but none of that reached the buffer,
/// so a <c>.csproj</c> opened as inert XML. This pack is the seam that puts the answers on the spans
/// they belong to.
/// </para>
/// <para>
/// A pack rather than a handler beside <c>BindingRedirectHandler</c>, which answers about
/// <c>web.config</c> without being a language. That shape is right for one file name and one
/// question; this is five file types across seven LSP endpoints with a per-window switch, which is
/// what the pack model exists for.
/// </para>
/// </remarks>
internal sealed partial class MsBuildLanguage : ILanguagePack
{
    public string Id => "msbuild";

    public string DisplayName => "MSBuild Project Files";

    public ImmutableArray<string> FileExtensions { get; } = [.. MsBuildFile.Extensions];

    /// <summary>
    /// NuGet's two, claimed by name.
    /// </summary>
    /// <remarks>
    /// Claiming <c>.config</c> would also take <c>web.config</c> and <c>app.config</c>, which belong
    /// to the binding-redirect handler and are answered ahead of pack dispatch — the pack would
    /// silently take their diagnostics and quick fixes with it.
    /// </remarks>
    public ImmutableArray<string> FileNames { get; } = [.. MsBuildFile.Names];

    /// <summary>
    /// What opens a name here. <c>&lt;</c> starts an element and <c>"</c> and <c>'</c> open an
    /// attribute value — the two places a project file names anything. <c>.</c> continues a version
    /// and a target framework, both of which are typed a segment at a time. <c>/</c> and <c>\</c>
    /// are path separators, so each one opens the next directory of an <c>Include=</c>.
    /// </summary>
    /// <remarks>
    /// No signature-help characters: nothing in the grammar takes an argument list. No commands
    /// either — opening the NuGet panel is the extension's command, carried on a code lens, because
    /// the panel is the client's and the server has no way to show it.
    /// </remarks>
    public LanguageCapabilities Capabilities { get; } = new(
        CompletionTriggerCharacters: ["<", "\"", "'", ".", "/", "\\"],
        SignatureHelpTriggerCharacters: [],
        Commands: [],
        FileOperationGlobs: [],
        SemanticTokenTypes: [],
        SemanticTokenModifiers: [],
        SupportsBreakpoints: false);

    /// <summary>
    /// Empty, and deliberately so.
    /// </summary>
    /// <remarks>
    /// The gate exists to spare a pack from being asked about compilations it has nothing to say
    /// about, which is the right trade when the pack answers <em>about C#</em>. This one never does:
    /// it implements no contributor, and every question it answers is about the project file in
    /// front of it. A project file is also the one file that is present before any compilation
    /// exists, so a type-resolution gate would switch the pack off exactly when a solution is
    /// still loading and someone is looking at a <c>.csproj</c> wondering why.
    /// </remarks>
    public ImmutableArray<string> WellKnownTypeNames { get; } = [];

    /// <inheritdoc cref="WellKnownTypeNames"/>
    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } = [];

    /// <summary>Never. The pack generates nothing; a project file is what generation reads.</summary>
    public bool IsProjectionPath(string? filePath) => false;
}
