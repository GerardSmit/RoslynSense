using System.Collections.Concurrent;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Routes.Core;

/// <summary>
/// Which projects could serve an HTTP endpoint, answered without evaluating one.
/// </summary>
/// <remarks>
/// <para>
/// The Discovery view promises that listing its sections evaluates no project, so the question
/// "does this solution serve anything" has to be answered without MSBuild, without a restore and
/// without a workspace.
/// </para>
/// <para>
/// Three pieces of evidence, cheapest first, and the second is why this is not just a call to
/// <see cref="ProjectClassifier"/>. Being a web project is the strongest signal there is — it is
/// what the SDK attribute says outright — but controllers are as often in a class library that the
/// web project references, and a library is not a web project by any classification. So a manifest
/// scan for the routing packages runs beside it.
/// </para>
/// <para>
/// The third is the source itself, and it exists because the first two miss the case this pack was
/// written for. A solution with its own routing layer references no web framework anywhere — that
/// is what made the layer in-house — so every manifest in it is silent and the project holding the
/// controllers is invisible to a scan of project files, however generous the word list. Its
/// controllers still say <c>[Route("api/…")]</c> and <c>[HttpGet("")]</c>, because a hand-written
/// routing layer copies the names it is replacing; matching on the written name is the whole idea
/// of this pack, and the probe asks for the same names.
/// </para>
/// <para>
/// Reading source is more I/O than a probe would like, so it runs only for the projects the first
/// two evidences turned down, stops at the first file that says yes, and is cached on the newest
/// write across the files it enumerated — a directory walk per draw, and no reads at all once the
/// answer has settled.
/// </para>
/// </remarks>
internal static class RouteProjectProbe
{
    /// <summary>The manifests worth reading beside the project file itself.</summary>
    private static readonly string[] Siblings = ["packages.config", "Directory.Packages.props"];

    /// <summary>
    /// The words that mean HTTP endpoints, matched case-insensitively.
    /// </summary>
    /// <remarks>
    /// Families rather than exact package ids, the same trade the scheduled-jobs probe makes:
    /// <c>Microsoft.AspNetCore.Mvc.Core</c>, <c>Microsoft.AspNet.WebApi.Core</c> and
    /// <c>Microsoft.AspNetCore.App</c> are all matched by a fragment, and being generous costs a
    /// project row that expands to nothing while being exact costs a whole framework.
    /// </remarks>
    private static readonly string[] Frameworks =
    [
        "AspNetCore",
        "AspNet.WebApi",
        "AspNet.Mvc",
        "System.Web.Http",
        "System.Web.Mvc",
    ];

    /// <summary>Where compiled and vendored output lives, which is nobody’s source.</summary>
    private static readonly string[] Generated = ["bin", "obj", "node_modules", ".git"];

