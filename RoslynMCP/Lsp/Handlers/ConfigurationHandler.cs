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

        if (Bool(section, "sourceLink") is { } sourceLink)
            LspFeatureOptions.SourceLink = sourceLink;

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

        return analyzersChanged;
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
