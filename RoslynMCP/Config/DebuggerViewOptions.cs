using RoslynMCP.Debugger;

namespace RoslynMCP.Config;

/// <summary>
/// The <c>debugger</c> section of <c>roslynsense.json</c>: which <c>System.Diagnostics</c>
/// debugger attributes the debug engines honour, and which engine debugs a CoreCLR target.
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

    /// <summary>Show values through their <c>ToString</c> override when no attribute claims
    /// them — VS's "call string-conversion function on objects in variables windows".</summary>
    public bool? CallToString { get; init; }

    /// <summary>Step past <c>DebuggerStepThrough</c>, <c>DebuggerHidden</c>,
    /// <c>DebuggerNonUserCode</c>, and code with no symbols.</summary>
    public bool? JustMyCode { get; init; }

    /// <summary>Offer a <c>Raw View</c> child whenever a proxy or a hidden member means the
    /// listed children are not the object's own fields.</summary>
    public bool? RawView { get; init; }

    /// <summary>How many children of one value to list before truncating. Null = 100.</summary>
    public int? MaxChildren { get; init; }

    /// <summary>Globs for the only modules whose symbols load, when non-empty. A glob without
    /// a path separator matches the module file name; with one, its full path.</summary>
    public string[]? SymbolInclude { get; init; }

    /// <summary>Globs for modules whose symbols never load, winning over the include list.</summary>
    public string[]? SymbolExclude { get; init; }

    /// <summary>
    /// Which engine debugs a CoreCLR target: <c>netcoredbg</c> (default) or <c>icordebug</c>.
    /// </summary>
    /// <remarks>
    /// A string rather than the enum so an unreadable value warns and falls back instead of
    /// failing the whole configuration load — the rest of the section is still usable, and a
    /// debugger that will not start is a worse answer than one that starts on the default.
    /// Read once per session by <see cref="DebugEngineOptions"/>; it is not part of the view
    /// policy and never reaches a running session.
    /// </remarks>
    public string? CoreClrEngine { get; init; }
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

        // Semicolon-separated in the environment, mirroring PATH — a glob can contain a comma.
        static string[] Globs(string environment, string[]? configured) =>
            Environment.GetEnvironmentVariable(environment) is { Length: > 0 } env
                ? env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : configured ?? [];

        return new DebugDisplayOptions
        {
            DebuggerDisplay = Resolve1("--no-debugger-display", "ROSLYNMCP_DEBUGGER_DISPLAY", config?.DebuggerDisplay),
            TypeProxy = Resolve1("--no-type-proxy", "ROSLYNMCP_DEBUGGER_TYPE_PROXY", config?.TypeProxy),
            Browsable = Resolve1("--no-debugger-browsable", "ROSLYNMCP_DEBUGGER_BROWSABLE", config?.Browsable),
            CallToString = Resolve1("--no-call-tostring", "ROSLYNMCP_DEBUGGER_CALL_TOSTRING", config?.CallToString),
            JustMyCode = Resolve1("--no-just-my-code", "ROSLYNMCP_JUST_MY_CODE", config?.JustMyCode),
            RawView = Resolve1("--no-raw-view", "ROSLYNMCP_DEBUGGER_RAW_VIEW", config?.RawView),
            MaxChildren = maxChildren,
            SymbolInclude = Globs("ROSLYNMCP_SYMBOL_INCLUDE", config?.SymbolInclude),
            SymbolExclude = Globs("ROSLYNMCP_SYMBOL_EXCLUDE", config?.SymbolExclude),
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
        Toggle("callToString", before.CallToString, after.CallToString);
        Toggle("justMyCode", before.JustMyCode, after.JustMyCode);
        Toggle("rawView", before.RawView, after.RawView);

        if (before.MaxChildren != after.MaxChildren)
            changes.Add($"debugger maxChildren: {before.MaxChildren} → {after.MaxChildren}");

        void Globs(string name, string[] old, string[] @new)
        {
            if (!old.SequenceEqual(@new, StringComparer.OrdinalIgnoreCase))
                changes.Add($"{name}: [{string.Join(", ", old)}] → [{string.Join(", ", @new)}]");
        }

        Globs("symbolInclude", before.SymbolInclude, after.SymbolInclude);
        Globs("symbolExclude", before.SymbolExclude, after.SymbolExclude);

        return changes;
    }
}
