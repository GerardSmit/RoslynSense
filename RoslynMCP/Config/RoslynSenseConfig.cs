using System.Text.Json.Serialization;

namespace RoslynMCP.Config;

public sealed class RoslynSenseConfig
{
    public ToolsConfig Tools { get; init; } = new();
    public DatabaseConfig Database { get; init; } = new();
    public ResourcesConfig Resources { get; init; } = new();

    /// <summary>The <c>webConfig</c> section: which extra files the Framework-configuration pack
    /// claims beyond <c>web.config</c> and <c>app.config</c>.</summary>
    public WebConfigConfig WebConfig { get; init; } = new();

    /// <summary>The <c>logging</c> section: which of the message-template rules run.</summary>
    public LoggingConfig Logging { get; init; } = new();

    /// <summary>The <c>valueSets</c> section: strings whose allowed values live somewhere the
    /// compiler cannot see.</summary>
    public ValueSetsConfig ValueSets { get; init; } = new();

    /// <summary>The <c>webForms</c> section: which markup attributes carry data expressions.</summary>
    public WebFormsConfig WebForms { get; init; } = new();

    /// <summary>Which debugger attributes the debug engines honour while inspecting and stepping.</summary>
    public DebuggerConfig Debugger { get; init; } = new();
    public string? TableFormat { get; init; }
    /// <summary>
    /// Paths to preload on startup (solution or project files).
    /// Null = auto-discover from CWD. Empty list = disabled.
    /// </summary>
    public IReadOnlyList<string>? Preload { get; init; }

    /// <summary>
    /// Share one out-of-process host per solution across all MCP clients (chats), so the
    /// solution is loaded once instead of once per chat. Null = default (enabled).
    /// </summary>
    public bool? SharedHost { get; init; }

    /// <summary>Minutes the shared host stays alive after its last client disconnects. Null = 30.</summary>
    public int? HostIdleMinutes { get; init; }

    /// <summary>Max cached workspaces (LRU bound) per process. Null = 4.</summary>
    public int? MaxWorkspaces { get; init; }
}

public sealed class ToolsConfig
{
    public bool WebForms { get; init; } = true;
    public bool Razor { get; init; } = true;
    public bool Proto { get; init; } = true;
    public bool Mediator { get; init; } = true;
    public bool Resources { get; init; } = true;
    public bool MsBuild { get; init; } = true;
    public bool Dbml { get; init; } = true;
    public bool AppSettings { get; init; } = true;
    public bool WebConfig { get; init; } = true;
    public bool DotSettings { get; init; } = true;
    public bool Logging { get; init; } = true;
    public bool ValueSets { get; init; } = true;
    public bool Debugger { get; init; } = true;
    public bool Profiling { get; init; } = true;
    public bool Database { get; init; } = true;
}

/// <summary>
/// The <c>webConfig</c> section of <c>roslynsense.json</c>.
/// </summary>
/// <remarks>
/// Separate from the <c>tools.webConfig</c> switch that turns the pack on, the same way
/// <see cref="DatabaseConfig"/> is separate from <c>tools.database</c>: one decides whether the
/// pack runs, the other what it runs over.
/// </remarks>
public sealed class WebConfigConfig
{
    /// <summary>
    /// File names the pack claims in addition to <c>web.config</c> and <c>app.config</c>, matched
    /// case-insensitively against the file name alone.
    /// </summary>
    /// <remarks>
    /// For the conventions a framework invents on top of the .NET ones — DotNetNuke keeps a
    /// <c>release.config</c> and a <c>development.config</c> beside its <c>web.config</c>, each a
    /// whole <c>&lt;configuration&gt;</c> document rather than an XDT transform, and the installer
    /// copies one of them over <c>web.config</c>. Exact names rather than a glob on purpose:
    /// <c>*.config</c> would take <c>packages.config</c> and <c>nuget.config</c> with it, which is
    /// what claiming the extension outright already fails to do.
    /// </remarks>
    public IReadOnlyList<string>? AdditionalFiles { get; init; }
}

