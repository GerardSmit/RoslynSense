using RoslynMCP.Debugger;

namespace RoslynMCP.Config;

/// <summary>
/// The <c>debugger</c> section of <c>roslynsense.json</c>: which <c>System.Diagnostics</c>
/// debugger attributes the debug engines honour.
/// </summary>
/// <remarks>
/// Nullable throughout so "absent" stays distinguishable from "explicitly false" — the resolver
/// layers a command-line flag over an environment variable over this, and only a value that was
/// actually written should win over a flag.
/// </remarks>
public sealed class DebuggerConfig
{
    /// <summary>Format values with their type's <c>DebuggerDisplayAttribute</c>.</summary>
    public bool? DebuggerDisplay { get; init; }

    /// <summary>Expand values through their type's <c>DebuggerTypeProxyAttribute</c>.</summary>
    public bool? TypeProxy { get; init; }

    /// <summary>Honour <c>DebuggerBrowsableAttribute</c> when listing members.</summary>
    public bool? Browsable { get; init; }

    /// <summary>Step past <c>DebuggerStepThrough</c>, <c>DebuggerHidden</c>,
    /// <c>DebuggerNonUserCode</c>, and code with no symbols.</summary>
    public bool? JustMyCode { get; init; }

    /// <summary>Offer a <c>Raw View</c> child whenever a proxy or a hidden member means the
    /// listed children are not the object's own fields.</summary>
    public bool? RawView { get; init; }

    /// <summary>How many children of one value to list before truncating. Null = 100.</summary>
    public int? MaxChildren { get; init; }
}

/// <summary>
/// The debugger view policy in force for this process, and the one place a debug session reads
/// it from.
/// </summary>
/// <remarks>
/// <para>
/// Static for the same reason <see cref="LspFeatureOptions"/> is: the daemon is shared, a debug
/// session is created deep inside a tool call with no settings in hand, and this describes the
/// solution rather than one editor window.
/// </para>
/// <para>
/// Held as a whole object that is swapped, never mutated field by field, so a session reading it
/// while a configuration reload writes it sees one consistent policy rather than half of each.
/// </para>
/// </remarks>
public static class DebuggerViewOptions
{
    private static DebugDisplayOptions s_current = new();

    /// <summary>The policy new debug sessions start with, and that running ones are updated to.</summary>
    public static DebugDisplayOptions Current
    {
        get => s_current;
        set => s_current = value ?? new DebugDisplayOptions();
    }

    /// <summary>
    /// Resolves the policy from configuration, environment, and command-line flags — in that
    /// order of increasing precedence, matching every other switch in the tool.
    /// </summary>
    public static DebugDisplayOptions Resolve(DebuggerConfig? config, string[] args)
    {
        bool HasFlag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

        static bool? Env(string name) => Environment.GetEnvironmentVariable(name) switch
        {
            "0" or "false" or "off" => false,
            "1" or "true" or "on" => true,
            _ => null,
        };

        bool Resolve1(string flag, string environment, bool? configured) =>
            !HasFlag(flag) && (Env(environment) ?? configured ?? true);

        var maxChildren = int.TryParse(Environment.GetEnvironmentVariable("ROSLYNMCP_DEBUG_MAX_CHILDREN"), out var v) && v > 0
            ? v
            : config?.MaxChildren is > 0 ? config.MaxChildren.Value : 100;

        return new DebugDisplayOptions
        {
            DebuggerDisplay = Resolve1("--no-debugger-display", "ROSLYNMCP_DEBUGGER_DISPLAY", config?.DebuggerDisplay),
            TypeProxy = Resolve1("--no-type-proxy", "ROSLYNMCP_DEBUGGER_TYPE_PROXY", config?.TypeProxy),
            Browsable = Resolve1("--no-debugger-browsable", "ROSLYNMCP_DEBUGGER_BROWSABLE", config?.Browsable),
            JustMyCode = Resolve1("--no-just-my-code", "ROSLYNMCP_JUST_MY_CODE", config?.JustMyCode),
            RawView = Resolve1("--no-raw-view", "ROSLYNMCP_DEBUGGER_RAW_VIEW", config?.RawView),
            MaxChildren = maxChildren,
        };
    }

    /// <summary>The switches as short phrases, for the reload log and the settings surface.</summary>
    public static IReadOnlyList<string> Describe(DebugDisplayOptions before, DebugDisplayOptions after)
    {
        var changes = new List<string>();

        void Toggle(string name, bool old, bool @new)
        {
            if (old != @new)
                changes.Add($"{name}: {(old ? "on" : "off")} → {(@new ? "on" : "off")}");
        }

        Toggle("debuggerDisplay", before.DebuggerDisplay, after.DebuggerDisplay);
        Toggle("typeProxy", before.TypeProxy, after.TypeProxy);
        Toggle("debuggerBrowsable", before.Browsable, after.Browsable);
        Toggle("justMyCode", before.JustMyCode, after.JustMyCode);
        Toggle("rawView", before.RawView, after.RawView);

        if (before.MaxChildren != after.MaxChildren)
            changes.Add($"debugger maxChildren: {before.MaxChildren} → {after.MaxChildren}");

        return changes;
    }
}
