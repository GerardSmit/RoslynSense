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

    /// <summary>Everything off — the unfiltered view of what is actually in memory.</summary>
    public static DebugDisplayOptions Raw => new()
    {
        DebuggerDisplay = false,
        TypeProxy = false,
        Browsable = false,
        JustMyCode = false,
        RawView = false,
    };

    public DebugDisplayOptions Clone() => new()
    {
        DebuggerDisplay = DebuggerDisplay,
        TypeProxy = TypeProxy,
        Browsable = Browsable,
        JustMyCode = JustMyCode,
        RawView = RawView,
        MaxChildren = MaxChildren,
    };
}
