using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.AppSettings.Core;

namespace RoslynMCP.Languages.AppSettings;

/// <summary>
/// The application's configuration files — <c>appsettings.json</c>, its environment overlays and
/// the user-secrets store — as a pack: the keys the JSON declares, joined to the C# that reads
/// them.
/// </summary>
/// <remarks>
/// <para>
/// The join runs in both directions and neither side has a symbol. A key exists as a JSON
/// property and its readers exist as string literals — <c>GetSection("Example")</c>,
/// <c>Configuration["Example:Retries"]</c> — so everything here goes through
/// <see cref="ConfigurationUsageIndex"/>, which is where those literals are found and where
/// <c>Configure&lt;TOptions&gt;</c> bindings tie a section to a real type. The type is what lets
/// a bound key answer with more than its literal mentions: its property's references, and its
/// property's siblings as completion.
/// </para>
/// <para>
/// The three file kinds are one keyspace split across files, because that is what the runtime
/// makes of them: an overlay key overrides the base key, and a secrets key exists precisely so
/// it is not in the repository. Every feature treats them alike.
/// </para>
/// </remarks>
internal sealed partial class AppSettingsLanguage : ILanguagePack
{
    public string Id => "appsettings";

    public string DisplayName => "Application settings";

    /// <summary>
    /// Empty on purpose: claiming <c>.json</c> would take <c>package.json</c>, every schema and
    /// every data file along with it. Ownership is by name shape instead — see
    /// <see cref="OwnsFileName"/>.
    /// </summary>
    public ImmutableArray<string> FileExtensions { get; } = [];

    public bool OwnsFileName(string fileName) => AppSettingsFile.IsConfigurationPath(fileName);

    /// <summary>A quote opens a property name, a colon starts a value.</summary>
    public LanguageCapabilities Capabilities { get; } = new(
        CompletionTriggerCharacters: ["\"", ":"],
        SignatureHelpTriggerCharacters: [],
        Commands: [],
        FileOperationGlobs: [],
        SemanticTokenTypes: [],
        SemanticTokenModifiers: [],
        SupportsBreakpoints: false);

    /// <summary>The one interface every configuration read goes through. A compilation that
    /// cannot resolve it reads no configuration, and the pack has nothing to say.</summary>
    public ImmutableArray<string> WellKnownTypeNames { get; } =
        ["Microsoft.Extensions.Configuration.IConfiguration"];

    /// <summary>Bound options surface as types and their properties; nothing else in C#
    /// corresponds to a key.</summary>
    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } =
        [SymbolKind.NamedType, SymbolKind.Property];

    /// <summary>Never — the pack projects nothing; the JSON is the document.</summary>
    public bool IsProjectionPath(string? filePath) => false;
}
