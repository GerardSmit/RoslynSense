using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using RoslynMCP.Config;
using RoslynMCP.Services;
using RoslynMCP.Services.Database;
using RoslynMCP.Services.Designers;
using RoslynMCP.Services.Run;
using RoslynMCP.Tools;
using RoslynMCP.Languages;

namespace RoslynMCP;

/// <summary>
/// Drives every MCP tool from the command line without running the MCP server.
/// <br/>
/// Usage: <c>roslyn-sense --cli [tool-name] [--param value ...]</c>
/// <br/>
/// Examples:
/// <code>
///   roslyn-sense --cli --help
///   roslyn-sense --cli find_usages --help
///   roslyn-sense --cli find_usages --file-path "C:\src\Foo.ascx" --markup-snippet "ID=\"[|litSizeRemark|]\""
/// </code>
/// </summary>
internal static class CliRunner
{
    /// <summary>
    /// Whether this process is a one-shot CLI invocation rather than a long-lived MCP session.
    /// </summary>
    /// <remarks>
    /// Tools that start something meant to outlive the call — a web app, a debug session — have
    /// to know: everything launched dies with this process, so promising a handle to stop later
    /// would be a lie.
    /// </remarks>
    public static bool IsOneShot { get; private set; }

    // DI-injected parameter types that the runner provides automatically.
    private static readonly HashSet<Type> s_diTypes =
    [
        typeof(IOutputFormatter),
        typeof(CancellationToken),
        typeof(BackgroundTaskStore),
        typeof(BuildWarningsStore),
        typeof(ProfilingSessionStore),
        typeof(IEnumerable<IFindUsagesHandler>),
        typeof(IEnumerable<IGoToDefinitionHandler>),
        typeof(IEnumerable<IOutlineHandler>),
        typeof(IEnumerable<IRenameHandler>),
        typeof(IEnumerable<IDiagnosticsHandler>),
        typeof(DbConnectionRegistry),
        typeof(DesignerRegenerationService),
        typeof(SolutionSessionService),
        typeof(AppSessionStore),
        typeof(AppRunService),
    ];

    // -------------------------------------------------------------------------
    // Entry point
    // -------------------------------------------------------------------------

    public static async Task<int> RunAsync(string[] args)
    {
        IsOneShot = true;

        // --cli --help  →  list all tools
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintGlobalHelp();
            return 0;
        }

        var toolName = args[0];

        // --cli find_usages --help  →  show tool usage
        bool wantHelp = args.Any(a => a is "-h" or "--help");

        var method = FindToolMethod(toolName);
        if (method is null)
        {
            Console.Error.WriteLine($"Unknown tool '{toolName}'. Run 'roslyn-sense --cli --help' to list available tools.");
            return 1;
        }

        if (wantHelp)
        {
            PrintToolHelp(method);
            return 0;
        }

        var parsed = ParseFlags(args[1..]);

        var (config, configPath, configError) = RoslynSenseConfigLoader.Load(Directory.GetCurrentDirectory());
        if (configError is not null)
            Console.Error.WriteLine($"Warning: {configError}");

        var settings = EffectiveSettings.Resolve(args, config, out var settingsWarnings);
        foreach (var w in settingsWarnings)
            Console.Error.WriteLine($"Warning: {w}");
        DebuggerViewOptions.Current = settings.DebugView;

        bool useToon = string.Equals(settings.TableFormat, "toon", StringComparison.OrdinalIgnoreCase);
        var fmt = useToon ? (IOutputFormatter)new ToonFormatter() : new MarkdownFormatter();

