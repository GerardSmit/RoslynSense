using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages.Values.Core;
using RoslynMCP.Services.Database;

namespace RoslynMCP.Languages.Values;

/// <summary>
/// Strings whose allowed values are declared somewhere the compiler cannot see — a lookup table,
/// most of the time — and the places in C# that have to be one of them.
/// </summary>
/// <remarks>
/// <para>
/// The shape of the problem: a status code lives as rows in <c>Shop_OrderStatus</c>, the C# that
/// reads and compares it is a bare <c>string</c>, and there is nothing in between. A misspelled
/// code compiles, runs, and takes a branch that can never be true; a row renamed by a migration
/// turns a branch that used to run into one that silently stopped. Both are invisible to the
/// compiler, to the analyzers, and to review, and both surface weeks later as "that feature just
/// doesn't do anything any more".
/// </para>
/// <para>
/// An enum would fix it and is usually not available: the table is the product's data, other
/// systems write to it, and rows are added without a deployment. So the table stays the definition
/// and this makes it reachable — name the query once, say which member holds its values, and the
/// column becomes completion, hover and a diagnostic wherever that member is written or compared.
/// </para>
/// <para>
/// A pack with no files, like Mediator and Logging: what is missing is not a file type but the edge
/// between a C# literal and a definition living outside the compilation. It reaches literals
/// through <see cref="IConfiguredStringLanguage"/>, and it is registered last of the three so that
/// a literal another pack already recognises stays that pack's.
/// </para>
/// </remarks>
internal sealed partial class ValuesLanguage : ILanguagePack
{
    /// <summary>
    /// The pack id, the <c>roslynSense.languages.*</c> key and the <c>tools.valueSets</c> gate, one
    /// string so a new surface cannot spell it differently from the last one.
    /// </summary>
    public const string PackId = "valuesets";

    /// <summary>The id every diagnostic from this pack is reported under.</summary>
    internal const string DiagnosticSource = "roslyn-sense";

    private readonly ValueSetCatalog _catalog;

    public ValuesLanguage(EffectiveSettings settings, DbConnectionRegistry? connections = null)
        : this(settings.ValueSets, connections)
    {
    }

    /// <summary>The sets directly, for a caller that has resolved them without a whole
    /// <see cref="EffectiveSettings"/> — the tests, and nothing else so far.</summary>
    internal ValuesLanguage(ValueSettings settings, DbConnectionRegistry? connections = null)
    {
        Settings = settings;
        _catalog = new ValueSetCatalog(connections);
    }

    /// <summary>The sets and bindings this process runs with.</summary>
    public ValueSettings Settings { get; }

    /// <summary>The values behind the sets, loaded on first use and cached.</summary>
    internal ValueSetCatalog Catalog => _catalog;

    public string Id => PackId;

    public string DisplayName => "Value sets";

    /// <summary>
    /// None. A bound literal lives in a <c>.cs</c> file, which the C# routes already cover; the
    /// pack reaches it through the embedded-string seam instead of by owning a document.
    /// </summary>
    public ImmutableArray<string> FileExtensions { get; } = [];

    /// <summary>
    /// <c>"</c> is the trigger, because the caret arriving inside an empty literal is the moment
    /// the list is wanted and there is nothing else to type first. No token types of its own: a
    /// value is a string and stays coloured as one, since a set of them is a fact about the data
    /// rather than about the syntax.
    /// </summary>
    public LanguageCapabilities Capabilities { get; } = new(
        CompletionTriggerCharacters: ["\""],
        SignatureHelpTriggerCharacters: [],
        Commands: [RefreshCommand],
        FileOperationGlobs: [],
        SemanticTokenTypes: [],
        SemanticTokenModifiers: [],
        SupportsBreakpoints: false);

    /// <summary>
    /// None. Whether this pack has anything to do depends on <c>roslynsense.json</c> and not on
    /// what the solution references, so there is no type whose absence would rule it out.
    /// </summary>
    public ImmutableArray<string> WellKnownTypeNames { get; } = [];

    /// <summary>
    /// A value is not a symbol, so no contributor pass over C# symbols has anything to add.
    /// Everything this pack answers arrives through the embedded-string seam, which does not
    /// consult this.
    /// </summary>
    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } = [];

    /// <summary>Nothing is projected: the literal is read where it is written.</summary>
    public bool IsProjectionPath(string? filePath) => false;
}
