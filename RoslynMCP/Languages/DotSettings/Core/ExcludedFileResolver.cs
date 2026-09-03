using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.DotSettings.Core;

/// <summary>
/// Turns the GUID-rooted paths in <c>FilesAndFoldersToSkip2</c> into absolute paths on disk.
/// </summary>
/// <remarks>
/// <para>
/// ReSharper does not store an excluded file by its path. It stores it relative to the project
/// that owns it, identified by the project's solution GUID, with each segment tagged by what it
/// is — so <c>Services/ApplicationServiceImpl.cs</c> in one project is written (before unescaping)
/// as <c>155A78F7-41F0-40CE-835B-0F7C74E60CE0/d:Services/f:ApplicationServiceImpl.cs</c>.
/// </para>
/// <para>
/// That choice is why the setting survives a project being moved on disk, and why resolving it
/// needs the solution: the GUID is only meaningful against the <c>Project(...)</c> lines that
/// declare it. A GUID no solution claims resolves to nothing, which is the right answer for a
/// stale entry left behind by a removed project.
/// </para>
/// </remarks>
internal static partial class ExcludedFileResolver
{
    /// <summary>
    /// The specs as absolute paths. Directories are included alongside files — a spec may stop at
    /// a <c>d:</c> segment, meaning the whole folder is excluded.
    /// </summary>
    public static ImmutableHashSet<string> Resolve(
        IReadOnlyCollection<string> specs, string projectPath)
    {
        if (specs.Count == 0)
            return [];

        if (PathHelper.FindNearestSolution(projectPath) is not { Length: > 0 } solution)
            return [];

        var projectsByGuid = ProjectsByGuid(solution);

        if (projectsByGuid.Count == 0)
            return [];

        var resolved = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string spec in specs)
        {
            if (Resolve(spec, projectsByGuid) is { } path)
                resolved.Add(path);
        }

        return resolved.ToImmutable();
    }

    private static string? Resolve(string spec, IReadOnlyDictionary<string, string> projectsByGuid)
    {
        var segments = spec.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2)
            return null;

        if (!projectsByGuid.TryGetValue(segments[0].Trim('{', '}'), out string? projectPath))
            return null;

        if (Path.GetDirectoryName(projectPath) is not { Length: > 0 } directory)
            return null;

        var parts = new List<string> { directory };

        for (int i = 1; i < segments.Length; i++)
        {
            string segment = segments[i];

            // Anything not tagged d: or f: is a shape this does not know; taking the whole spec
            // out beats guessing at a path and excluding a file nobody asked to exclude.
            if (segment.Length < 3 || segment[1] != ':' || segment[0] is not ('d' or 'f'))
                return null;

            parts.Add(segment[2..]);
        }

        try
        {
            return PathHelper.NormalizePath(Path.Combine([.. parts]));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static readonly ConcurrentDictionary<string, (long Stamp, ImmutableDictionary<string, string> Map)>
        s_cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Project GUID to absolute project path, from the solution's own <c>Project(...)</c> lines.
    /// </summary>
    private static ImmutableDictionary<string, string> ProjectsByGuid(string solutionPath)
    {
        long stamp;

        try
        {
            stamp = new FileInfo(solutionPath).LastWriteTimeUtc.Ticks;
        }
        catch (IOException)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        if (s_cache.TryGetValue(solutionPath, out var cached) && cached.Stamp == stamp)
            return cached.Map;

        var map = Parse(solutionPath);
        s_cache[solutionPath] = (stamp, map);
        return map;
    }

    private static ImmutableDictionary<string, string> Parse(string solutionPath)
    {
        string text;

        try
        {
            text = File.ReadAllText(solutionPath);
        }
        catch (IOException)
        {
            return ImmutableDictionary<string, string>.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        if (Path.GetDirectoryName(solutionPath) is not { Length: > 0 } directory)
            return ImmutableDictionary<string, string>.Empty;

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ProjectLine().Matches(text))
        {
            string relative = match.Groups["path"].Value;
            string guid = match.Groups["guid"].Value;

            // Solution folders are Project entries too, and their "path" is just the folder name.
            if (!relative.Contains('.', StringComparison.Ordinal))
                continue;

            try
            {
                string portable = relative.Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                builder[guid] = PathHelper.NormalizePath(Path.Combine(directory, portable));
            }
            catch (ArgumentException)
            {
                // A path the platform will not accept names no project here.
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// <c>Project("{type}") = "Name", "relative\path.csproj", "{guid}"</c> — the classic solution
    /// format. An <c>.slnx</c> carries no GUIDs at all, so it simply yields no matches and the
    /// exclusions resolve to nothing.
    /// </summary>
    [GeneratedRegex(
        """"
        ^Project\("\{[^}]*\}"\)\s*=\s*"[^"]*",\s*"(?<path>[^"]*)",\s*"\{(?<guid>[^}]*)\}"
        """",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectLine();
}