        var dbProviders = settings.ExplicitDbProviders;
        if (settings.Database && settings.ShouldRunAutoDiscovery())
        {
            var auto = AutoConnectionStringDiscovery.Discover(Directory.GetCurrentDirectory(), out _);
            var existing = new HashSet<string>(dbProviders.Select(p => p.Alias), StringComparer.OrdinalIgnoreCase);
            var merged = new List<IDbProvider>(dbProviders);
            foreach (var p in auto)
                if (existing.Add(p.Alias))
                    merged.Add(p);
            dbProviders = merged;
        }
        var dbRegistry = new DbConnectionRegistry(dbProviders);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            var result = await InvokeAsync(method, parsed, fmt, dbRegistry, settings, cts.Token);
            Console.WriteLine(result);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            Console.Error.WriteLine($"Error: {tie.InnerException.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    // -------------------------------------------------------------------------
    // Tool discovery
    // -------------------------------------------------------------------------

    private static IReadOnlyList<MethodInfo>? s_allTools;

    private static IReadOnlyList<MethodInfo> AllTools =>
        s_allTools ??= typeof(FindUsagesTool).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(m => ToolCommandName(m))
            .ToList();

    private static MethodInfo? FindToolMethod(string name)
    {
        var normalized = NormalizeCommandName(name);
        return AllTools.FirstOrDefault(m => NormalizeCommandName(ToolCommandName(m)) == normalized);
    }

    // Derive the CLI name from the method: FindUsages → find_usages
    private static string ToolCommandName(MethodInfo m)
    {
        // Check if the attribute has an explicit Name
        var attr = m.GetCustomAttribute<McpServerToolAttribute>()!;
        // McpServerToolAttribute.Name is the MCP protocol name; use it if set
        var name = attr.Name;
        return string.IsNullOrEmpty(name) ? PascalToSnakeCase(m.Name) : name;
    }

    private static string NormalizeCommandName(string s) =>
        s.Replace('-', '_').ToLowerInvariant();

    // -------------------------------------------------------------------------
    // Invocation
    // -------------------------------------------------------------------------

    private static async Task<string> InvokeAsync(
        MethodInfo method, Dictionary<string, string> parsed,
        IOutputFormatter fmt, DbConnectionRegistry dbRegistry, EffectiveSettings settings,
        CancellationToken ct)
    {
        // Build lazily — only create the language packs we actually need
        LanguageRegistry? languages = null;
        BackgroundTaskStore? taskStore = null;
        BuildWarningsStore? warningsStore = null;
        ProfilingSessionStore? profilingStore = null;
        ProfileRecordingStore? recordingStore = null;
        RoslynMCP.Services.Memory.MemorySnapshotStore? memoryStore = null;
        DesignerRegenerationService? designerService = null;
        SolutionSessionService? solutionSession = null;
        AppSessionStore? appStore = null;
        AppRunService? appRunner = null;

        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var pt = p.ParameterType;

            // ---- DI-injected ------------------------------------------------
            if (pt == typeof(IOutputFormatter)) { values[i] = fmt; continue; }
            if (pt == typeof(CancellationToken)) { values[i] = ct; continue; }
            if (pt == typeof(BackgroundTaskStore))
            {
                values[i] = taskStore ??= new BackgroundTaskStore();
                continue;
            }
            if (pt == typeof(BuildWarningsStore))
            {
                values[i] = warningsStore ??= new BuildWarningsStore();
                continue;
            }
            if (pt == typeof(ProfilingSessionStore))
            {
                values[i] = profilingStore ??= new ProfilingSessionStore();
                continue;
            }
            if (pt == typeof(ProfileRecordingStore))
            {
                values[i] = recordingStore ??= new ProfileRecordingStore();
                continue;
            }
            if (pt == typeof(RoslynMCP.Services.Memory.MemorySnapshotStore))
            {
                values[i] = memoryStore ??= new RoslynMCP.Services.Memory.MemorySnapshotStore();
                continue;
            }
            if (pt == typeof(DesignerRegenerationService))
            {
                values[i] = designerService ??= CreateDesignerService(settings);
                continue;
            }
            if (pt == typeof(AppSessionStore))
            {
                values[i] = appStore ??= new AppSessionStore();
                continue;
            }
            if (pt == typeof(AppRunService))
            {
                values[i] = appRunner ??= new AppRunService(appStore ??= new AppSessionStore());
                continue;
            }
            if (pt == typeof(SolutionSessionService))
            {
                // A CLI invocation is a single shot, so watching would never observe a change.
                values[i] = solutionSession ??= new SolutionSessionService(
                    designerService ??= CreateDesignerService(settings));
                continue;
            }
            if (pt == typeof(IEnumerable<IFindUsagesHandler>))
            {
                values[i] = Languages(ref languages, settings, fmt).FindUsagesHandlers;
                continue;
            }
            if (pt == typeof(IEnumerable<IGoToDefinitionHandler>))
            {
                values[i] = Languages(ref languages, settings, fmt).GoToDefinitionHandlers;
                continue;
            }
            if (pt == typeof(IEnumerable<IOutlineHandler>))
            {
                values[i] = Languages(ref languages, settings, fmt).OutlineHandlers;
                continue;
            }
            if (pt == typeof(IEnumerable<IRenameHandler>))
            {
                values[i] = Languages(ref languages, settings, fmt).RenameHandlers;
                continue;
            }
            if (pt == typeof(IEnumerable<IDiagnosticsHandler>))
            {
                values[i] = Languages(ref languages, settings, fmt).DiagnosticsHandlers;
                continue;
            }
            if (pt == typeof(DbConnectionRegistry))
            {
                values[i] = dbRegistry;
                continue;
            }

            // ---- User-supplied ----------------------------------------------
            // Accept --camelCase, --kebab-case, --snake_case
            var lookupKeys = new[]
            {
                p.Name!,
                ToKebabCase(p.Name!),
                PascalToSnakeCase(p.Name!)
            };

            if (TryGetParsed(parsed, lookupKeys, out var raw))
            {
                values[i] = ConvertValue(raw, pt, p.Name!);
            }
            else if (p.HasDefaultValue)
            {
                values[i] = p.DefaultValue;
            }
            else
            {
                throw new ArgumentException(
                    $"Required parameter '--{ToKebabCase(p.Name!)}' is missing.");
            }
        }

        var result = method.Invoke(null, values);
        return result switch
        {
            Task<string> t => await t,
            Task t => await t.ContinueWith(_ => "Done."),
            string s => s,
            _ => result?.ToString() ?? ""
        };
    }

