using RoslynMCP.Services.Database;

namespace RoslynMCP.Config;

public sealed record EffectiveSettings(
    bool WebForms,
    bool Razor,
    bool Debugger,
    bool Profiling,
    bool Database,
    bool? AutoDiscoverDb,
    string? TableFormat,
    IReadOnlyList<IDbProvider> ExplicitDbProviders,
    IReadOnlyList<string>? Preload,
    bool SharedHost,
    int HostIdleMinutes,
    int MaxWorkspaces)
{
    public static EffectiveSettings Resolve(string[] args, RoslynSenseConfig? config, out List<string> warnings)
    {
        warnings = new List<string>();

        bool HasFlag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

        static string? Env(string name) => Environment.GetEnvironmentVariable(name);
        static int EnvInt(string name, int? configVal, int fallback) =>
            int.TryParse(Env(name), out var v) && v > 0 ? v : (configVal is > 0 ? configVal.Value : fallback);

        // Shared host: env wins (ROSLYNMCP_SHARED_HOST=0/1), then config, default on.
        bool sharedHost = Env("ROSLYNMCP_SHARED_HOST") switch
        {
            "0" or "false" or "off" => false,
            "1" or "true" or "on" => true,
            _ => config?.SharedHost ?? true,
        };
        int hostIdleMinutes = EnvInt("ROSLYNMCP_HOST_IDLE_MINUTES", config?.HostIdleMinutes, 30);
        int maxWorkspaces = EnvInt("ROSLYNMCP_MAX_WORKSPACES", config?.MaxWorkspaces, 4);

        var tools = config?.Tools ?? new ToolsConfig();

        bool webForms = !HasFlag("--no-webforms") && tools.WebForms;
        bool razor = !HasFlag("--no-razor") && tools.Razor;
        bool debugger = !HasFlag("--no-debugger") && tools.Debugger;
        bool profiling = !HasFlag("--no-profiling") && tools.Profiling;
        bool database = !HasFlag("--no-db") && tools.Database;

        bool? autoDiscover = HasFlag("--no-auto-db") ? false : config?.Database.AutoDiscovery;

        string? tableFormat = HasFlag("--toon") ? "toon" : config?.TableFormat;

        var explicitProviders = new List<IDbProvider>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var p in DbCliParser.Parse(args))
            {
                if (seen.Add(p.Alias))
                    explicitProviders.Add(p);
            }
        }
        catch (ArgumentException ex)
        {
            warnings.Add($"--db: {ex.Message}");
        }

        if (config?.Database.Connections is { Count: > 0 } configConnections)
        {
            foreach (var (alias, entry) in configConnections)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    warnings.Add("Config connection has empty alias; skipped.");
                    continue;
                }
                if (!seen.Add(alias)) continue;

                try
                {
                    explicitProviders.Add(DbProviderFactory.Create(entry.Provider, alias, entry.ConnectionString));
                }
                catch (ArgumentException ex)
                {
                    warnings.Add($"Config connection '{alias}': {ex.Message}");
                }
            }
        }

        IReadOnlyList<string>? preload = HasFlag("--no-preload") ? [] : config?.Preload;

        return new EffectiveSettings(
            webForms, razor, debugger, profiling, database,
            autoDiscover, tableFormat, explicitProviders,
            preload, sharedHost, hostIdleMinutes, maxWorkspaces);
    }

    public bool ShouldRunAutoDiscovery()
    {
        if (!Database) return false;
        if (AutoDiscoverDb == false) return false;
        if (AutoDiscoverDb == true) return true;
        return ExplicitDbProviders.Count == 0;
    }
}
