namespace RoslynMCP.Config;

/// <summary>
/// Feature switches for the editor-facing paths. Static because the LSP handlers and the
/// analyzer services are static too (matching <see cref="Services.WorkspaceService"/>);
/// values come from environment variables at startup and are settable for tests.
/// </summary>
/// <remarks>
/// Everything here describes the solution, so one shared value across the daemon's editor
/// windows and MCP clients is right. Anything that describes a <em>window</em> does not belong
/// here — which is why the language-pack switches (<c>roslynSense.languages.*</c>) live on the
/// per-connection <c>LanguageSession</c> instead: as a static, one window turning a pack off
/// would turn it off under every other window and remove the matching MCP tools from the AI
/// sessions attached to the same daemon.
/// </remarks>
public static class LspFeatureOptions
{
    /// <summary>Run project analyzers (StyleCop, Roslynator, in-house) for editor diagnostics.</summary>
    public static bool AnalyzerDiagnostics { get; set; } = EnvFlag("ROSLYNMCP_ANALYZER_DIAGNOSTICS", true);

    /// <summary>Include Roslyn's built-in IDE0xxx code-style analyzers.</summary>
    public static bool CodeStyleDiagnostics { get; set; } = EnvFlag("ROSLYNMCP_CODE_STYLE_DIAGNOSTICS", true);

    /// <summary>
    /// How much of the solution <c>workspace/diagnostic</c> sweeps: <c>off</c>,
    /// <c>openProjects</c> (projects owning an open document, plus what they reference), or
    /// <c>solution</c>. The default is the middle one — an empty Problems panel is unhelpful,
    /// and sweeping 200 projects on every request is worse.
    /// </summary>
    public static string WorkspaceDiagnosticsScope { get; set; } =
        Environment.GetEnvironmentVariable("ROSLYNMCP_WORKSPACE_DIAGNOSTICS") switch
        {
            "off" or "0" => "off",
            "solution" or "all" => "solution",
            _ => "openProjects",
        };

    /// <summary>
    /// Load every project the solution lists as soon as an editor connects, instead of loading a
    /// project the first time a file in it is touched.
    /// </summary>
    /// <remarks>
    /// This is what makes the solution-wide features answer about the whole solution rather than
    /// about whatever happens to be open — Search Everywhere, workspace symbols, find-references
    /// into a project nobody has visited. It costs one full MSBuild evaluation of the solution in
    /// the background at start-up, and the memory to keep it; off restores the demand-driven
    /// behaviour, which is the better trade only on a solution too large to hold at once.
    /// </remarks>
    public static bool LoadEntireSolution { get; set; } = EnvFlag("ROSLYNMCP_LOAD_ENTIRE_SOLUTION", true);

    /// <summary>
    /// Reference counts in the gutter of a WebForms markup file — <c>.aspx</c>, <c>.ascx</c> and
    /// the rest of the pack's files. Off by default, unlike every other switch here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A markup file is close to nothing but control declarations, so the count lands on almost
    /// every line: the gutter stops annotating the markup and starts laying it out, and a
    /// user control is the extreme case. The number is still one gesture away on any <c>ID</c>,
    /// by find-references — which is the better default, and why this one starts off.
    /// </para>
    /// <para>
    /// Process-wide like the switches around it, though it describes an editor rather than a
    /// solution: the per-connection channel is <c>roslynSense.languages.*</c> alone, and adding a
    /// second one for a gutter is not worth what it costs. Two windows on one daemon share the
    /// answer, and the last one to write it wins.
    /// </para>
    /// </remarks>
    public static bool WebFormsCodeLens { get; set; } = EnvFlag("ROSLYNMCP_WEBFORMS_CODE_LENS", false);

    /// <summary>Per-document time budget for an analyzer pass.</summary>
    public static TimeSpan AnalyzerTimeout { get; set; } =
        TimeSpan.FromSeconds(EnvInt("ROSLYNMCP_ANALYZER_TIMEOUT_SECONDS", 15));

    /// <summary>
    /// The master switch for reading a dependency's real source rather than a decompilation of
    /// it. Off means navigation never reaches the network and always decompiles.
    /// </summary>
    /// <remarks>
    /// Source embedded in a PDB is deliberately exempt: it is already on disk, so none of the
    /// reasons to turn this off apply to it.
    /// </remarks>
    public static bool ExternalSource { get; set; } = EnvFlag("ROSLYNMCP_EXTERNAL_SOURCE", true);

    /// <summary>
    /// Fetch a dependency's real source when its PDB says where to get it, instead of
    /// decompiling. Off means navigation never reaches the network.
    /// </summary>
    public static bool SourceLink { get; set; } = EnvFlag("ROSLYNMCP_SOURCE_LINK", true);

    /// <summary>
    /// Download PDBs from the Microsoft and NuGet symbol servers. Without this, Source Link only
    /// works for assemblies that ship or embed their own PDB — which the .NET framework
    /// assemblies do not, so this is what makes F12 into the BCL reach real source.
    /// </summary>
    public static bool SymbolServer { get; set; } = EnvFlag("ROSLYNMCP_SYMBOL_SERVER", true);

    /// <summary>
    /// For .NET Framework assemblies, which carry no Source Link: read the matching snapshot of
    /// <c>microsoft/referencesource</c> instead of decompiling.
    /// </summary>
    public static bool ReferenceSource { get; set; } = EnvFlag("ROSLYNMCP_REFERENCE_SOURCE", true);

    /// <summary>
    /// A GitHub token, raising the reference-source lookup's API budget from 60 requests an hour
    /// to 5000. Read from the environment only, never from a settings file that might sync.
    /// </summary>
    public static string? GitHubToken { get; } =
        Environment.GetEnvironmentVariable("ROSLYNMCP_GITHUB_TOKEN") is { Length: > 0 } own
            ? own
            : Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } shared
                ? shared
                : null;

    private static bool EnvFlag(string name, bool fallback) =>
        Environment.GetEnvironmentVariable(name) switch
        {
            "0" or "false" or "off" => false,
            "1" or "true" or "on" => true,
            _ => fallback,
        };

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}