    // -------------------------------------------------------------------------
    // Argument parsing
    // -------------------------------------------------------------------------

    /// <summary>Parses --key value / --key=value / --flag pairs into a dictionary.</summary>
    private static Dictionary<string, string> ParseFlags(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < args.Length)
        {
            var arg = args[i];
            if (!arg.StartsWith('-'))
            {
                i++;
                continue; // skip positional args (not expected)
            }

            // --key=value
            var eqIdx = arg.IndexOf('=');
            if (eqIdx >= 0)
            {
                var key = arg[..eqIdx].TrimStart('-');
                result[key] = arg[(eqIdx + 1)..];
                i++;
                continue;
            }

            var flagKey = arg.TrimStart('-');

            // --key value  (next token doesn't start with -)
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                result[flagKey] = args[i + 1];
                i += 2;
            }
            else
            {
                // --flag (boolean)
                result[flagKey] = "true";
                i++;
            }
        }
        return result;
    }

    private static bool TryGetParsed(
        Dictionary<string, string> parsed, string[] keys, out string value)
    {
        foreach (var k in keys)
        {
            if (parsed.TryGetValue(k, out var v)) { value = v; return true; }
        }
        value = "";
        return false;
    }

    // -------------------------------------------------------------------------
    // Type conversion
    // -------------------------------------------------------------------------

    private static object? ConvertValue(string raw, Type target, string paramName)
    {
        // Unwrap Nullable<T>
        var underlying = Nullable.GetUnderlyingType(target);
        if (underlying is not null)
        {
            if (string.IsNullOrEmpty(raw) || raw.Equals("null", StringComparison.OrdinalIgnoreCase))
                return null;
            target = underlying;
        }

        if (target == typeof(string)) return raw;
        if (target == typeof(bool)) return ParseBool(raw, paramName);
        if (target == typeof(int)) return ParseInt(raw, paramName);
        if (target == typeof(long)) return ParseLong(raw, paramName);

        throw new ArgumentException($"Unsupported parameter type '{target.Name}' for --{ToKebabCase(paramName)}.");
    }

    private static bool ParseBool(string raw, string name)
    {
        if (string.IsNullOrEmpty(raw) || raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        throw new ArgumentException($"--{ToKebabCase(name)} expects true/false, got '{raw}'.");
    }

    private static int ParseInt(string raw, string name)
    {
        if (int.TryParse(raw, out var v)) return v;
        throw new ArgumentException($"--{ToKebabCase(name)} expects an integer, got '{raw}'.");
    }

    private static long ParseLong(string raw, string name)
    {
        if (long.TryParse(raw, out var v)) return v;
        throw new ArgumentException($"--{ToKebabCase(name)} expects a number, got '{raw}'.");
    }

    // -------------------------------------------------------------------------
    // Help rendering
    // -------------------------------------------------------------------------

    private static void PrintGlobalHelp()
    {
        Console.WriteLine("roslyn-sense --cli <tool> [options]");
        Console.WriteLine();
        Console.WriteLine("Available tools:");
        Console.WriteLine();

        foreach (var m in AllTools)
        {
            var cmdName = ToolCommandName(m);
            var desc = m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
            // Trim to first sentence for compact listing
            var dot = desc.IndexOf(". ", StringComparison.Ordinal);
            var summary = dot > 0 ? desc[..(dot + 1)] : desc;
            if (summary.Length > 80) summary = summary[..77] + "...";
            Console.WriteLine($"  {cmdName,-36} {summary}");
        }

        Console.WriteLine();
        Console.WriteLine("Run 'roslyn-sense --cli <tool> --help' for per-tool options.");
    }

    private static void PrintToolHelp(MethodInfo method)
    {
        var cmdName = ToolCommandName(method);
        var desc = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        Console.WriteLine($"roslyn-sense --cli {cmdName} [options]");
        Console.WriteLine();
        if (!string.IsNullOrEmpty(desc))
        {
            Console.WriteLine(desc);
            Console.WriteLine();
        }
        Console.WriteLine("Options:");
        foreach (var p in method.GetParameters())
        {
            if (s_diTypes.Contains(p.ParameterType)) continue; // skip DI params

            var flag = ToKebabCase(p.Name!);
            var typeName = FriendlyTypeName(p.ParameterType);
            var defaultStr = p.HasDefaultValue
                ? $" (default: {p.DefaultValue ?? "null"})"
                : " (required)";
            var paramDesc = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

            Console.Write($"  --{flag,-30} <{typeName}>{defaultStr}");
            if (!string.IsNullOrEmpty(paramDesc))
            {
                Console.WriteLine();
                Console.WriteLine($"      {paramDesc}");
            }
            else
            {
                Console.WriteLine();
            }
        }
        Console.WriteLine();
        Console.WriteLine("Additional flags:");
        Console.WriteLine("  --toon                          Use TOON formatter instead of Markdown");
    }

    private static string FriendlyTypeName(Type t)
    {
        var u = Nullable.GetUnderlyingType(t);
        if (u is not null) return FriendlyTypeName(u) + "?";
        if (t == typeof(string)) return "string";
        if (t == typeof(int)) return "int";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(long)) return "long";
        return t.Name;
    }

    /// <summary>
    /// The designer generators enabled for this invocation. The aspx one is gated the same way the
    /// MCP server and the shared host gate it, so <c>--no-webforms</c> means the same thing on every
    /// entry point: nobody rewrites a <c>.designer.cs</c> behind the user's back.
    /// </summary>
    internal static DesignerRegenerationService CreateDesignerService(EffectiveSettings settings)
    {
        var generators = new List<IDesignerGenerator> { new DbmlDesignerGenerator() };
        if (settings.WebForms)
            generators.Add(new AspxDesignerGenerator());
        return new DesignerRegenerationService(generators);
    }

    /// <summary>
    /// The language packs enabled for this invocation, built once. The CLI has no container, so
    /// the registry is constructed directly rather than resolved — same gate, same order.
    /// </summary>
    private static LanguageRegistry Languages(
        ref LanguageRegistry? cached, EffectiveSettings settings, IOutputFormatter fmt) =>
        cached ??= new LanguageRegistry(LanguagePackRegistration.Create(settings, fmt)).Publish();

    // -------------------------------------------------------------------------
    // Naming helpers
    // -------------------------------------------------------------------------

    // FindUsages → find_usages
    private static string PascalToSnakeCase(string s) =>
        Regex.Replace(s, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();

    // filePath → file-path
    private static string ToKebabCase(string s) =>
        Regex.Replace(s, "(?<=[a-z0-9])([A-Z])", "-$1").ToLowerInvariant();
}
