using System.Text.RegularExpressions;

namespace RoslynMCP.Services.Database;

/// <summary>
/// Expands <c>${gitRoot}</c>, <c>${solutionRoot}</c>, and <c>${env:NAME}</c> placeholders
/// in path strings. Used for config-file references that must stay portable across machines.
/// </summary>
public static class PathVariableExpander
{
    private static readonly Regex s_varPattern = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    public static string Expand(string value, string? baseDir = null)
    {
        baseDir ??= Directory.GetCurrentDirectory();
        return s_varPattern.Replace(value, match =>
        {
            var name = match.Groups[1].Value;
            if (name.Equals("gitRoot", StringComparison.OrdinalIgnoreCase))
                return FindGitRoot(baseDir) ?? throw new InvalidOperationException(
                    $"Could not find .git directory walking up from '{baseDir}'.");
            if (name.Equals("solutionRoot", StringComparison.OrdinalIgnoreCase))
                return FindSolutionRoot(baseDir) ?? throw new InvalidOperationException(
                    $"Could not find a .sln or .slnx file walking up from '{baseDir}'.");
            if (name.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            {
                var envName = name[4..];
                return Environment.GetEnvironmentVariable(envName) ?? throw new InvalidOperationException(
                    $"Environment variable '{envName}' is not set.");
            }
            throw new InvalidOperationException(
                $"Unknown placeholder '${{{name}}}'. Supported: $gitRoot, $solutionRoot, $env:NAME.");
        });
    }

    /// <summary>
    /// Expands placeholders then resolves the path. For plain relative paths (no placeholder,
    /// not rooted), tries CWD → solutionRoot → gitRoot and returns the first match that exists.
    /// If none exists, returns the CWD-relative candidate so the caller can produce a clear error.
    /// </summary>
    public static string ResolveFilePath(string value, string? baseDir = null)
    {
        baseDir ??= Directory.GetCurrentDirectory();
        bool hasPlaceholder = s_varPattern.IsMatch(value);
        var expanded = hasPlaceholder ? Expand(value, baseDir) : value;

        if (Path.IsPathRooted(expanded))
            return expanded;
        if (hasPlaceholder)
            return Path.GetFullPath(Path.Combine(baseDir, expanded));

        var candidates = new List<string> { Path.GetFullPath(Path.Combine(baseDir, expanded)) };

        var sln = FindSolutionRoot(baseDir);
        if (sln is not null)
            candidates.Add(Path.GetFullPath(Path.Combine(sln, expanded)));

        var git = FindGitRoot(baseDir);
        if (git is not null)
            candidates.Add(Path.GetFullPath(Path.Combine(git, expanded)));

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        return candidates[0];
    }

    private static string? FindGitRoot(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, ".git"))) // worktree link file
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static string? FindSolutionRoot(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir is not null)
        {
            if (dir.EnumerateFiles("*.sln").Any() || dir.EnumerateFiles("*.slnx").Any())
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
