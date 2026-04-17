using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace RoslynMCP.Services;

/// <summary>
/// Caches build warnings from the most recent build per project path, grouped by warning code.
/// Populated by BuildProjectTool; queried by GetBuildWarningsTool.
/// </summary>
public sealed class BuildWarningsStore
{
    // key: normalized project path → (warning code → raw warning lines)
    private readonly ConcurrentDictionary<string, Dictionary<string, List<string>>> _cache = new();

    private static readonly Regex WarningCodeRegex =
        new(@"\bwarning\s+([A-Za-z]+\d+)\s*:", RegexOptions.Compiled);

    // Captures the message text after "warning CODE: ", stripping trailing " [project.csproj]"
    private static readonly Regex WarningMessageRegex =
        new(@"\bwarning\s+[A-Za-z]+\d+\s*:\s*(.+?)(?:\s*\[.*?\])?\s*$", RegexOptions.Compiled);

    /// <summary>Stores all parsed warning lines for a given build target, replacing any prior data.</summary>
    public void Store(string resolvedProjectPath, IEnumerable<string> warningLines)
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in warningLines)
        {
            var m = WarningCodeRegex.Match(line);
            if (!m.Success) continue;
            var code = m.Groups[1].Value.ToUpperInvariant();
            if (!grouped.TryGetValue(code, out var list))
                grouped[code] = list = [];
            list.Add(line.Trim());
        }
        _cache[NormalizeKey(resolvedProjectPath)] = grouped;
    }

    /// <summary>
    /// Returns all raw warning lines for the given project path and warning code.
    /// Returns null if the project has no cached build data.
    /// Returns an empty list if the project was built but has no warnings with that code.
    /// </summary>
    public IReadOnlyList<string>? GetWarnings(string resolvedProjectPath, string warningCode)
    {
        if (!_cache.TryGetValue(NormalizeKey(resolvedProjectPath), out var byCode))
            return null;
        return byCode.TryGetValue(warningCode.ToUpperInvariant(), out var list)
            ? list
            : [];
    }

    /// <summary>Returns grouped warning data (code → lines) for a project, for use in build summaries.</summary>
    public IReadOnlyDictionary<string, List<string>>? GetAll(string resolvedProjectPath) =>
        _cache.TryGetValue(NormalizeKey(resolvedProjectPath), out var byCode) ? byCode : null;

    /// <summary>Extracts just the message text from a raw warning line, stripping file path and project path.</summary>
    public static string ExtractMessage(string rawLine)
    {
        var m = WarningMessageRegex.Match(rawLine);
        return m.Success ? m.Groups[1].Value.Trim() : rawLine.Trim();
    }

    private static string NormalizeKey(string path) =>
        path.TrimEnd('\\', '/').ToUpperInvariant();
}
