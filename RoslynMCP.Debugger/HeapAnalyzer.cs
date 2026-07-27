using System.Text.Json;
using Microsoft.Diagnostics.Runtime;

namespace RoslynMCP.Debugger;

/// <summary>
/// Walks the managed heap of a live process with ClrMD and aggregates statistics. Lives in this
/// shared library because — like ICorDebug — ClrMD cannot inspect across x86/x64, so the same
/// code must run either in the host or inside a bitness-matched worker process.
/// </summary>
/// <remarks>
/// Capture uses <see cref="DataTarget.CreateSnapshotAndAttach"/>, which snapshots the process via
/// PssCaptureSnapshot on Windows so the target keeps running while the heap is walked.
/// </remarks>
public static class HeapAnalyzer
{
    public sealed record HeapTypeStat(string TypeName, long Count, long TotalBytes);

    public sealed record HeapStats(
        long TotalHeapBytes,
        long ObjectCount,
        long Gen0Bytes,
        long Gen1Bytes,
        long Gen2Bytes,
        long LargeObjectBytes,
        long PinnedObjectBytes,
        List<HeapTypeStat> Types);

    public sealed record HeapRootPath(string Root, List<string> Chain);

    /// <summary>Serializer settings shared by the host and the worker's stdout channel.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Aggregates object count and size per type across the whole managed heap.</summary>
    public static HeapStats CaptureStats(int pid, CancellationToken cancellationToken)
    {
        using var dataTarget = AttachTo(pid);
        var runtime = CreateRuntime(dataTarget, pid);
        var heap = runtime.Heap;

        var byType = new Dictionary<string, (long Count, long Bytes)>(StringComparer.Ordinal);
        long objectCount = 0;
        long totalBytes = 0;

        foreach (var obj in heap.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!obj.IsValid)
                continue;

            var typeName = obj.Type?.Name ?? "<unknown>";
            var size = (long)obj.Size;

            objectCount++;
            totalBytes += size;

            byType[typeName] = byType.TryGetValue(typeName, out var stat)
                ? (stat.Count + 1, stat.Bytes + size)
                : (1, size);
        }

        long gen0 = 0, gen1 = 0, gen2 = 0, loh = 0, poh = 0;
        foreach (var segment in heap.Segments)
        {
            switch (segment.Kind)
            {
                case GCSegmentKind.Generation0:
                    gen0 += (long)segment.ObjectRange.Length;
                    break;
                case GCSegmentKind.Generation1:
                    gen1 += (long)segment.ObjectRange.Length;
                    break;
                case GCSegmentKind.Generation2:
                    gen2 += (long)segment.ObjectRange.Length;
                    break;
                case GCSegmentKind.Large:
                    loh += (long)segment.ObjectRange.Length;
                    break;
                case GCSegmentKind.Pinned:
                    poh += (long)segment.ObjectRange.Length;
                    break;
                case GCSegmentKind.Ephemeral:
                    // Workstation GC keeps gen0+gen1 in one ephemeral segment; count it as gen0
                    // rather than inventing a split ClrMD does not report.
                    gen0 += (long)segment.ObjectRange.Length;
                    break;
            }
        }

        return new HeapStats(
            totalBytes, objectCount, gen0, gen1, gen2, loh, poh,
            byType.Select(kv => new HeapTypeStat(kv.Key, kv.Value.Count, kv.Value.Bytes)).ToList());
    }

    /// <summary>
    /// Finds why instances of a type are kept alive: walks GC roots to up to
    /// <paramref name="maxInstances"/> instances whose type name contains
    /// <paramref name="typeNameSubstring"/>.
    /// </summary>
    public static List<HeapRootPath> FindPathsToRoot(
        int pid, string typeNameSubstring, int maxInstances, CancellationToken cancellationToken)
    {
        using var dataTarget = AttachTo(pid);
        var runtime = CreateRuntime(dataTarget, pid);
        var heap = runtime.Heap;

        var targets = new List<ulong>();
        foreach (var obj in heap.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!obj.IsValid || obj.Type?.Name is not { } name)
                continue;

            if (name.Contains(typeNameSubstring, StringComparison.OrdinalIgnoreCase))
            {
                targets.Add(obj.Address);
                if (targets.Count >= maxInstances)
                    break;
            }
        }

        var paths = new List<HeapRootPath>();
        if (targets.Count == 0)
            return paths;

        var gcroot = new GCRoot(heap, targets);
        foreach (var (source, link) in gcroot.EnumerateRootPaths(cancellationToken))
        {
            var chain = new List<string>();
            for (var node = link; node is not null; node = node.Next)
            {
                var obj = heap.GetObject(node.Object);
                chain.Add(obj.Type?.Name ?? $"<0x{node.Object:x}>");
            }

            paths.Add(new HeapRootPath(DescribeRoot(source), chain));

            // One path per requested instance is enough to explain retention.
            if (paths.Count >= maxInstances)
                break;
        }

        return paths;
    }

    private static DataTarget AttachTo(int pid)
    {
        try
        {
            return DataTarget.CreateSnapshotAndAttach(pid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Could not attach to process {pid}: {ex.Message}", ex);
        }
    }

    private static ClrRuntime CreateRuntime(DataTarget dataTarget, int pid)
    {
        if (dataTarget.ClrVersions.Length == 0)
            throw new InvalidOperationException(
                $"Process {pid} has no CLR loaded — it does not appear to be a .NET process.");

        return dataTarget.ClrVersions[0].CreateRuntime();
    }

    private static string DescribeRoot(ClrRoot root)
    {
        var kind = root.RootKind.ToString();
        var type = root.Object.Type?.Name;
        return type is null ? kind : $"{kind} → {type}";
    }
}
