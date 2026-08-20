using System.Text.Json;
using RoslynMCP.Config;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// workspace/didChangeConfiguration — applies the editor's <c>roslynSense.*</c> settings to the
/// running server.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed the analyzer switches were environment variables read once at startup,
/// so turning off a noisy rule set meant restarting the daemon — which is not a setting, it is
/// a deployment step.
/// </para>
/// <para>
/// The values are process-wide, and the daemon is shared: two editor windows on one solution,
/// plus every MCP client, see one set of values and the last write wins. That is the right
/// trade for settings that describe a solution rather than a window, but it does mean a change
/// made in one window takes effect in the other.
/// </para>
/// <para>
/// <c>roslynSense.languages.*</c> is the exception, and is why <see cref="ReadLanguages"/>
/// returns its answer instead of storing it: which packs are active is per connection, so it
/// belongs on that session's <see cref="Languages.LanguageSession"/>. Applied process-wide it
/// would let one editor window deactivate a pack under another window, and let an editor
/// setting strip MCP tools from the AI sessions on the same daemon.
/// </para>
/// </remarks>
internal static class ConfigurationHandler
{
    /// <summary>
    /// Reads the settings block sent at <c>initialize</c> or on a configuration change.
    /// Returns true when something the analyzer path depends on actually changed, so the caller
    /// knows whether to throw away cached diagnostics.
    /// </summary>
    public static bool Apply(JsonElement? settings)
    {
        if (!TrySection(settings, out var section))
            return false;

        bool analyzersChanged = false;

        if (Bool(section, "analyzerDiagnostics") is { } analyzers
            && analyzers != LspFeatureOptions.AnalyzerDiagnostics)
        {
            LspFeatureOptions.AnalyzerDiagnostics = analyzers;
            analyzersChanged = true;
        }

        if (Bool(section, "codeStyleDiagnostics") is { } codeStyle
            && codeStyle != LspFeatureOptions.CodeStyleDiagnostics)
        {
            LspFeatureOptions.CodeStyleDiagnostics = codeStyle;
            analyzersChanged = true;
        }

        if (Int(section, "analyzerTimeoutSeconds") is > 0 and int seconds)
            LspFeatureOptions.AnalyzerTimeout = TimeSpan.FromSeconds(seconds);

        if (Bool(section, "externalSource") is { } externalSource)
            LspFeatureOptions.ExternalSource = externalSource;

        if (Bool(section, "loadEntireSolution") is { } loadAll
            && loadAll != LspFeatureOptions.LoadEntireSolution)
        {
            LspFeatureOptions.LoadEntireSolution = loadAll;

            // Turning it on mid-session means what it says: the projects nobody has opened are
            // still missing, and the setting is how the user asked for them. Only on a real
            // change, so the settings block replayed at initialize does not start the load before
            // the client is ready to render its progress — initialized does that.
            if (loadAll)
                _ = SolutionWarmup.Start();
        }

        if (Bool(section, "sourceLink") is { } sourceLink)
            LspFeatureOptions.SourceLink = sourceLink;

        if (Bool(section, "symbolServer") is { } symbolServer)
            LspFeatureOptions.SymbolServer = symbolServer;

        if (Bool(section, "referenceSource") is { } referenceSource)
            LspFeatureOptions.ReferenceSource = referenceSource;

        ApplyFileNestingRules(section);

        if (String(section, "workspaceDiagnostics") is { Length: > 0 } scope
            && scope is "off" or "openProjects" or "solution"
            && scope != LspFeatureOptions.WorkspaceDiagnosticsScope)
        {
            // The capability was advertised at initialize and cannot be withdrawn now; the
            // sweep itself honors the new scope, which is what the setting is for.
            LspFeatureOptions.WorkspaceDiagnosticsScope = scope;
            analyzersChanged = true;
        }

        ApplyDebuggerView(section);

        return analyzersChanged;
    }

