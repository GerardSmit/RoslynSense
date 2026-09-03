using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.AppSettings.Core;

/// <summary>One configuration JSON file as the editor sees it: the buffer and the keys read
/// from it.</summary>
internal sealed record AppSettingsDocument(
    string FilePath, SourceText Text, ImmutableArray<AppSettingsKey> Keys)
{
    /// <summary>The key whose name the offset is inside, or null between properties.</summary>
    public AppSettingsKey? KeyAt(int offset) =>
        Keys.FirstOrDefault(key =>
            key.NameSpan.Start <= offset && offset <= key.NameSpan.End);

    /// <summary>The innermost key whose value contains the offset — the section an insertion at
    /// the offset would land in.</summary>
    public AppSettingsKey? EnclosingAt(int offset)
    {
        AppSettingsKey? innermost = null;

        foreach (var key in Keys)
        {
            if (key.Kind != AppSettingsValueKind.Object || !key.ValueSpan.Contains(offset))
                continue;

            if (innermost is null || key.Depth > innermost.Depth)
                innermost = key;
        }

        return innermost;
    }

    public AppSettingsKey? Find(string path) =>
        Keys.FirstOrDefault(key =>
            string.Equals(key.Path, path, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Which configuration file a path is, if any: the base file, an environment overlay, or a
/// user-secrets store.
/// </summary>
/// <remarks>
/// All three feed one keyspace at runtime, so the language features treat them as one file split
/// across several — a key in <c>appsettings.Development.json</c> or <c>secrets.json</c> gets the
/// same lens as the same key in the base file.
/// </remarks>
internal static class AppSettingsFile
{
    public const string SecretsFileName = "secrets.json";

    public static bool IsConfigurationPath(string? filePath)
    {
        if (filePath is not { Length: > 0 })
            return false;

        string name = PathHelper.GetFileName(filePath);

        return name.Equals(SecretsFileName, StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSecretsPath(string? filePath) =>
        filePath is { Length: > 0 }
        && PathHelper.GetFileName(filePath).Equals(SecretsFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The environment an overlay file targets — <c>Development</c> for
    /// <c>appsettings.Development.json</c> — or null for the base file and secrets.</summary>
    public static string? Environment(string filePath)
    {
        string name = PathHelper.GetFileName(filePath);

        if (!name.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // "appsettings.json" itself passes both checks above with nothing in between.
        int start = "appsettings.".Length;
        int end = name.Length - ".json".Length;
        return end > start ? name[start..end] : null;
    }
}

/// <summary>
/// Resolves a configuration JSON path to its keys, reusing the previous read while the buffer
/// has not moved.
/// </summary>
/// <remarks>
/// The same shape as <c>DbmlDocumentCache</c> minus the incremental splice: reading keys out of a
/// settings file is a single linear pass over a file that is rarely large, so a full re-read on a
/// real edit costs less than the bookkeeping to avoid it.
/// </remarks>
internal static class AppSettingsDocumentCache
{
    private sealed record Entry(SourceText Text, ImmutableArray<AppSettingsKey> Keys);

    private static readonly ConcurrentDictionary<string, Entry> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static AppSettingsDocument? Get(string filePath)
    {
        if (!AppSettingsFile.IsConfigurationPath(filePath))
            return null;

        string path = Normalize(filePath);
        return Read(path) is { } text ? For(path, text) : null;
    }

    public static AppSettingsDocument For(string filePath, SourceText text)
    {
        string path = Normalize(filePath);

        if (s_cache.TryGetValue(path, out var cached)
            && cached.Text.GetChecksum().SequenceEqual(text.GetChecksum()))
        {
            return new AppSettingsDocument(path, cached.Text, cached.Keys);
        }

        var entry = new Entry(text, AppSettingsReader.Read(text.ToString()));
        s_cache[path] = entry;
        return new AppSettingsDocument(path, text, entry.Keys);
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
