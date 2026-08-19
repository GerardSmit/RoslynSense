using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.WebConfig.Core;

/// <summary>The two sections of a <c>.config</c> file that hold named settings the code reads by
/// name.</summary>
internal enum WebConfigSection
{
    AppSettings,
    ConnectionStrings,
}

/// <summary>One <c>&lt;add&gt;</c> under <c>appSettings</c> or <c>connectionStrings</c>.</summary>
/// <param name="Name">The <c>key</c> of an app setting or the <c>name</c> of a connection string,
/// decoded.</param>
/// <param name="Value">The <c>value</c> or <c>connectionString</c> attribute; null when the entry
/// carries none.</param>
/// <param name="Provider">The <c>providerName</c> of a connection string; null for an app
/// setting.</param>
/// <param name="NameSpan">The naming attribute's value, quotes excluded, or
/// <see langword="default"/> when it could not be located in the text — an entity in the name puts
/// the decoded value and the file out of step, and a range that is merely close is worse than
/// none.</param>
internal readonly record struct WebConfigEntry(
    string Name,
    string? Value,
    string? Provider,
    WebConfigSection Section,
    string FilePath,
    TextSpan NameSpan);

/// <summary>One <c>.config</c> file as the editor sees it: the buffer and the entries read from
/// it.</summary>
internal sealed record WebConfigDocument(
    string FilePath, SourceText Text, ImmutableArray<WebConfigEntry> Entries)
{
    /// <summary>The entry whose name the offset is inside, or null between entries.</summary>
    public WebConfigEntry? EntryAt(int offset)
    {
        foreach (var entry in Entries)
        {
            if (entry.NameSpan != default
                && entry.NameSpan.Start <= offset && offset <= entry.NameSpan.End)
            {
                return entry;
            }
        }

        return null;
    }

    public WebConfigEntry? Find(WebConfigSection section, string name)
    {
        foreach (var entry in Entries)
        {
            if (entry.Section == section
                && entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}

/// <summary>Which <c>.config</c> files carry application settings, by name.</summary>
/// <remarks>
/// Exact names rather than the <c>.config</c> extension: <c>packages.config</c> and
/// <c>nuget.config</c> beside them are NuGet's, and <c>Web.Release.config</c> is an XDT transform
/// whose <c>&lt;add&gt;</c> elements are edits to apply rather than settings that exist.
/// </remarks>
internal static class WebConfigFile
{
    /// <summary>The two the framework itself defines. Always claimed, never configurable.</summary>
    public static readonly ImmutableArray<string> BuiltInNames = ["web.config", "app.config"];

    /// <summary>
    /// The built-in names plus whatever <c>webConfig.additionalFiles</c> added, assigned once
    /// while the packs are being registered.
    /// </summary>
    /// <remarks>
    /// Static, and mutable exactly as long as startup lasts, because the readers below are static
    /// too — the document cache and the watched-file filter have no pack instance to ask. Same
    /// shape and same reason as <c>DotSettingsExclusions.Enabled</c>, which the registration sets
    /// two lines away from this one.
    /// </remarks>
    public static ImmutableArray<string> Names { get; private set; } = BuiltInNames;

    /// <summary>Adds the configured names to the claimed set. Idempotent for a given input.</summary>
    public static void Configure(ImmutableArray<string> additionalFiles) =>
        Names = additionalFiles.IsDefaultOrEmpty
            ? BuiltInNames
            : [.. BuiltInNames, .. additionalFiles.Where(
                name => !BuiltInNames.Contains(name, StringComparer.OrdinalIgnoreCase))];

    public static bool IsConfigPath(string? filePath) =>
        filePath is { Length: > 0 }
        && Names.Contains(Path.GetFileName(filePath), StringComparer.OrdinalIgnoreCase);

    public static bool OwnsFileName(string fileName) =>
        Names.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>The config file in a directory, whichever way it is spelled, or null.</summary>
    /// <remarks>
    /// <para>Both spellings are tried because the file is <c>Web.config</c> in a Visual Studio
    /// project and <c>web.config</c> in most of the world, and only one of them exists.</para>
    /// <para>The built-in names only, deliberately. This is what builds the override chain, and a
    /// chain is a hierarchy of files that share a name and differ by directory. An additional file
    /// is a sibling of <c>web.config</c>, not a nearer version of it, so it answers for itself and
    /// joins nothing.</para>
    /// </remarks>
    public static string? Locate(string directory)
    {
        foreach (string name in new[] { "web.config", "Web.config", "app.config", "App.config" })
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path))
                return path;
        }

        return null;
    }
}

/// <summary>
/// Resolves a <c>.config</c> path to its entries, reusing the previous read while the buffer has
/// not moved.
/// </summary>
/// <remarks>
/// The same shape as <c>AppSettingsDocumentCache</c>: keyed on the text's checksum, so an editor
/// buffer and the file on disk go through one path and a re-read only happens on a real edit.
/// </remarks>
internal static class WebConfigDocumentCache
{
    private sealed record Entry(SourceText Text, ImmutableArray<WebConfigEntry> Entries);

    private static readonly ConcurrentDictionary<string, Entry> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static WebConfigDocument? Get(string filePath)
    {
        if (!WebConfigFile.IsConfigPath(filePath))
            return null;

        string path = Normalize(filePath);
        return Read(path) is { } text ? For(path, text) : null;
    }

    public static WebConfigDocument For(string filePath, SourceText text)
    {
        string path = Normalize(filePath);

        if (s_cache.TryGetValue(path, out var cached)
            && cached.Text.GetChecksum().SequenceEqual(text.GetChecksum()))
        {
            return new WebConfigDocument(path, cached.Text, cached.Entries);
        }

        var entry = new Entry(text, WebConfigReader.Read(text, path));
        s_cache[path] = entry;
        return new WebConfigDocument(path, text, entry.Entries);
    }

    public static void Invalidate(string filePath) => s_cache.TryRemove(Normalize(filePath), out _);

    private static SourceText? Read(string path)
    {
        if (OpenDocumentStore.TryGet(path, out var open))
            return open;

        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = File.OpenRead(path);
            return SourceText.From(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string Normalize(string filePath)
    {
        try
        {
            return PathHelper.NormalizePath(filePath);
        }
        catch (ArgumentException)
        {
            return filePath;
        }
        catch (IOException)
        {
            return filePath;
        }
    }
}
