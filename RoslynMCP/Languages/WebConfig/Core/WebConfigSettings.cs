using System.Collections.Immutable;

namespace RoslynMCP.Languages.WebConfig.Core;

/// <summary>
/// The <c>&lt;appSettings&gt;</c> and <c>&lt;connectionStrings&gt;</c> a file can see: the config
/// files from the application root down to its own directory, nearer overriding further.
/// </summary>
/// <remarks>
/// A deliberately shallow reading of what the runtime does. Machine-level configuration,
/// <c>configSource</c> redirection and build-time transforms are all out — this exists so that
/// <c>&lt;%$ AppSettings: CdnRoot %&gt;</c> hovers to a value instead of to nothing, and so that a
/// key's declaration can be found from the code that reads it.
/// </remarks>
internal static class WebConfigSettings
{
    /// <summary>How deep the search for nested config files goes, and how many it may find.
    /// A web application has a handful; a tree that answers with hundreds is a tree the search
    /// wandered out of.</summary>
    private const int MaxNestedConfigs = 64;

    /// <summary>Directories that hold copies rather than sources.</summary>
    private static readonly string[] s_skipped = ["bin", "obj", "node_modules", "packages", ".git"];

    /// <summary>
    /// The merged view of one section for a file inside the application — nearest declaration
    /// wins, declaration order preserved.
    /// </summary>
    public static ImmutableArray<WebConfigEntry> Merged(
        string filePath, string? projectFilePath, WebConfigSection section)
    {
        var byName = new Dictionary<string, WebConfigEntry>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (string path in Chain(filePath, projectFilePath))
        {
            if (WebConfigDocumentCache.Get(path) is not { } document)
                continue;

            foreach (var entry in document.Entries)
            {
                if (entry.Section != section)
                    continue;

                if (!byName.ContainsKey(entry.Name))
                    order.Add(entry.Name);

                byName[entry.Name] = entry;
            }
        }

        var merged = ImmutableArray.CreateBuilder<WebConfigEntry>(order.Count);

        foreach (string name in order)
            merged.Add(byName[name]);

        return merged.ToImmutable();
    }

    public static WebConfigEntry? Find(ImmutableArray<WebConfigEntry> entries, string name)
    {
        foreach (var entry in entries)
        {
            if (entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return entry;
        }

        return null;
    }

    /// <summary>
    /// The entry a <c>&lt;%$ ConnectionStrings: … %&gt;</c> argument names. The builder accepts a
    /// bare name and a <c>.ProviderName</c> suffix, and the suffixed form asks for a different
    /// field of the same entry rather than for a different entry.
    /// </summary>
    public static (WebConfigEntry Entry, bool Provider)? ConnectionString(
        string filePath, string? projectFilePath, string argument)
    {
        const string providerSuffix = ".ProviderName";

        var entries = Merged(filePath, projectFilePath, WebConfigSection.ConnectionStrings);

        if (Find(entries, argument) is { } direct)
            return (direct, false);

        return argument.EndsWith(providerSuffix, StringComparison.OrdinalIgnoreCase)
            && Find(entries, argument[..^providerSuffix.Length]) is { } named
                ? (named, true)
                : null;
    }

    /// <summary>The config files above a file, application root first so the nearest one is applied
    /// last and wins.</summary>
    public static IEnumerable<string> Chain(string filePath, string? projectFilePath)
    {
        if (Path.GetDirectoryName(projectFilePath) is not { Length: > 0 } root)
        {
            // No project to bound the walk: the file's own directory is all that can be trusted.
            if (Path.GetDirectoryName(filePath) is { Length: > 0 } own
                && WebConfigFile.Locate(own) is { } only)
            {
                yield return only;
            }

            yield break;
        }

        var directories = new List<string>();

        for (string? directory = Path.GetDirectoryName(filePath);
             directory is { Length: > 0 };
             directory = Path.GetDirectoryName(directory))
        {
            directories.Add(directory);

            if (directory.Equals(root, StringComparison.OrdinalIgnoreCase))
                break;
        }

        // A file outside the project directory still gets the application's own settings.
        if (directories.Count == 0
            || !directories[^1].Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            directories.Add(root);
        }

        for (int i = directories.Count - 1; i >= 0; i--)
        {
            if (WebConfigFile.Locate(directories[i]) is { } path)
                yield return path;
        }
    }

    /// <summary>
    /// Every config file feeding a project's settings: the one beside the project file, and the
    /// nested ones a web application puts in its subdirectories. Root first.
    /// </summary>
    public static IReadOnlyList<string> ConfigFilesFor(string projectFilePath)
    {
        if (Path.GetDirectoryName(projectFilePath) is not { Length: > 0 } root
            || !Directory.Exists(root))
        {
            return [];
        }

        var files = new List<string>();

        if (WebConfigFile.Locate(root) is { } own)
            files.Add(own);

        foreach (string directory in Descend(root))
        {
            if (files.Count >= MaxNestedConfigs)
                break;

            if (WebConfigFile.Locate(directory) is { } nested)
                files.Add(nested);
        }

        return files;
    }

    /// <summary>Every directory under a root that could hold a config file of the application's
    /// own, build output and package caches left out.</summary>
    private static IEnumerable<string> Descend(string root)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            string[] children;

            try
            {
                children = Directory.GetDirectories(queue.Dequeue());
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string child in children)
            {
                if (s_skipped.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                    continue;

                yield return child;
                queue.Enqueue(child);
            }
        }
    }
}
