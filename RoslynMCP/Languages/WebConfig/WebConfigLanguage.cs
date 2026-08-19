using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.WebConfig.Core;

namespace RoslynMCP.Languages.WebConfig;

/// <summary>
/// The .NET Framework configuration file — <c>web.config</c>, <c>app.config</c> and the nested
/// ones a web application puts in its subdirectories — as a pack: the names the file declares,
/// joined to the C# and the markup that read them.
/// </summary>
/// <remarks>
/// <para>
/// The appsettings pack's problem in an older shape, and a simpler one. There are no nested
/// sections and no options binding: <c>&lt;add key="CdnRoot" …&gt;</c> is read as
/// <c>ConfigurationManager.AppSettings["CdnRoot"]</c> from C# and as
/// <c>&lt;%$ AppSettings: CdnRoot %&gt;</c> from markup, and neither side has a symbol — so the
/// join runs through <see cref="ConfigurationManagerUsageIndex"/> and
/// <see cref="MarkupSettingUsageIndex"/>, which is where those two spellings are found.
/// </para>
/// <para>
/// <c>connectionStrings</c> is the same keyspace question with a different section name and is
/// treated alike throughout, down to the <c>.ProviderName</c> suffix the markup builder accepts.
/// </para>
/// <para>
/// The file is XML and stays XML in the editor: the pack claims it by name, not by extension, so
/// <c>packages.config</c> and <c>nuget.config</c> beside it stay NuGet's. It also does not claim
/// the XDT transforms — a <c>Web.Release.config</c> declares edits to apply at publish, not
/// settings that exist.
/// </para>
/// <para>
/// A framework that invents its own name for the same file — DotNetNuke's <c>release.config</c>
/// and <c>development.config</c>, whole <c>&lt;configuration&gt;</c> documents its installer copies
/// over <c>web.config</c> — is named in <c>webConfig.additionalFiles</c> rather than guessed at.
/// The alternative is reading every <c>.config</c> in the tree to find out whether its root element
/// makes it ours, on a path that otherwise decides ownership from the name alone and touches no
/// disk.
/// </para>
/// </remarks>
internal sealed partial class WebConfigLanguage : ILanguagePack
{
    public string Id => "webconfig";

    public string DisplayName => "Web.config settings";

    /// <summary>
    /// Empty on purpose: claiming <c>.config</c> would take <c>packages.config</c> and
    /// <c>nuget.config</c> along with it. Ownership is by exact name instead — see
    /// <see cref="FileNames"/>.
    /// </summary>
    public ImmutableArray<string> FileExtensions { get; } = [];

    /// <summary>
    /// <c>web.config</c> and <c>app.config</c>, plus whatever <c>webConfig.additionalFiles</c>
    /// named — read through rather than copied, because the static readers of the same set are
    /// configured before any pack is constructed.
    /// </summary>
    public ImmutableArray<string> FileNames => WebConfigFile.Names;

    /// <summary>Nothing to add: the file is XML the editor already knows how to type, and every
    /// answer here is about a name that is already written.</summary>
    /// <summary>A quote opens an attribute value, which is where a setting is named.</summary>
    public LanguageCapabilities Capabilities { get; } = LanguageCapabilities.None with
    {
        CompletionTriggerCharacters = ["\""],
    };

    /// <summary>
    /// The type every Framework configuration read goes through. Empty rather than naming it,
    /// though, because a compilation that cannot resolve it can still have markup that reads the
    /// file — an ASP.NET site's pages are not compiled into the project Roslyn loads.
    /// </summary>
    public ImmutableArray<string> WellKnownTypeNames { get; } = [];

    /// <summary>Nothing in C# corresponds to an entry: a setting is a string in a collection,
    /// never a symbol.</summary>
    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } = [];

    /// <summary>Never — the pack projects nothing; the XML is the document.</summary>
    public bool IsProjectionPath(string? filePath) => false;
}
