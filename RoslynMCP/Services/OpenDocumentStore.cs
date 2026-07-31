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

    /// <summary>Bumped on every open/change/close; snapshot overlays are memoized against it.</summary>
    public static long Generation => Interlocked.Read(ref s_generation);

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
        Interlocked.Increment(ref s_generation);
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
        Interlocked.Increment(ref s_generation);
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
        Interlocked.Increment(ref s_generation);
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
        Interlocked.Increment(ref s_generation);
    }

    /// <summary>True when the file is open in some editor (its buffer may differ from disk).</summary>
    public static bool IsOpen(string filePath) =>
        s_docs.ContainsKey(PathHelper.NormalizePath(filePath));

    /// <summary>Every open document's path — the scope signal for workspace-wide sweeps.</summary>
    public static IReadOnlyCollection<string> OpenPaths() => s_docs.Keys.ToList();
}
