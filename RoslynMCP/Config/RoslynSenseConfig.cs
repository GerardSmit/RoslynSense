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

public sealed class DatabaseConfig
{
    public bool? AutoDiscovery { get; init; }
    public Dictionary<string, ConnectionEntry> Connections { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

[JsonConverter(typeof(ConnectionEntryConverter))]
public sealed record ConnectionEntry(string Provider, string ConnectionString);
