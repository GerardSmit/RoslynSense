using System.Collections.Concurrent;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Services;

/// <summary>
/// Process-wide registry of documents currently open in an editor (LSP didOpen/didChange),
/// keyed by normalized file path. <see cref="WorkspaceService"/> overlays these texts onto
/// every project snapshot it hands out, so MCP tools and LSP requests both see unsaved
/// editor buffers — the shared-view core of the LSP integration.
/// Static to match <see cref="WorkspaceService"/>'s shape: reachable from static tool code
/// and shared across every LSP session in the daemon process.
/// </summary>
public static class OpenDocumentStore
{
    public sealed class OpenDocument
    {
        public required SourceText Text { get; set; }
        public int Version { get; set; }
        /// <summary>LSP session ids that have this document open (two editor windows on the
        /// same solution each own the entry; it dies only when the last one closes it).</summary>
        public HashSet<string> OwnerSessions { get; } = new(StringComparer.Ordinal);
    }

    private static readonly ConcurrentDictionary<string, OpenDocument> s_docs =
        new(StringComparer.OrdinalIgnoreCase);

    private static long s_generation;
    private static long s_overlayGeneration;

    /// <summary>Bumped on every open/change/close of any buffer, whatever its language.</summary>
    public static long Generation => Interlocked.Read(ref s_generation);

    /// <summary>
    /// Bumped only when a buffer moved that a Roslyn snapshot can actually carry — see
    /// <see cref="IsOverlayable"/>. This is what
    /// <see cref="WorkspaceService.ApplyOpenDocumentOverlay"/> memoizes against.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Generation"/> because the overlay is the expensive consumer and a
    /// markup buffer can never change its outcome. Opening one <c>.ascx</c> used to fork a fresh
    /// <c>Solution</c> — and therefore a fresh <c>Compilation</c> for every project holding an open
    /// <c>.cs</c> — which invalidated every cached markup parse in the project, moved every
    /// document's dependent semantic version, and so made the next <c>workspace/diagnostic</c> pull
    /// report a whole website as changed. All of that for a file Roslyn does not model.
    /// </remarks>
    public static long OverlayGeneration => Interlocked.Read(ref s_overlayGeneration);

    /// <summary>
    /// Whether a buffer at this path can change what the overlay produces. The overlay only ever
    /// calls <c>Solution.WithDocumentText</c>, which reaches regular documents and nothing else, so
    /// the answer is exactly "is this a file Roslyn compiles".
    /// </summary>
    /// <remarks>
    /// Deliberately an extension test rather than a workspace lookup: this runs inside didOpen and
    /// didChange, on the message loop, before any project is necessarily loaded — and it has to be
    /// answerable for a path the workspace has never heard of. Erring towards <see langword="true"/>
    /// costs a redundant fork, which is what happened unconditionally before.
    /// </remarks>
    private static bool IsOverlayable(string path) =>
        Path.GetExtension(path.AsSpan()) is var ext
        && (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".vb", StringComparison.OrdinalIgnoreCase));

    public static bool IsEmpty => s_docs.IsEmpty;

    public static bool TryGet(string filePath, out SourceText text)
    {
        if (s_docs.TryGetValue(PathHelper.NormalizePath(filePath), out var doc))
        {
            lock (doc) { text = doc.Text; }
            return true;
        }
        text = null!;
        return false;
    }

    /// <summary>Snapshot of all open documents (path → text) for whole-solution overlays.</summary>
    public static List<(string Path, SourceText Text)> SnapshotAll()
    {
        var result = new List<(string, SourceText)>(s_docs.Count);
        foreach (var (path, doc) in s_docs)
        {
            lock (doc) { result.Add((path, doc.Text)); }
        }
        return result;
    }

    public static void Open(string sessionId, string filePath, SourceText text, int version)
    {
        string key = PathHelper.NormalizePath(filePath);
        var doc = s_docs.GetOrAdd(key, _ => new OpenDocument { Text = text, Version = version });
        lock (doc)
        {
            doc.Text = text;
            doc.Version = version;
            doc.OwnerSessions.Add(sessionId);
        }
        Bump(key);
    }

    /// <summary>Applies incremental (or full) changes. Returns the resulting text, or null if
    /// the document is not open (client protocol error — didChange before didOpen).</summary>
    public static SourceText? Change(string filePath, int version, Func<SourceText, SourceText> apply)
    {
        if (!s_docs.TryGetValue(PathHelper.NormalizePath(filePath), out var doc))
            return null;
        SourceText updated;
        lock (doc)
        {
            updated = apply(doc.Text);
            doc.Text = updated;
            doc.Version = version;
        }
        Bump(PathHelper.NormalizePath(filePath));
        return updated;
    }

    public static void Close(string sessionId, string filePath)
    {
        string key = PathHelper.NormalizePath(filePath);
        if (!s_docs.TryGetValue(key, out var doc))
            return;
        bool removed;
        lock (doc)
        {
            doc.OwnerSessions.Remove(sessionId);
            removed = doc.OwnerSessions.Count == 0;
        }
        if (removed)
            s_docs.TryRemove(key, out _);
        Bump(key);
    }

    /// <summary>Drops every document owned by a session that disconnected.</summary>
    public static void CloseSession(string sessionId)
    {
        foreach (var (key, doc) in s_docs)
        {
            bool removed;
            lock (doc)
            {
                doc.OwnerSessions.Remove(sessionId);
                removed = doc.OwnerSessions.Count == 0;
            }
            if (removed)
                s_docs.TryRemove(key, out _);
        }

        // A disconnecting session takes buffers of every kind with it, so both counters move
        // rather than being decided per path.
        Interlocked.Increment(ref s_generation);
        Interlocked.Increment(ref s_overlayGeneration);
    }

    /// <summary>Records that a buffer moved, on both counters or only the general one.</summary>
    private static void Bump(string normalizedPath)
    {
        Interlocked.Increment(ref s_generation);
        if (IsOverlayable(normalizedPath))
            Interlocked.Increment(ref s_overlayGeneration);
    }

    /// <summary>True when the file is open in some editor (its buffer may differ from disk).</summary>
    public static bool IsOpen(string filePath) =>
        s_docs.ContainsKey(PathHelper.NormalizePath(filePath));

    /// <summary>Every open document's path — the scope signal for workspace-wide sweeps.</summary>
    public static IReadOnlyCollection<string> OpenPaths() => s_docs.Keys.ToList();
}
