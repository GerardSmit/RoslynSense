using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using RoslynMCP.Config;
using RoslynMCP.Services;
using RoslynMCP.Services.Database;
using RoslynMCP.Tools; // IFindUsagesHandler, IGoToDefinitionHandler, etc.
using RoslynMCP.Tools.Razor;
using RoslynMCP.Tools.WebForms;

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
    // DI-injected parameter types that the runner provides automatically.
    private static readonly HashSet<Type> s_diTypes =
    [
        typeof(IOutputFormatter),
        typeof(CancellationToken),
        typeof(BackgroundTaskStore),
        typeof(ProfilingSessionStore),
        typeof(IEnumerable<IFindUsagesHandler>),
        typeof(IEnumerable<IGoToDefinitionHandler>),
        typeof(IEnumerable<IOutlineHandler>),
        typeof(IEnumerable<IRenameHandler>),
        typeof(IEnumerable<IDiagnosticsHandler>),
        typeof(DbConnectionRegistry),
    ];

    // -------------------------------------------------------------------------
    // Entry point
    // -------------------------------------------------------------------------

    public static async Task<int> RunAsync(string[] args)
    {
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
            Console.Error.WriteLine($"Warning: roslynsense.json ({configPath}): {configError}");

        var settings = EffectiveSettings.Resolve(args, config, out var settingsWarnings);
        foreach (var w in settingsWarnings)
            Console.Error.WriteLine($"Warning: {w}");

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
            var result = await InvokeAsync(method, parsed, fmt, dbRegistry, cts.Token);
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
        IOutputFormatter fmt, DbConnectionRegistry dbRegistry, CancellationToken ct)
    {
        // Build lazily — only create handler instances we actually need
        IFindUsagesHandler[]? findUsagesHandlers = null;
        IGoToDefinitionHandler[]? goToDefHandlers = null;
        IOutlineHandler[]? outlineHandlers = null;
        IRenameHandler[]? renameHandlers = null;
        IDiagnosticsHandler[]? diagnosticsHandlers = null;
        BackgroundTaskStore? taskStore = null;
        ProfilingSessionStore? profilingStore = null;

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
            if (pt == typeof(ProfilingSessionStore))
            {
                values[i] = profilingStore ??= new ProfilingSessionStore();
                continue;
            }
            if (pt == typeof(IEnumerable<IFindUsagesHandler>))
            {
                values[i] = findUsagesHandlers ??= [new AspxFindUsages(fmt)];
                continue;
            }
            if (pt == typeof(IEnumerable<IGoToDefinitionHandler>))
            {
                values[i] = goToDefHandlers ??= [new AspxGoToDefinition(fmt), new RazorGoToDefinition(fmt)];
                continue;
            }
            if (pt == typeof(IEnumerable<IOutlineHandler>))
            {
                values[i] = outlineHandlers ??= [new AspxOutline(), new RazorOutline()];
                continue;
            }
            if (pt == typeof(IEnumerable<IRenameHandler>))
            {
                values[i] = renameHandlers ??= [new AspxRename(), new RazorRename()];
                continue;
            }
            if (pt == typeof(IEnumerable<IDiagnosticsHandler>))
            {
                values[i] = diagnosticsHandlers ??= [new AspxDiagnostics(), new RazorDiagnostics()];
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
