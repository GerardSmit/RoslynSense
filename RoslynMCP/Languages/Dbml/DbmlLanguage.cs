using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Services.Database;

namespace RoslynMCP.Languages.Dbml;

/// <summary>
/// LINQ to SQL models — <c>.dbml</c> — as a pack: the file the developer edits, rather than the
/// <c>.designer.cs</c> SqlMetal re-emits from it.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as the protobuf pack and for the same reason: SqlMetal writes a real <c>.cs</c>
/// that MSBuild compiles, so Roslyn already holds the symbols and the pack's job is to join a model
/// declaration to one rather than to project anything. See <see cref="DbmlGeneratedIndex"/> for how
/// that join is made, and <see cref="DbmlSourceMappingService"/> for why the path alone cannot make
/// it.
/// </para>
/// <para>
/// LSP only, for now. There is no <c>Tools/</c> layer and therefore no formatter in the constructor;
/// the engine is separated so one can be added without the features moving.
/// </para>
/// </remarks>
internal sealed partial class DbmlLanguage : ILanguagePack
{
    /// <summary>
    /// The connections a table refresh may run against, or <c>null</c> in a host that has none.
    /// </summary>
    /// <remarks>
    /// Optional because the CLI builds its packs without a container and never executes an LSP
    /// command; a refresh there would have nothing to connect to and nothing to ask. The registry is
    /// the only reason this pack has a constructor at all — every other feature reads files.
    /// </remarks>
    private readonly DbConnectionRegistry? _connections;

    public DbmlLanguage(DbConnectionRegistry? connections = null) => _connections = connections;

    public string Id => "dbml";

    public string DisplayName => "LINQ to SQL";

    public ImmutableArray<string> FileExtensions { get; } = [".dbml"];

    /// <summary>
    /// XML's own triggers — a tag opening and an attribute value opening. Not the space before an
    /// attribute name: the list still opens on the first letter typed, and claiming the space would
    /// make it a character no single pack's absence can withdraw. No signature help: a model declares
    /// nothing that takes arguments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The commands are the refresh flow, declared here so they leave <c>initialize</c> — and the
    /// command palette — when the pack is switched off. A command advertised by a pack that is not
    /// running is one the editor will happily send and nothing will answer.
    /// </para>
    /// <para>
    /// No token types of its own even though the pack does answer semanticTokens: everything it
    /// colours is something C# already names, so it emits C#'s legend entries and adds nothing to the
    /// legend. See <see cref="SemanticTokenTypeNames"/>.
    /// </para>
    /// </remarks>
    public LanguageCapabilities Capabilities { get; } = new(
        CompletionTriggerCharacters: ["<", "\"", "'"],
        SignatureHelpTriggerCharacters: [],
        Commands: [ConnectionsCommand, PlanRefreshCommand, ApplyRefreshCommand,
            AddableCommand, ApplyAddCommand],
        FileOperationGlobs: [],
        SemanticTokenTypes: [],
        SemanticTokenModifiers: [],
        SupportsBreakpoints: false);

    /// <summary>
    /// The one runtime type every generated designer needs: the context derives from
    /// <c>DataContext</c> and the mapping attributes live beside it. Not resolving means the project
    /// references no LINQ to SQL at all, so the contributors decline before touching the file system.
    /// </summary>
    public ImmutableArray<string> WellKnownTypeNames { get; } = ["System.Data.Linq.DataContext"];

    /// <summary>
    /// What a model declaration becomes in C#: a table's row type and the context are types, a
    /// column and an association are properties, and a function is a method. A symbol of any other
    /// kind has nothing in a <c>.dbml</c> to correspond to.
    /// </summary>
    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } =
        [SymbolKind.NamedType, SymbolKind.Property, SymbolKind.Method];

    /// <summary>
    /// Never. SqlMetal writes a real <c>.designer.cs</c> that MSBuild compiles as an ordinary
    /// <c>Compile</c> item, so the pack invents no document and every request about that file
    /// belongs to the C# handlers — which is exactly what returning <c>false</c> arranges.
    /// </summary>
    public bool IsProjectionPath(string? filePath) => false;
}
