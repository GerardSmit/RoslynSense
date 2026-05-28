using System.Text.Json.Serialization;

namespace RoslynMCP.Config;

public sealed class RoslynSenseConfig
{
    public ToolsConfig Tools { get; init; } = new();
    public DatabaseConfig Database { get; init; } = new();
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
    public bool Debugger { get; init; } = true;
    public bool Profiling { get; init; } = true;
    public bool Database { get; init; } = true;
}

public sealed class DatabaseConfig
{
    public bool? AutoDiscovery { get; init; }
    public Dictionary<string, ConnectionEntry> Connections { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

[JsonConverter(typeof(ConnectionEntryConverter))]
public sealed record ConnectionEntry(string Provider, string ConnectionString);
