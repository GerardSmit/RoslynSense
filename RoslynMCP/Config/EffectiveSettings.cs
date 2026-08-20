using System.Collections.Immutable;
using RoslynMCP.Languages.Logging;
using RoslynMCP.Languages.Resources;
using RoslynMCP.Languages.Values;
using RoslynMCP.Languages.WebConfig.Core;
using RoslynMCP.Languages.WebForms;
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

    /// <summary>The logging-template pack's gate and which of its rules run. Init-only for the
    /// same reason as <see cref="Resources"/>.</summary>
    internal LoggingSettings Logging { get; init; } = LoggingSettings.Disabled;

    /// <summary>The value-set pack's gate, its sets and where they are bound. Init-only for the
    /// same reason as <see cref="Resources"/>.</summary>
    internal ValueSettings ValueSets { get; init; } = ValueSettings.Disabled;

    /// <summary>The format-string pack's gate. Init-only for the same reason as
    /// <see cref="Resources"/>.</summary>
    internal bool Formatting { get; init; } = true;

    /// <summary>Which markup attributes are read as data expressions. Init-only for the same
    /// reason as <see cref="Resources"/>.</summary>
    internal MarkupBindingSettings MarkupBindings { get; init; } = MarkupBindingSettings.None;

    /// <summary>The project-file pack's gate. Init-only for the same reason as
    /// <see cref="Resources"/>.</summary>
    internal bool MsBuild { get; init; } = true;

    /// <summary>The LINQ to SQL pack's gate. Init-only for the same reason as
    /// <see cref="Resources"/>.</summary>
    internal bool Dbml { get; init; } = true;

    /// <summary>The application-settings pack's gate. Init-only for the same reason as
    /// <see cref="Resources"/>.</summary>
    internal bool AppSettings { get; init; } = true;

    /// <summary>The <c>web.config</c> pack's gate. Init-only for the same reason as
    /// <see cref="Resources"/>.</summary>
    internal bool WebConfig { get; init; } = true;

    /// <summary>
    /// File names the <c>web.config</c> pack claims beyond the two built-in ones, already
    /// validated and de-duplicated. Empty is the normal case.
    /// </summary>
    internal ImmutableArray<string> WebConfigFiles { get; init; } = [];

    /// <summary>
    /// The ReSharper-settings pack's gate. Init-only for the same reason as <see cref="Resources"/>.
    /// </summary>
    /// <remarks>
    /// Unlike the other gates this one does not only decide whether requests about a file type are
    /// answered — the pack answers none. It decides whether a committed <c>.DotSettings</c> is
    /// allowed to change the namespace inferred for a new file, the files a search returns, and the
    /// types a coverage run counts. Turning it off is how a team that has stale layers in the
    /// repository gets RoslynSense's own defaults back.
    /// </remarks>
    internal bool DotSettings { get; init; } = true;

    /// <summary>
    /// Which <c>System.Diagnostics</c> debugger attributes the debug engines honour. Init-only for
    /// the same reason as <see cref="Resources"/>.
    /// </summary>
    public Debugger.DebugDisplayOptions DebugView { get; init; } = new();

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
        var markupBindings = webForms
            ? MarkupBindingSettings.Resolve(config?.WebForms, warnings)
            : MarkupBindingSettings.None;
        bool razor = !HasFlag("--no-razor") && tools.Razor;
        bool proto = !HasFlag("--no-proto") && tools.Proto;
        bool mediator = !HasFlag("--no-mediator") && tools.Mediator;
        var resources = ResourceSettings.Resolve(
            !HasFlag("--no-resources") && tools.Resources, config?.Resources, warnings);
        bool msBuild = !HasFlag("--no-msbuild") && tools.MsBuild;
        bool dbml = !HasFlag("--no-dbml") && tools.Dbml;
        bool appSettings = !HasFlag("--no-appsettings") && tools.AppSettings;
        bool webConfig = !HasFlag("--no-webconfig") && tools.WebConfig;
        var webConfigFiles = ResolveWebConfigFiles(config?.WebConfig, webConfig, warnings);
        bool dotSettings = !HasFlag("--no-dotsettings") && tools.DotSettings;
        var logging = LoggingSettings.Resolve(
            !HasFlag("--no-logging") && tools.Logging, config?.Logging);
        var valueSets = ValueSettings.Resolve(
            !HasFlag("--no-valuesets") && tools.ValueSets, config?.ValueSets, warnings);
        bool formatting = !HasFlag("--no-formatting") && tools.Formatting;
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
            DebugView = DebuggerViewOptions.Resolve(config?.Debugger, args),
            Resources = resources,
            MsBuild = msBuild,
            Dbml = dbml,
            AppSettings = appSettings,
            WebConfig = webConfig,
            WebConfigFiles = webConfigFiles,
            DotSettings = dotSettings,
            Logging = logging,
            ValueSets = valueSets,
            Formatting = formatting,
            MarkupBindings = markupBindings,
        };
    }

    /// <summary>
    /// The additional <c>web.config</c>-shaped file names, minus the ones that would take a file
    /// belonging to something else. Every rejection is a warning rather than a silent drop: a name
    /// in the file that does nothing is a question the user will otherwise ask twice.
    /// </summary>
    private static ImmutableArray<string> ResolveWebConfigFiles(
        WebConfigConfig? config, bool packEnabled, List<string> warnings)
    {
        if (config?.AdditionalFiles is not { Count: > 0 } declared)
            return [];

        if (!packEnabled)
        {
            warnings.Add(
                "webConfig.additionalFiles is set but the web.config pack is off; the names do nothing.");
            return [];
        }

        var names = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? raw in declared)
        {
            string name = raw?.Trim() ?? string.Empty;
            if (name.Length == 0)
                continue;

            if (name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
                || name.Contains('*') || name.Contains('?'))
            {
                warnings.Add(
                    $"webConfig.additionalFiles '{name}': a file name, not a path or a glob; skipped.");
                continue;
            }

            // NuGet's two are why the pack claims names instead of the .config extension in the
            // first place; letting a config file hand them over would undo that from the outside.
            if (s_reservedConfigNames.Contains(name))
            {
                warnings.Add($"webConfig.additionalFiles '{name}': belongs to NuGet; skipped.");
                continue;
            }

            if (WebConfigFile.BuiltInNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            if (seen.Add(name))
                names.Add(name);
        }

        return names.ToImmutable();
    }

    private static readonly HashSet<string> s_reservedConfigNames =
        new(StringComparer.OrdinalIgnoreCase) { "packages.config", "nuget.config" };

    public bool ShouldRunAutoDiscovery()
    {
        if (!Database) return false;
        if (AutoDiscoverDb == false) return false;
        if (AutoDiscoverDb == true) return true;
        return ExplicitDbProviders.Count == 0;
    }
}
