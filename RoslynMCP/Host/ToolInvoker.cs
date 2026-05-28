using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Tools;

namespace RoslynMCP.Daemon;

/// <summary>
/// Canonical tool dispatch table + binder shared by the shared-host daemon (and reusable by
/// the CLI). Discovers every <c>[McpServerTool]</c> static method, binds its parameters from a
/// supplied <see cref="IServiceProvider"/> (DI services) plus a CLI/IPC string-arg dictionary
/// (user scalars), and invokes it. Tool outputs are always strings.
/// </summary>
internal static partial class ToolInvoker
{
    private static IReadOnlyList<MethodInfo>? s_allTools;

    public static IReadOnlyList<MethodInfo> AllTools =>
        s_allTools ??= typeof(FindUsagesTool).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(ToolCommandName)
            .ToList();

    public static MethodInfo? FindTool(string name)
    {
        var normalized = NormalizeCommandName(name);
        return AllTools.FirstOrDefault(m => NormalizeCommandName(ToolCommandName(m)) == normalized);
    }

    private static IReadOnlyList<MethodInfo>? s_allResources;

    /// <summary>Every <c>[McpServerResource]</c> static method in the assembly.</summary>
    public static IReadOnlyList<MethodInfo> AllResources =>
        s_allResources ??= typeof(FindUsagesTool).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerResourceAttribute>() is not null)
            .ToList();

    public static string ResourceName(MethodInfo m)
    {
        var attr = m.GetCustomAttribute<McpServerResourceAttribute>()!;
        return string.IsNullOrEmpty(attr.Name) ? PascalToSnakeCase(m.Name) : attr.Name;
    }

    public static MethodInfo? FindResource(string name) =>
        AllResources.FirstOrDefault(m => string.Equals(ResourceName(m), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>FindUsages → find_usages (or the attribute's explicit Name).</summary>
    public static string ToolCommandName(MethodInfo m)
    {
        var attr = m.GetCustomAttribute<McpServerToolAttribute>()!;
        return string.IsNullOrEmpty(attr.Name) ? PascalToSnakeCase(m.Name) : attr.Name;
    }

    public static string NormalizeCommandName(string s) => s.Replace('-', '_').ToLowerInvariant();

    /// <summary>
    /// Binds and invokes <paramref name="method"/>. DI parameters are resolved from
    /// <paramref name="services"/>; user scalars (string/int/long/bool and their nullable
    /// forms) from <paramref name="args"/>. <see cref="IOutputFormatter"/> and
    /// <see cref="CancellationToken"/> are supplied per-call so a shared daemon can honor each
    /// client's requested output format.
    /// </summary>
    public static async Task<string> InvokeAsync(
        MethodInfo method,
        IReadOnlyDictionary<string, string> args,
        IServiceProvider services,
        IOutputFormatter fmt,
        CancellationToken ct)
    {
        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var pt = p.ParameterType;

            if (pt == typeof(IOutputFormatter)) { values[i] = fmt; continue; }
            if (pt == typeof(CancellationToken)) { values[i] = ct; continue; }

            if (IsUserScalar(pt))
            {
                if (TryGetArg(args, p.Name!, out var raw))
                    values[i] = ConvertValue(raw, pt, p.Name!);
                else if (p.HasDefaultValue)
                    values[i] = p.DefaultValue;
                else
                    throw new ArgumentException($"Required parameter '--{ToKebabCase(p.Name!)}' is missing.");
                continue;
            }

            // Everything else is a DI service (stores, registries, handler collections, settings).
            var svc = services.GetService(pt);
            if (svc is not null) { values[i] = svc; continue; }
            values[i] = p.HasDefaultValue ? p.DefaultValue : null;
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

    private static bool IsUserScalar(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        return u == typeof(string) || u == typeof(bool) || u == typeof(int) || u == typeof(long);
    }

    private static bool TryGetArg(IReadOnlyDictionary<string, string> args, string paramName, out string value)
    {
        foreach (var key in new[] { paramName, ToKebabCase(paramName), PascalToSnakeCase(paramName) })
        {
            if (args.TryGetValue(key, out var v)) { value = v; return true; }
        }
        value = "";
        return false;
    }

    private static object? ConvertValue(string raw, Type target, string paramName)
    {
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

    private static int ParseInt(string raw, string name) =>
        int.TryParse(raw, out var v) ? v : throw new ArgumentException($"--{ToKebabCase(name)} expects an integer, got '{raw}'.");

    private static long ParseLong(string raw, string name) =>
        long.TryParse(raw, out var v) ? v : throw new ArgumentException($"--{ToKebabCase(name)} expects a number, got '{raw}'.");

    private static string PascalToSnakeCase(string s) =>
        SnakeRegex().Replace(s, "_$1").ToLowerInvariant();

    private static string ToKebabCase(string s) =>
        SnakeRegex().Replace(s, "-$1").ToLowerInvariant();

    [GeneratedRegex("(?<=[a-z0-9])([A-Z])")]
    private static partial Regex SnakeRegex();
}