    /// <summary>
    /// How much source one project may be read for before the probe gives up on it.
    /// </summary>
    /// <remarks>
    /// A bound rather than a budget: a project that declares an endpoint says so in a controller,
    /// and a controller is not sixteen megabytes into the enumeration. What this stops is a folder
    /// of generated or vendored code costing a second of reading to produce the answer it was
    /// always going to produce.
    /// </remarks>
    private const long ReadLimit = 16L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, bool Value)> s_cache = new();

    /// <summary>The source verdict, keyed on what the enumeration saw and on the names asked for.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, int Markers, bool Value)>
        s_source = new();

    /// <summary>Whether this project could declare an endpoint.</summary>
    /// <remarks>
    /// Keyed on the newest of every file <see cref="Scan"/> reads, not on the project file alone:
    /// a solution that keeps its versions in <c>Directory.Packages.props</c> writes the reference
    /// there, and a key that watched only the <c>.csproj</c> would answer "no" for the rest of the
    /// session to the edit that added the framework.
    /// </remarks>
    public static bool Serves(string projectPath, IReadOnlyList<string> markers)
    {
        var stamp = Newest(projectPath);

        if (!s_cache.TryGetValue(projectPath, out var cached) || cached.Stamp != stamp)
        {
            cached = (stamp, Scan(projectPath));
            s_cache[projectPath] = cached;
        }

        return cached.Value || Declares(projectPath, markers);
    }

    /// <summary>Whether any source file in the project’s own tree writes one of the markers.</summary>
    private static bool Declares(string projectPath, IReadOnlyList<string> markers)
    {
        if (markers.Count == 0 || Path.GetDirectoryName(projectPath) is not { Length: > 0 } directory)
            return false;

        var files = new List<FileInfo>();
        var stamp = DateTime.MinValue;

        try
        {
            foreach (var file in new DirectoryInfo(directory)
                .EnumerateFiles("*.cs", SearchOption.AllDirectories))
            {
                if (IsGenerated(file, directory))
                    continue;

                files.Add(file);

                if (file.LastWriteTimeUtc > stamp)
                    stamp = file.LastWriteTimeUtc;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        // The enumeration is the key as well as the work: a project that gains its first controller
        // gains a file, and a file that gains an attribute gains a write time. Both move the stamp,
        // so the answer follows the source without anybody having to invalidate anything.
        int hash = Hash(markers);

        if (s_source.TryGetValue(projectPath, out var known)
            && known.Stamp == stamp
            && known.Markers == hash)
        {
            return known.Value;
        }

        bool declares = Read(files, markers);
        s_source[projectPath] = (stamp, hash, declares);
        return declares;
    }

    private static bool Read(List<FileInfo> files, IReadOnlyList<string> markers)
    {
        long budget = ReadLimit;

        // Newest first, because the file somebody is working in is the likeliest reason they opened
        // this view, and because it makes the limit bite on the oldest and most-likely-vendored code
        // rather than on what changed this morning.
        foreach (var file in files.OrderByDescending(file => file.LastWriteTimeUtc))
        {
            if (budget <= 0)
                return false;

            string text;
            try
            {
                text = File.ReadAllText(file.FullName);
            }
            catch
            {
                continue;
            }

            budget -= text.Length;

            foreach (string marker in markers)
            {
                if (text.Contains(marker, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static bool IsGenerated(FileInfo file, string root)
    {
        string? directory = file.DirectoryName;

        while (directory is { Length: > 0 }
            && directory.Length > root.Length
            && !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(directory);

            foreach (string generated in Generated)
            {
                if (string.Equals(name, generated, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    private static int Hash(IReadOnlyList<string> markers)
    {
        var hash = default(HashCode);

        foreach (string marker in markers)
            hash.Add(marker, StringComparer.Ordinal);

        return hash.ToHashCode();
    }

    /// <summary>The most recent write across the project file and the manifests beside it.</summary>
    private static DateTime Newest(string projectPath)
    {
        var newest = Written(projectPath);

        if (Path.GetDirectoryName(projectPath) is { Length: > 0 } directory)
        {
            foreach (string sibling in Siblings)
            {
                var written = Written(Path.Combine(directory, sibling));
                if (written > newest)
                    newest = written;
            }
        }

        return newest;
    }

    private static DateTime Written(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static bool Scan(string projectPath)
    {
        // The classification is already timestamp-cached and already reads the SDK attribute, so
        // asking it first is free and settles most web projects outright.
        if (ProjectClassifier.Classify(projectPath).Kind
            is AppKind.AspNetCore or AppKind.AspNetClassic)
        {
            return true;
        }

        if (Mentions(projectPath))
            return true;

        string? directory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(directory))
            return false;

        foreach (string sibling in Siblings)
        {
            if (Mentions(Path.Combine(directory, sibling)))
                return true;
        }

        return false;
    }

    private static bool Mentions(string path)
    {
        string text;
        try
        {
            // The whole file rather than a line-by-line read: a project file is a few kilobytes,
            // and an unreadable one is a project with nothing to show rather than an error worth
            // reporting — the view is being drawn.
            text = File.ReadAllText(path);
        }
        catch
        {
            return false;
        }

        foreach (string framework in Frameworks)
        {
            if (text.Contains(framework, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
