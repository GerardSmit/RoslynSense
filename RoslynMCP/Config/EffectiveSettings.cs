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
    IReadOnlyList<string>? Preload)
{
    public static EffectiveSettings Resolve(string[] args, RoslynSenseConfig? config, out List<string> warnings)
    {
        warnings = new List<string>();

        bool HasFlag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

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

        return new EffectiveSettings(
            webForms, razor, debugger, profiling, database,
            autoDiscover, tableFormat, explicitProviders,
            config?.Preload);
    }

    public bool ShouldRunAutoDiscovery()
    {
        if (!Database) return false;
        if (AutoDiscoverDb == false) return false;
        if (AutoDiscoverDb == true) return true;
        return ExplicitDbProviders.Count == 0;
    }
}