/// <summary>
/// The <c>logging</c> section of <c>roslynsense.json</c>: which of the message-template rules
/// report.
/// </summary>
/// <remarks>
/// One switch per rule, because two of them restate what the <c>[LoggerMessage]</c> source
/// generator already says as SYSLIB1014 and SYSLIB1015. A solution where the generator runs turns
/// those two off and keeps the three it has no equivalent for.
/// </remarks>
public sealed class LoggingConfig
{
    /// <summary>LOG0001 — malformed template text: an unclosed brace, a hole naming nothing.</summary>
    public bool? TemplateSyntax { get; init; }

    /// <summary>LOG0002 — a placeholder that matches no parameter of a generated logging
    /// method, so it prints as literal text. SYSLIB1014's claim, reported on the placeholder.</summary>
    public bool? UnknownPlaceholder { get; init; }

    /// <summary>LOG0003 — the template's placeholder count and the call's value count
    /// disagree.</summary>
    public bool? ValueCount { get; init; }

    /// <summary>LOG0004 — a value no placeholder renders. SYSLIB1015's claim for a generated
    /// method, reported on the parameter.</summary>
    public bool? UnusedValue { get; init; }

    /// <summary>LOG0005 — an exception passed as a rendered value instead of as the call's
    /// first argument, which loses the stack trace.</summary>
    public bool? ExceptionPosition { get; init; }
}

/// <summary>
/// The <c>valueSets</c> section of <c>roslynsense.json</c>: named sets of allowed string values,
/// and the places in C# that have to be one of them.
/// </summary>
/// <remarks>
/// The case this exists for is a status code that lives in a database table. The column is the
/// definition, the C# side is a bare <c>string</c>, and nothing between them checks anything — so a
/// typo is a branch that never runs and a renamed row is a branch that stopped running, both
/// silently. Naming the query once makes the column the source of truth for completion and for a
/// warning, without the code having to change shape.
/// </remarks>
public sealed class ValueSetsConfig
{
    /// <summary>The sets themselves. Each needs an <c>id</c> and either a query or a list.</summary>
    public IReadOnlyList<ValueSetEntry>? Sets { get; init; }

    /// <summary>Where in C# each set's values are written.</summary>
    public IReadOnlyList<ValueBindingEntry>? Bindings { get; init; }

    /// <summary>
    /// Whether a literal that is not one of the set's values is reported. Default on.
    /// </summary>
    /// <remarks>
    /// Only ever reported for a set that actually loaded. A database that is unreachable, a query
    /// that failed and a set nothing has asked for yet all report nothing at all, because "this is
    /// not a valid code" is a claim that needs the codes in hand.
    /// </remarks>
    public bool? UnknownValueDiagnostic { get; init; }

    /// <summary>
    /// How loudly: <c>error</c> (default), <c>warning</c> or <c>information</c>.
    /// </summary>
    /// <remarks>
    /// An error by default because that is what the situation is. A code the table does not have is
    /// a branch that will never be taken — the build succeeds, the tests pass, and the feature is
    /// simply missing at runtime — which is the same class of mistake as a misspelled member name,
    /// and nobody would want that as a warning. Softened for a codebase adopting this over an
    /// existing solution, where the first run finds things that are wrong but not urgent.
    /// </remarks>
    public string? Severity { get; init; }
}

/// <summary>One named set of allowed values.</summary>
public sealed class ValueSetEntry
{
    /// <summary>What a binding refers to this set by.</summary>
    public string? Id { get; init; }

    /// <summary>The alias of a connection from the <c>database</c> section.</summary>
    public string? Connection { get; init; }

    /// <summary>
    /// The query that produces the values. The first column is the value; a second column, if
    /// there is one, is shown beside it as a label.
    /// </summary>
    public string? Query { get; init; }

    /// <summary>The values written out, for a set with no database behind it. Used when
    /// <see cref="Query"/> is absent.</summary>
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>Whether a literal has to match a value's casing exactly. Default off, since the
    /// comparison the code does is usually case-insensitive too.</summary>
    public bool? CaseSensitive { get; init; }
}

