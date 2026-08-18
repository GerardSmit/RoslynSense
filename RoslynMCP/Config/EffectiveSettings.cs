using RoslynMCP.Languages.Resources;
using RoslynMCP.Services.Database;

namespace RoslynMCP.Config;

public sealed record EffectiveSettings(
    bool WebForms,
    bool Razor,
    bool Proto,
    bool Mediator,
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
    /// <summary>
    /// The resources pack's gate and its resolved lookup set.
    /// </summary>
    /// <remarks>
    /// An init-only property rather than one more positional parameter: the record already carries
    /// thirteen, and the single construction site would have to grow another argument for a value
    /// every other caller of this type ignores.
    /// </remarks>
    internal ResourceSettings Resources { get; init; } = ResourceSettings.Disabled;

    /// <summary>The project-file pack's gate. Init-only for the same reason as
    /// <see cref="Resources"/>.</summary>
    internal bool MsBuild { get; init; } = true;

    /// <summary>The LINQ to SQL pack's gate. Init-only for the same reason as
    /// <see cref="Resources"/>.</summary>
    internal bool Dbml { get; init; } = true;

    /// <summary>The application-settings pack's gate. Init-only for the same reason as
    /// <see cref="Resources"/>.</summary>
    internal bool AppSettings { get; init; } = true;

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
        bool proto = !HasFlag("--no-proto") && tools.Proto;
        bool mediator = !HasFlag("--no-mediator") && tools.Mediator;
        var resources = ResourceSettings.Resolve(
            !HasFlag("--no-resources") && tools.Resources, config?.Resources, warnings);
        bool msBuild = !HasFlag("--no-msbuild") && tools.MsBuild;
        bool dbml = !HasFlag("--no-dbml") && tools.Dbml;
        bool appSettings = !HasFlag("--no-appsettings") && tools.AppSettings;
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
            webForms, razor, proto, mediator, debugger, profiling, database,
            autoDiscover, tableFormat, explicitProviders,
            preload, sharedHost, hostIdleMinutes, maxWorkspaces)
        {
            Resources = resources,
            MsBuild = msBuild,
            Dbml = dbml,
            AppSettings = appSettings,
        };
    }

    public bool ShouldRunAutoDiscovery()
    {
        if (!Database) return false;
        if (AutoDiscoverDb == false) return false;
        if (AutoDiscoverDb == true) return true;
        return ExplicitDbProviders.Count == 0;
    }
}
