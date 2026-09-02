using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.Language.Xml;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace RoslynMCP.Services;

/// <summary>
/// Centralizes file-path normalization used by every MCP tool.
/// </summary>
internal static partial class PathHelper
{
    [GeneratedRegex("""Sdk\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex SdkAttributeRegex();

    [GeneratedRegex("""Name\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex SdkNameAttributeRegex();

    [GeneratedRegex(
        @"Project\(""[^""]*""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+\.(?:csproj|vbproj|fsproj))""",
        RegexOptions.IgnoreCase)]
    private static partial Regex SolutionProjectLineRegex();

    /// <summary>
    /// Identity of a file as of a particular read: its length and last-write time. Two reads that
    /// agree on both are treated as reads of the same content.
    /// </summary>
    /// <remarks>
    /// Length as well as timestamp, because a file rewritten inside the filesystem's timestamp
    /// granularity — which a code generator or a fast <c>git checkout</c> genuinely does — changes
    /// length far more often than it does not. Missing files get a distinct sentinel so "not there"
    /// caches too and a repeated miss is not a repeated <c>File.Exists</c> plus a failed open.
    /// </remarks>
    private readonly record struct FileStamp(long Length, long TicksUtc)
    {
        public static readonly FileStamp Missing = new(-1, -1);

        public static FileStamp Of(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : Missing;
            }
            catch
            {
                return Missing;
            }
        }
    }

    /// <summary>
    /// A value derived purely from one file's content, remembered until that file changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parses on this path are small individually and enormous in aggregate. A single cold
    /// project load used to re-read a 34-project <c>.sln</c> several times over and open every
    /// <c>.csproj</c> it lists — once to decide the owning solution, once to pick a restore target,
    /// once per proto consumer scan — and every one of those reads produced the same answer as the
    /// last, because none of the files had changed in between.
    /// </para>
    /// <para>
    /// Keyed on the file's own length and timestamp rather than on a generation counter or an
    /// explicit invalidation call: a <c>.sln</c> or <c>.csproj</c> can be edited by the user, by
    /// <c>git</c>, or by <c>dotnet sln add</c> without anything in this process being told, and a
    /// cache that needed to be told would serve a stale project list until restart.
    /// </para>
    /// </remarks>
    internal static class FileDerived<T>
    {
        private static readonly ConcurrentDictionary<string, (FileStamp Stamp, T Value)> s_cache =
            new(StringComparer.OrdinalIgnoreCase);

        public static T Get(string path, Func<string, T> compute)
        {
            var stamp = FileStamp.Of(path);

            if (s_cache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
                return cached.Value;

            T value = compute(path);
            s_cache[path] = (stamp, value);
            return value;
        }
    }

    /// <summary>
    /// Reads the SDK attribute from a .csproj file (e.g., "Microsoft.NET.Sdk.Web").
    /// Returns null if the file is legacy (non-SDK-style) or cannot be read.
    /// </summary>
    public static string? ReadProjectSdk(string projectPath)
    {
        if (!projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return null;

        return FileDerived<string?>.Get(projectPath, ReadProjectSdkUncached);
    }

    private static string? ReadProjectSdkUncached(string projectPath)
    {
        try
        {
            using var reader = new StreamReader(projectPath);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                line = line.TrimStart();
                if (line.StartsWith("<Project", StringComparison.OrdinalIgnoreCase))
                {
                    var match = SdkAttributeRegex().Match(line);
                    if (match.Success)
                        return match.Groups[1].Value;
                    break;
                }

                // Also check for <Sdk Name="..."/> import style
                if (line.StartsWith("<Sdk", StringComparison.OrdinalIgnoreCase))
                {
                    var match = SdkNameAttributeRegex().Match(line);
                    if (match.Success)
                        return match.Groups[1].Value;
                }
            }
        }
        catch
        {
            // Don't fail if we can't read the project file
        }

        return null;
    }

    /// <summary>
    /// Returns true if a .csproj is a legacy (non-SDK-style) project.
    /// </summary>
    public static bool IsLegacyProject(string csprojPath) =>
        ReadProjectSdk(csprojPath) is null;

    /// <summary>
    /// Returns true if a .sln contains at least one legacy .csproj.
    /// </summary>
    /// <remarks>
    /// Cached against the <c>.sln</c>'s own stamp only, not against the projects it lists. The
    /// answer does depend on those projects, so in principle a project converted from legacy to
    /// SDK-style without touching the <c>.sln</c> goes unnoticed until the solution file changes.
    /// That is the right trade: this opens every project in the solution to compute one boolean,
    /// stamping all of them would cost most of what the cache saves, and the two callers — pick a
    /// restore target, pick MSBuild vs the dotnet CLI — both degrade to "slower but correct" rather
    /// than to a wrong answer.
    /// </remarks>
    public static bool IsLegacySolution(string slnPath)
    {
        if (!IsSolutionFile(slnPath) || !File.Exists(slnPath))
            return false;

        return FileDerived<bool>.Get(slnPath, IsLegacySolutionUncached);
    }

    private static bool IsLegacySolutionUncached(string slnPath)
    {
        var slnDir = Path.GetDirectoryName(slnPath)!;

        try
        {
            if (slnPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                return IsLegacySolutionXml(slnPath, slnDir);

            // Parse Project("{type}") = "Name", "relative\path.csproj", "{GUID}" lines
            foreach (var line in File.ReadLines(slnPath))
            {
                var match = SolutionProjectLineRegex().Match(line);
                if (!match.Success) continue;

                var relativePath = match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(slnDir, relativePath));
                if (File.Exists(fullPath) && IsLegacyProject(fullPath))
                    return true;
            }
        }
        catch
        {
            // Don't fail if we can't read the solution
        }

        return false;
    }

    private static bool IsLegacySolutionXml(string slnxPath, string slnDir)
    {
        var doc = Parser.ParseText(File.ReadAllText(slnxPath));
        var projectElements = doc.Descendants("Project");

        foreach (var proj in projectElements)
        {
            var pathAttr = proj.GetAttributeValue("Path");
            if (string.IsNullOrEmpty(pathAttr)) continue;
            if (!pathAttr.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                !pathAttr.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) &&
                !pathAttr.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
                continue;

            var fullPath = Path.GetFullPath(Path.Combine(slnDir, pathAttr.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(fullPath) && IsLegacyProject(fullPath))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the build target (a .csproj or .sln) requires MSBuild
    /// rather than the dotnet CLI.
    /// </summary>
    public static bool RequiresMsBuild(string buildTarget)
    {
        if (IsSolutionFile(buildTarget))
            return IsLegacySolution(buildTarget);
        if (buildTarget.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return ProjectClassifier.Classify(buildTarget).BuildTool == BuildTool.VisualStudioMsBuild;
        return false;
    }


    /// <summary>
    /// Normalizes a file path by resolving it to a full absolute path.
    /// </summary>
    public static string NormalizePath(string filePath) =>
        Path.GetFullPath(filePath);

    /// <summary>
    /// Returns true if the path ends with .sln or .slnx.
    /// </summary>
    public static bool IsSolutionFile(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds all solution files (.sln and .slnx) in a directory (non-recursive).
    /// </summary>
    public static string[] FindSolutionFiles(string directory)
    {
        // Sorted case-insensitively: callers take the first entry, and the plugin's node
        // drain hook mirrors this ordering — OS enumeration order would diverge from it.
        var sln = Directory.GetFiles(directory, "*.sln").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        var slnx = Directory.GetFiles(directory, "*.slnx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (sln.Length == 0) return slnx;
        if (slnx.Length == 0) return sln;
        return [.. sln, .. slnx];
    }

    /// <summary>
    /// Which directory owns which solution, so that the walk below is paid once per directory
    /// rather than once per question.
    /// </summary>
    /// <remarks>
    /// The walk is two directory enumerations per ancestor level, and the callers ask it per
    /// file: a Search Everywhere keystroke asked it once per document, which on a solution of a
    /// couple of thousand files measured at 2.8s of pure filesystem metadata — the whole of the
    /// search. A directory's nearest solution is a fact about the checkout rather than about the
    /// session, so it is remembered until <see cref="ClearNearestSolutionCache"/> says the layout
    /// moved. Misses are cached too: "nothing above here" costs a walk to the drive root, and it
    /// is the answer for every file outside the tree.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, string?> s_nearestSolution =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forgets the directory-to-solution map — for a solution open or close, and tests.</summary>
    public static void ClearNearestSolutionCache() => s_nearestSolution.Clear();

    /// <summary>
    /// Walks up from a file or directory looking for the nearest .sln.
    /// Returns null if not found.
    /// </summary>
    public static string? FindNearestSolution(string path)
    {
        var normalized = NormalizePath(path);

        // A path the workspace still lists can be gone from disk — a folder deleted under a
        // loaded project. File.Exists is false for such a file, so testing for a directory is
        // what keeps the walk from enumerating the file path itself as one.
        var dir = Directory.Exists(normalized) ? normalized : Path.GetDirectoryName(normalized);

        if (dir is null)
            return null;

        if (s_nearestSolution.TryGetValue(dir, out string? memoized))
            return memoized;

        // Every directory passed on the way up shares the answer found above it: none of them
        // held a solution, or the walk would have stopped there. So one walk fills the whole
        // chain, and the next file in any of those directories asks the filesystem nothing.
        var walked = new List<string>();
        string? solution = null;

        for (string? current = dir; current is not null; current = Path.GetDirectoryName(current))
        {
            if (s_nearestSolution.TryGetValue(current, out string? cached))
            {
                solution = cached;
                break;
            }

            walked.Add(current);

            if (!Directory.Exists(current))
                continue;

            try
            {
                var slnFiles = FindSolutionFiles(current);
                if (slnFiles.Length >= 1)
                {
                    solution = slnFiles[0];
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deleted between the check and the enumeration, or unreadable: an ancestor
                // may still hold the solution.
            }
        }

        foreach (string visited in walked)
            s_nearestSolution[visited] = solution;

        return solution;
    }

    /// <summary>
    /// Parses a .sln or .slnx file and returns the absolute paths of all .csproj projects it references.
    /// Returns an empty list if the file cannot be read or contains no C# projects.
    /// </summary>
    /// <remarks>
    /// Memoized against the solution file's own stamp. This is read on every workspace cache miss,
    /// on every restore-target decision and on every cross-project reference sweep, and it is a
    /// pure function of the file — so the second and every later read of an unchanged <c>.sln</c>
    /// was a full <c>File.ReadAllLines</c> and re-parse for a list the process already had.
    /// The returned list is shared, so callers must not mutate it.
    /// </remarks>
    public static List<string> GetProjectsFromSolution(string solutionPath) =>
        FileDerived<List<string>>.Get(solutionPath, GetProjectsFromSolutionUncached);

    private static List<string> GetProjectsFromSolutionUncached(string solutionPath)
    {
        var slnDir = Path.GetDirectoryName(solutionPath)!;
        var result = new List<string>();
        try
        {
            if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                var doc = Parser.ParseText(File.ReadAllText(solutionPath));
                foreach (var elem in doc.Descendants("Project"))
                {
                    var rel = elem.GetAttributeValue("Path");
                    if (!string.IsNullOrEmpty(rel) && rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                        result.Add(Path.GetFullPath(Path.Combine(slnDir, rel.Replace('/', Path.DirectorySeparatorChar))));
                }
            }
            else
            {
                foreach (var line in File.ReadAllLines(solutionPath))
                {
                    if (!line.StartsWith("Project(", StringComparison.Ordinal)) continue;
                    var parts = line.Split('"');
                    if (parts.Length < 6) continue;
                    var rel = parts[5];
                    if (rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                        result.Add(Path.GetFullPath(Path.Combine(slnDir, rel.Replace('\\', Path.DirectorySeparatorChar))));
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Resolves a project path, file path, or directory to the containing .csproj file.
    /// Walks up directories from source files. Returns null if not found.
    /// </summary>
    public static string? ResolveCsprojPath(string projectPath)
    {
        var normalized = NormalizePath(projectPath);

        if (normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(normalized))
            return normalized;

        if (File.Exists(normalized))
        {
            var dir = Path.GetDirectoryName(normalized);
            while (dir is not null)
            {
                var csprojs = Directory.GetFiles(dir, "*.csproj");
                if (csprojs.Length >= 1) return csprojs[0];
                dir = Path.GetDirectoryName(dir);
            }
        }

        if (Directory.Exists(normalized))
        {
            var csprojs = Directory.GetFiles(normalized, "*.csproj");
            if (csprojs.Length >= 1) return csprojs[0];
        }

        return null;
    }

    /// <summary>
    /// Returns true if the path points to a C# source file (not a .csproj/.sln/directory).
    /// </summary>
    public static bool IsSourceFile(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts top-level class/struct/record names from a .cs file using simple text scanning.
    /// Returns empty list if the file cannot be read or has no type declarations.
    /// </summary>
    public static List<string> ExtractTypeNames(string csFilePath)
    {
        var results = new List<string>();
        if (!File.Exists(csFilePath)) return results;

        try
        {
            var regex = new System.Text.RegularExpressions.Regex(
                @"(?:^|\s)(?:public|internal|private|protected|static|sealed|abstract|partial|\s)*\s*(?:class|struct|record)\s+(\w+)",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            var content = File.ReadAllText(csFilePath);
            foreach (System.Text.RegularExpressions.Match match in regex.Matches(content))
            {
                var name = match.Groups[1].Value;
                if (!results.Contains(name))
                    results.Add(name);
            }
        }
        catch
        {
            // Don't fail if we can't read the file
        }

        return results;
    }

    /// <summary>
    /// Builds a dotnet test filter expression scoping to the types in a source file.
    /// Combines with an existing filter using &amp;. Returns the original filter if
    /// type names cannot be extracted.
    /// </summary>
    public static string? BuildSourceFileFilter(string csFilePath, string? existingFilter)
    {
        var typeNames = ExtractTypeNames(csFilePath);
        if (typeNames.Count == 0) return existingFilter;

        var classFilter = typeNames.Count == 1
            ? $"FullyQualifiedName~.{typeNames[0]}."
            : $"({string.Join(" | ", typeNames.Select(t => $"FullyQualifiedName~.{t}."))})";

        if (string.IsNullOrWhiteSpace(existingFilter))
            return classFilter;

        return $"({existingFilter}) & {classFilter}";
    }

    /// <summary>
    /// Builds a VSTest /TestCaseFilter expression scoping to the types in a source file.
    /// </summary>
    public static string? BuildSourceFileVsTestFilter(string csFilePath, string? existingFilter)
    {
        var typeNames = ExtractTypeNames(csFilePath);
        if (typeNames.Count == 0) return existingFilter;

        var classFilter = typeNames.Count == 1
            ? $"FullyQualifiedName~.{typeNames[0]}."
            : $"({string.Join(" | ", typeNames.Select(t => $"FullyQualifiedName~.{t}."))})";

        if (string.IsNullOrWhiteSpace(existingFilter))
            return classFilter;

        return $"({existingFilter}) & {classFilter}";
    }

    /// <summary>
    /// Parses a severity filter string ("error", "warning", "info", "hidden", "all")
    /// into a DiagnosticSeverity. Returns true if valid; result is null for "all".
    /// </summary>
    public static bool TryParseSeverityFilter(string filter, out DiagnosticSeverity? result)
    {
        switch (filter.ToLowerInvariant())
        {
            case "error": result = DiagnosticSeverity.Error; return true;
            case "warning": result = DiagnosticSeverity.Warning; return true;
            case "info": result = DiagnosticSeverity.Info; return true;
            case "hidden": result = DiagnosticSeverity.Hidden; return true;
            case "all": result = null; return true;
            default: result = null; return false;
        }
    }
}
