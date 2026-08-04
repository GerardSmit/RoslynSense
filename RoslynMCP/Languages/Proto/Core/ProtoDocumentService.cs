using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using RoslynMCP.Tools;

namespace RoslynMCP.Languages.Proto.Core;

/// <summary>
/// One <c>.proto</c> file as the editor sees it: the parse of the buffer, and the project that
/// compiles it when there is one.
/// </summary>
/// <remarks>
/// <see cref="Project"/> is nullable where <c>AspxDocument</c>'s is not, because a <c>.proto</c>
/// means something without one. Its outline, its folding, its diagnostics and every name it
/// resolves come from the file and its imports alone; only binding a declaration to the C# protoc
/// generated for it needs the workspace. Refusing to produce a document for a <c>.proto</c> sitting
/// outside any project would take all of that away to protect the one feature that cannot work.
/// </remarks>
internal sealed record ProtoDocument(ProtoFile Parse, Project? Project)
{
    /// <summary>The normalized path, taken from the parse so the two cannot disagree.</summary>
    public string FilePath => Parse.FilePath;

    public SourceText Text => Parse.Text;

    /// <summary>The proto root Grpc.Tools gives files inside the project, which is what an
    /// <c>import</c> in this file is resolved against.</summary>
    public string? ProjectDirectory =>
        Project?.FilePath is { } path ? Path.GetDirectoryName(path) : null;

    /// <summary>
    /// Builds the name-resolution scope for this file.
    /// </summary>
    /// <remarks>
    /// Built on demand rather than cached on the document: it depends on every file in the import
    /// graph, so it would have to be invalidated when any of them changed, and the parses it is
    /// assembled from are already shared through <see cref="ProtoDocumentService"/> — what is left
    /// is a handful of path probes.
    /// </remarks>
    public ProtoScope CreateScope() => ProtoScope.Create(Parse, ProjectDirectory);
}

/// <summary>
/// Resolves a <c>.proto</c> path to a parsed <see cref="ProtoDocument"/>, the way
/// <c>AspxDocumentService</c> resolves an ASPX path to a parsed page.
/// </summary>
/// <remarks>
/// <para>
/// Parsing is memoized per file against the buffer text alone, which is the one real difference
/// from the ASPX cache: an ASPX parse binds markup to code-behind symbols and so has to be
/// invalidated when the compilation moves, while a <c>.proto</c> parse is purely syntactic and the
/// same text always yields the same tree. Nothing a C# edit does can stale an entry here.
/// </para>
/// <para>
/// The key is <see cref="SourceText.GetChecksum"/> rather than the text itself. A
/// <see cref="SourceText"/> memoizes its own checksum, so for a file open in the editor — the case
/// that runs on every keystroke — the comparison is a hash lookup against an array the buffer
/// already computed, where comparing the text would walk the whole file. A buffer and a disk read
/// hashed under different algorithms simply miss and reparse once.
/// </para>
/// </remarks>
internal static class ProtoDocumentService
{
    private sealed record CacheEntry(ImmutableArray<byte> Checksum, ProtoFile File);

    private static readonly ConcurrentDictionary<string, CacheEntry> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Only <c>.proto</c>. Grpc.Tools compiles nothing else, and the pack claims no
    /// companion extension the way the ASPX family does.</summary>
    public static bool IsProtoFile(string? filePath) =>
        filePath is { Length: > 0 } && filePath.EndsWith(".proto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The one spelling of a path the whole pack keys on.
    /// </summary>
    /// <remarks>
    /// Total where <see cref="PathHelper.NormalizePath"/> is not. Paths reach the pack from a
    /// directory walk, from a workspace document and from an <c>.csproj</c> item spec, and a single
    /// malformed one would otherwise throw out of a cache lookup. Returning the path unchanged
    /// simply fails to match anything, which is the right outcome for a path that names nothing.
    /// </remarks>
    public static string Normalize(string filePath)
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

    /// <summary>Whether two paths name the same file, under the one spelling the pack keys on.</summary>
    public static bool PathsEqual(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the parsed document, or <c>null</c> when the file is not a <c>.proto</c> or cannot
    /// be read.
    /// </summary>
    public static async Task<ProtoDocument?> GetAsync(string filePath, CancellationToken ct)
    {
        if (!IsProtoFile(filePath))
            return null;

        string path = Normalize(filePath);

        if (ReadText(path) is not { } text)
            return null;

        var parse = GetParse(path, text);

        // After the parse, not before: everything a document is used for except symbol binding
        // works without a project, so the file is never held hostage to opening one.
        string? projectPath = await NonCSharpProjectFinder.FindProjectAsync(path, ct);

        if (string.IsNullOrEmpty(projectPath))
            return new ProtoDocument(parse, null);

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, targetFilePath: path, cancellationToken: ct);

        return new ProtoDocument(parse, project);
    }

    /// <summary>
    /// The parse alone, with no project attached and no workspace touched.
    /// </summary>
    /// <remarks>
    /// This is how <see cref="ProtoScope"/> reads the files an import graph pulls in. They are
    /// ordinary files that happen to be imported — often from the standard imports directory inside
    /// a NuGet package, which belongs to no project at all — and resolving a project for each one
    /// would turn a name lookup into a workspace load.
    /// </remarks>
    public static ProtoFile? GetParse(string filePath)
    {
        if (!IsProtoFile(filePath))
            return null;

        string path = Normalize(filePath);

        return ReadText(path) is { } text ? GetParse(path, text) : null;
    }

    /// <summary>The parse of text the caller already holds, for a caller that has the buffer and
    /// should not make this re-read it.</summary>
    public static ProtoFile GetParse(string filePath, SourceText text)
    {
        string path = Normalize(filePath);
        var checksum = text.GetChecksum();

        if (s_cache.TryGetValue(path, out var cached)
            && cached.Checksum.AsSpan().SequenceEqual(checksum.AsSpan()))
        {
            return cached.File;
        }

        var parsed = ProtoParser.Parse(path, text);
        s_cache[path] = new CacheEntry(checksum, parsed);
        return parsed;
    }

    /// <summary>Open buffer first — an unsaved edit is what the user is looking at.</summary>
    private static SourceText? ReadText(string path)
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

    /// <summary>Drops a file's memoized parse — used when the file changes on disk under us.</summary>
    public static void Invalidate(string filePath) =>
        s_cache.TryRemove(Normalize(filePath), out _);

    /// <summary>
    /// Drops every memoized parse.
    /// </summary>
    /// <remarks>
    /// Cheaper than it looks and rarely needed: an entry only survives while its text is unchanged,
    /// so this exists for the sweeping cases — a solution closing, a watched-files notification too
    /// coarse to name the file — rather than for any edit.
    /// </remarks>
    public static void InvalidateAll() => s_cache.Clear();
}