    /// <summary>
    /// Applies <c>roslynSense.debugger.*</c> — which debugger attributes the engines honour.
    /// </summary>
    /// <remarks>
    /// Process-wide like the analyzer switches, and pushed into a running session rather than
    /// waiting for the next one: the moment somebody turns a display string off, it is because
    /// the value in front of them looks wrong and they want the raw fields now.
    /// </remarks>
    private static void ApplyDebuggerView(JsonElement section)
    {
        if (!section.TryGetProperty("debugger", out var debugger)
            || debugger.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var current = DebuggerViewOptions.Current;
        var updated = current.Clone();

        updated.DebuggerDisplay = Bool(debugger, "debuggerDisplay") ?? current.DebuggerDisplay;
        updated.TypeProxy = Bool(debugger, "typeProxy") ?? current.TypeProxy;
        updated.Browsable = Bool(debugger, "browsable") ?? current.Browsable;
        updated.JustMyCode = Bool(debugger, "justMyCode") ?? current.JustMyCode;
        updated.RawView = Bool(debugger, "rawView") ?? current.RawView;
        if (debugger.TryGetProperty("maxChildren", out var max)
            && max.ValueKind == JsonValueKind.Number
            && max.TryGetInt32(out var count) && count > 0)
        {
            updated.MaxChildren = count;
        }

        if (DebuggerViewOptions.Describe(current, updated).Count == 0)
            return;

        DebuggerViewOptions.Current = updated;
        DebugSessionManager.GetSession()?.ApplyViewOptions(updated);
    }

    /// <summary>
    /// Applies a change and makes the editor re-ask for everything the change invalidates.
    /// </summary>
    public static async Task HandleAsync(DidChangeConfigurationParams p, CancellationToken ct)
    {
        if (!Apply(p.Settings))
            return;

        // Severity changes are invisible until the cached results are gone: every entry was
        // computed under the old configuration and its key does not include it.
        AnalyzerDiagnosticCache.Clear();
        ProjectWideDiagnosticCache.Clear();
        await LspSessionRegistry.RequestRefreshAsync(RefreshKind.Diagnostics, ct);
    }

    /// <summary>
    /// Reads <c>roslynSense.languages</c> — the per-connection language-pack switches — out of
    /// the same settings block. Absent or malformed means every registered pack is active, which
    /// is also what a client too old to send the section gets.
    /// </summary>
    public static LanguageActivation ReadLanguages(JsonElement? settings)
    {
        if (!TrySection(settings, out var section)
            || !section.TryGetProperty("languages", out var languages)
            || languages.ValueKind != JsonValueKind.Object)
        {
            return LanguageActivation.All;
        }

        var disabled = languages.EnumerateObject()
            .Where(entry => entry.Value.ValueKind == JsonValueKind.False)
            .Select(entry => entry.Name);

        return new LanguageActivation(disabled);
    }

    /// <summary>
    /// Whether this connection wants the server's commands advertised to it.
    /// </summary>
    /// <remarks>
    /// Per connection and never applied process-wide, for a reason that only shows up with two
    /// windows or two solutions: an LSP client turns every id in <c>executeCommandProvider</c>
    /// into an editor command, and an editor has one command table — so the second client to
    /// connect from the same window fails outright on a duplicate id, taking its whole connection
    /// with it. The client that already owns the ids keeps them and the rest ask for none. Absent
    /// means yes, which is what a client too old to send it gets and what every single-connection
    /// editor wants.
    /// </remarks>
    public static bool ReadRegisterCommands(JsonElement? settings) =>
        !TrySection(settings, out var section) || Bool(section, "registerCommands") is not false;

    /// <summary>
    /// Whether this client implements <see cref="CodeActionHandler.PickNestedActionCommand"/> and
    /// can therefore be handed a collapsed group instead of its flattened children.
    /// </summary>
    /// <remarks>
    /// Absent means no, the opposite of every other option here. This one describes something the
    /// client has to *do*, and a client that cannot do it would show an entry that goes nowhere
    /// when clicked — a worse outcome than the long menu the flattening produces.
    /// </remarks>
    public static bool ReadNestedCodeActions(JsonElement? settings) =>
        TrySection(settings, out var section) && Bool(section, "nestedCodeActions") is true;

    /// <summary>
    /// Unwraps the settings block. The client may send the whole settings tree or just our
    /// section, and both <c>initialize</c> and <c>didChangeConfiguration</c> take either.
    /// </summary>
    private static bool TrySection(JsonElement? settings, out JsonElement section)
    {
        section = default;

        if (settings is not { ValueKind: JsonValueKind.Object } root)
            return false;

        section = root.TryGetProperty("roslynSense", out var nested) ? nested : root;
        return section.ValueKind == JsonValueKind.Object;
    }

    /// <summary>
    /// Reads <c>roslynSense.fileNesting.rules</c>, which VS Code flattens to a
    /// <c>fileNesting</c> object with a <c>rules</c> member.
    /// </summary>
    private static void ApplyFileNestingRules(JsonElement section)
    {
        if (!section.TryGetProperty("fileNesting", out var nesting)
            || nesting.ValueKind != JsonValueKind.Object
            || !nesting.TryGetProperty("rules", out var rules)
            || rules.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var pairs = rules.EnumerateObject()
            .Where(rule => rule.Value.ValueKind == JsonValueKind.String)
            .Select(rule => new KeyValuePair<string, string>(rule.Name, rule.Value.GetString()!));

        FileNestingService.SetCustomRules(pairs);
    }

    private static bool? Bool(JsonElement section, string name) =>
        section.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int? Int(JsonElement section, string name) =>
        section.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            ? number
            : null;

    private static string? String(JsonElement section, string name) =>
        section.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Which language packs one editor connection wants, read from its <c>roslynSense.languages</c>
/// block. Consumed once, when that connection's <see cref="Languages.LanguageSession"/> is built.
/// </summary>
/// <remarks>
/// Stored as the set that was switched <em>off</em> rather than the set switched on, because a
/// pack the client never mentions must stay active: the extension only sends the keys it
/// contributes, and a pack added later — or a client that predates the section entirely — would
/// otherwise arrive silently disabled. This gate can only ever narrow what
/// <see cref="Languages.LanguageRegistry"/> already registered; it cannot add a pack the
/// daemon's own settings turned off.
/// </remarks>
internal sealed class LanguageActivation
{
    /// <summary>Every registered pack is active.</summary>
    public static LanguageActivation All { get; } = new([]);

    private readonly HashSet<string> _disabled;

    public LanguageActivation(IEnumerable<string> disabledIds) =>
        _disabled = new HashSet<string>(disabledIds, StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled(string id) => !_disabled.Contains(id);
}