/// <summary>
/// One place in C# whose string is a value from a set: an argument of a call, or a member whose
/// value is compared against a literal.
/// </summary>
/// <remarks>
/// Which of the two it is comes from the member itself rather than from a flag. A method takes the
/// value as an argument and <see cref="ValueIndex"/> says which one; a property or a field
/// <i>holds</i> the value, and what is checked is every literal it is compared against.
/// </remarks>
public sealed class ValueBindingEntry
{
    /// <summary>The <see cref="ValueSetEntry.Id"/> this binds.</summary>
    public string? Set { get; init; }

    /// <summary>The full name of the class or interface declaring the member.</summary>
    public string? ContainingType { get; init; }

    /// <summary>The member's name, or <c>Item</c> for an indexer.</summary>
    public string? MemberName { get; init; }

    /// <summary>One type name per parameter, <c>*</c> for any. Empty matches every overload.</summary>
    public IReadOnlyList<string>? ParameterTypes { get; init; }

    /// <summary>Which parameter carries the value, counted from 0. Methods only.</summary>
    public int? ValueIndex { get; init; }
}

/// <summary>
/// The <c>webForms</c> section: markup attributes whose value names a member of the bound item.
/// </summary>
/// <remarks>
/// Configured rather than built in, because the attributes that behave this way come from the
/// control library rather than from the framework — <c>SortExpression</c> and <c>DataField</c> are
/// a grid vendor's names, and the next site uses a different vendor. Nothing ships enabled: an
/// attribute wrongly declared to hold a member path turns every use of it into a warning.
/// </remarks>
public sealed class WebFormsConfig
{
    /// <summary>The attributes to read as data expressions.</summary>
    public IReadOnlyList<MarkupBindingEntry>? DataExpressions { get; init; }

    /// <summary>Whether a name that binds to nothing is reported at all. Default true.</summary>
    public bool? UnknownMemberDiagnostic { get; init; }

    /// <summary>
    /// How loudly: <c>error</c>, <c>warning</c>, <c>info</c> or <c>hidden</c>. Default
    /// <c>warning</c>.
    /// </summary>
    /// <remarks>
    /// A warning rather than an error by default, unlike the value sets: a data-binding path is
    /// resolved case-insensitively through <c>TypeDescriptor</c> and can be satisfied at runtime by
    /// a type this tool never sees — a <c>DataTable</c> column, a dynamic row — so a name that
    /// binds to nothing here is very likely wrong rather than certainly wrong.
    /// </remarks>
    public string? Severity { get; init; }
}

/// <summary>One attribute that carries a data expression.</summary>
public sealed class MarkupBindingEntry
{
    /// <summary>
    /// The tag it is written on — <c>telerik:GridBoundColumn</c>, or <c>*</c> for any.
    /// </summary>
    /// <remarks>
    /// Matched on the tag as written rather than on the control's type, because the type behind a
    /// vendor prefix is often not resolvable in a site that references the assembly loosely, and an
    /// attribute registry that stopped working when the reference did would be worse than one that
    /// reads names.
    /// </remarks>
    public string? Tag { get; init; }

    /// <summary>The attribute name, matched case-insensitively as markup attributes are.</summary>
    public string? Attribute { get; init; }

    /// <summary>
    /// How the value reads: <c>member</c> — a path from the bound item — or <c>format</c>, a
    /// composite format string.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>
    /// For <c>format</c>, where the value being formatted comes from:
    /// <c>[ItemType].[Control.DataField]</c> reads this tag's <c>DataField</c> attribute and
    /// resolves it against the bound item, which is what tells a <c>{0:dd-MM-yyyy}</c> that it is
    /// formatting a date.
    /// </summary>
    public string? Source { get; init; }
}

public sealed class DatabaseConfig
{
    public bool? AutoDiscovery { get; init; }
    public Dictionary<string, ConnectionEntry> Connections { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

[JsonConverter(typeof(ConnectionEntryConverter))]
public sealed record ConnectionEntry(string Provider, string ConnectionString);
