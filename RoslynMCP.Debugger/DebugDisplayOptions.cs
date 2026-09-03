namespace RoslynMCP.Debugger;

/// <summary>
/// Which of the <c>System.Diagnostics</c> debugger attributes the engine honours.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these costs something the raw view does not. <see cref="DebuggerDisplay"/> and
/// <see cref="TypeProxy"/> run the debuggee's own code through function evaluation — a getter
/// that throws, blocks, or mutates state does so under the debugger too — and
/// <see cref="Browsable"/> and <see cref="JustMyCode"/> deliberately hide things that are really
/// there. So each is a switch rather than a hardcoded behaviour: when a display string lies about
/// an object, turning it off is how you find out.
/// </para>
/// <para>
/// Carried as a mutable class rather than a record so the worker can rebuild one from JSON, and
/// forwarded to the worker on connect — a bitness-mismatched session has to honour the same
/// settings as an in-process one.
/// </para>
/// </remarks>
public sealed class DebugDisplayOptions
{
    /// <summary>Format a value with its type's <c>DebuggerDisplayAttribute</c> instead of its
    /// type name. Requires function evaluation for anything that is not a plain field.</summary>
    public bool DebuggerDisplay { get; set; } = true;

    /// <summary>Expand a value through its type's <c>DebuggerTypeProxyAttribute</c>, so a
    /// dictionary shows its entries rather than its bucket arrays.</summary>
    public bool TypeProxy { get; set; } = true;

    /// <summary>Honour <c>DebuggerBrowsableAttribute</c>: <c>Never</c> hides a member,
    /// <c>RootHidden</c> replaces it with its own children.</summary>
    public bool Browsable { get; set; } = true;

    /// <summary>Show a value through its own <c>ToString</c> override when nothing else claims
    /// it — VS's "call string-conversion function on objects in variables windows". Function
    /// evaluation, with everything that implies.</summary>
    public bool CallToString { get; set; } = true;

    /// <summary>
    /// Step past code marked <c>DebuggerStepThrough</c>, <c>DebuggerHidden</c> or
    /// <c>DebuggerNonUserCode</c>, and past frames with no symbols at all.
    /// </summary>
    public bool JustMyCode { get; set; } = true;

    /// <summary>
    /// Append a <c>Raw View</c> child whenever a proxy or a hidden member means the listed
    /// children are not the object's real fields. The escape hatch from a lying proxy.
    /// </summary>
    public bool RawView { get; set; } = true;

    /// <summary>How many elements of an array or collection to list before truncating.</summary>
    public int MaxChildren { get; set; } = 100;

    /// <summary>
    /// Globs for the only modules whose symbols load, when non-empty — VS's "Load only
    /// specified modules". A glob without a path separator matches the module's file name;
    /// with one, its full path. See <see cref="SymbolGlobs"/>.
    /// </summary>
    public string[] SymbolInclude { get; set; } = [];

    /// <summary>
    /// Globs for modules whose symbols never load, winning over <see cref="SymbolInclude"/> —
    /// VS's "Load all modules, unless excluded". A module without symbols cannot bind source
    /// breakpoints, exactly as if it had shipped without a PDB.
    /// </summary>
    public string[] SymbolExclude { get; set; } = [];

    /// <summary>
    /// The assemblies the open solution builds, which is what makes <see cref="JustMyCode"/> mean
    /// something: a module the solution does not build is not the user's, however its path is
    /// spelled. Empty when no solution is open — see <see cref="UserCodeMap"/> for what that
    /// leaves.
    /// </summary>
    public string[] UserAssemblies { get; set; } = [];

    /// <summary>Everything off — the unfiltered view of what is actually in memory.</summary>
    public static DebugDisplayOptions Raw => new()
    {
        DebuggerDisplay = false,
        TypeProxy = false,
        Browsable = false,
        CallToString = false,
        JustMyCode = false,
        RawView = false,
    };

    public DebugDisplayOptions Clone() => new()
    {
        DebuggerDisplay = DebuggerDisplay,
        TypeProxy = TypeProxy,
        Browsable = Browsable,
        CallToString = CallToString,
        JustMyCode = JustMyCode,
        RawView = RawView,
        MaxChildren = MaxChildren,
        SymbolInclude = SymbolInclude,
        SymbolExclude = SymbolExclude,
        UserAssemblies = UserAssemblies,
    };
}
