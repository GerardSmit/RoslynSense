using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.DotSettings.Core;

namespace RoslynMCP.Languages.DotSettings;

/// <summary>
/// ReSharper and Rider settings layers — <c>.DotSettings</c> and <c>.DotSettings.user</c>.
/// </summary>
/// <remarks>
/// <para>
/// A pack that answers no requests about its own files, and that is the point of it. What these
/// files change is the answer to requests about <em>other</em> files: which folders contribute a
/// namespace segment, which files a search may return, which types a coverage run counts. So the
/// work happens where those answers are computed — <c>ProjectMutationService.InferNamespace</c>,
/// <c>SearchFileRules</c>, <c>CoverageService</c> — and reaches it through
/// <see cref="ReSharperSettings.ForProject"/> rather than through a provider interface.
/// </para>
/// <para>
/// The pack exists anyway because the registry is what tells the rest of the server that these
/// files are a known file type with an owner, and because it is the gate: switching the pack off
/// has to switch the whole behaviour off, and a gate nobody can find is not a gate.
/// </para>
/// </remarks>
internal sealed class DotSettingsLanguage : ILanguagePack
{
    public string Id => "dotsettings";

    public string DisplayName => "ReSharper settings";

    public ImmutableArray<string> FileExtensions => [".dotsettings"];

    /// <summary>
    /// The personal layer is <c>.sln.DotSettings.user</c>, whose extension is <c>.user</c>. It is
    /// claimed by name shape rather than by extension for the same reason <c>.config</c> is: a
    /// pack claiming <c>.user</c> outright would take <c>.csproj.user</c> with it.
    /// </summary>
    public bool OwnsFileName(string fileName) =>
        fileName.EndsWith(".DotSettings.user", StringComparison.OrdinalIgnoreCase);

    public LanguageCapabilities Capabilities => LanguageCapabilities.None;

    public ImmutableArray<string> WellKnownTypeNames => [];

    public ImmutableArray<SymbolKind> InterestingSymbolKinds => [];

    public bool IsProjectionPath(string? filePath) => false;
}
