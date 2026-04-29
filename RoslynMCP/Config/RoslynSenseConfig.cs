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
