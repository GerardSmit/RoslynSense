namespace RoslynMCP.Config;

/// <summary>
/// Feature switches for the editor-facing paths. Static because the LSP handlers and the
/// analyzer services are static too (matching <see cref="Services.WorkspaceService"/>);
/// values come from environment variables at startup and are settable for tests.
/// </summary>
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

    /// <summary>Per-document time budget for an analyzer pass.</summary>
    public static TimeSpan AnalyzerTimeout { get; set; } =
        TimeSpan.FromSeconds(EnvInt("ROSLYNMCP_ANALYZER_TIMEOUT_SECONDS", 15));

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
