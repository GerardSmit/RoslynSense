using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Tools;
using WebFormsCore.Nodes;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>
/// One ASPX file as the editor sees it: the buffer text, the project it belongs to, and the
/// markup parse tree built against that project's compilation.
/// </summary>
internal sealed record AspxDocument(
    string FilePath,
    string Text,
    SourceText SourceText,
    Project Project,
    Compilation Compilation,
    AspxParseResult Parse)
{
    public RootNode? Tree => Parse.ParseTree;

    /// <summary>The code-behind class the <c>Inherits</c> directive names, when it resolved.</summary>
    public INamedTypeSymbol? CodeBehind => Parse.ParseTree?.Inherits;
}

/// <summary>
/// Resolves an ASPX-family path to a parsed <see cref="AspxDocument"/>, the way
/// <see cref="LspDocumentResolver"/> resolves a <c>.cs</c> path to a Roslyn document.
/// </summary>
/// <remarks>
/// Parsing is memoized per file against the buffer text and the compilation it was parsed
/// against. Both have to match: an edit to the markup changes the tree, and an edit to the
/// code-behind changes which symbols the same markup binds to. Compilations are snapshots, so
/// reference equality is the correct staleness test.
/// </remarks>
internal static class AspxDocumentService
{
    private sealed record CacheEntry(string Text, Compilation Compilation, AspxDocument Document);

    private static readonly ConcurrentDictionary<string, CacheEntry> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record WebConfigEntry(DateTime WriteTimeUtc, ImmutableArray<KeyValuePair<string, string>> Namespaces);

    private static readonly ConcurrentDictionary<string, WebConfigEntry> s_webConfig =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool IsAspxFile(string filePath) => AspxSourceMappingService.IsAspxFile(filePath);

    /// <summary>
    /// Returns the parsed document, or <c>null</c> when the file is not ASPX-family, does not
    /// belong to a project, or that project has no compilation.
    /// </summary>
    public static async Task<AspxDocument?> GetAsync(string filePath, CancellationToken ct)
    {
        if (!IsAspxFile(filePath))
            return null;

        string path = PathHelper.NormalizePath(filePath);

        string? text = ReadText(path);
        if (text is null)
            return null;

        string? projectPath = await NonCSharpProjectFinder.FindProjectAsync(path, ct);
        if (string.IsNullOrEmpty(projectPath))
            return null;

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, targetFilePath: path, cancellationToken: ct);

        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null)
            return null;

        if (s_cache.TryGetValue(path, out var cached)
            && ReferenceEquals(cached.Compilation, compilation)
            && string.Equals(cached.Text, text, StringComparison.Ordinal))
        {
            return cached.Document;
        }

        string? projectDir = Path.GetDirectoryName(projectPath);
        var namespaces = projectDir is null ? default : WebConfigNamespaces(projectDir);

        var parse = AspxSourceMappingService.Parse(
            path, text, compilation,
            namespaces: namespaces.IsDefaultOrEmpty ? null : namespaces,
            rootDirectory: projectDir);

        var document = new AspxDocument(
            path, text, SourceText.From(text), project, compilation, parse);

        s_cache[path] = new CacheEntry(text, compilation, document);
        return document;
    }

    /// <summary>Open buffer first — an unsaved edit is what the user is looking at.</summary>
    private static string? ReadText(string path)
    {
        if (OpenDocumentStore.TryGet(path, out var open))
            return open.ToString();

        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
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

    /// <summary>
    /// web.config tag-prefix registrations, re-read only when the file's timestamp moves. Every
    /// keystroke in a markup file goes through here, and the parse needs them to bind a prefix.
    /// </summary>
    private static ImmutableArray<KeyValuePair<string, string>> WebConfigNamespaces(string projectDir)
    {
        DateTime writeTime = default;
        foreach (string name in new[] { "web.config", "Web.config" })
        {
            var info = new FileInfo(Path.Combine(projectDir, name));
            if (info.Exists)
            {
                writeTime = info.LastWriteTimeUtc;
                break;
            }
        }

        if (s_webConfig.TryGetValue(projectDir, out var entry) && entry.WriteTimeUtc == writeTime)
            return entry.Namespaces;

        var namespaces = AspxSourceMappingService.LoadWebConfigNamespaces(projectDir);
        s_webConfig[projectDir] = new WebConfigEntry(writeTime, namespaces);
        return namespaces;
    }

    /// <summary>Drops a file's memoized parse — used when the file changes on disk under us.</summary>
    public static void Invalidate(string filePath) =>
        s_cache.TryRemove(PathHelper.NormalizePath(filePath), out _);

    /// <summary>
    /// Drops every memoized parse. For a <c>web.config</c> change, which is the one edit that
    /// changes how markup binds without changing any markup: the entries are keyed on the file's
    /// own text and its compilation, neither of which moves when a tag prefix is registered, so
    /// every already-parsed page would keep reporting the control it now knows about as unknown.
    /// </summary>
    public static void InvalidateAll()
    {
        s_cache.Clear();
        s_webConfig.Clear();
    }
}
