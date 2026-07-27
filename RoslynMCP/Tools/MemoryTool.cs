using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Services.Memory;

namespace RoslynMCP.Tools;

/// <summary>
/// Memory tracing for live .NET and .NET Framework processes via ClrMD heap snapshots:
/// capture per-type statistics, compare snapshots to find growth, and trace GC root paths.
/// </summary>
[McpServerToolType]
public static class MemoryTool
{
    [McpServerTool, Description(
        "Take a managed heap snapshot of a running .NET or .NET Framework process (e.g. the PID " +
        "from RunProject) and return the types using the most memory. The snapshot is retained " +
        "for 30 minutes; take a second one after exercising the app and use MemoryCompare to " +
        "see which types grew. The target process keeps running.")]
    public static async Task<string> MemorySnapshot(
        [Description("PID of the process to snapshot (e.g. from RunProject).")]
        int processId,
        IOutputFormatter fmt,
        MemorySnapshotStore store,
        [Description("Optional label for the snapshot (e.g. 'after 100 requests').")]
        string? description = null,
        [Description("Number of top types to return. Default: 30.")]
        int maxResults = 30,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string processName;
            try
            {
                using var process = Process.GetProcessById(processId);
                processName = process.ProcessName;
            }
            catch (ArgumentException)
            {
                return $"Error: No process with PID {processId} is running.";
            }

            var label = string.IsNullOrWhiteSpace(description)
                ? $"{processName} (pid {processId})"
                : $"{processName} (pid {processId}) — {description}";

            var snapshot = await MemorySnapshotService.CaptureAsync(processId, label, cancellationToken);
            store.Store(snapshot);

            var sb = new StringBuilder();
            fmt.AppendHeader(sb, "Heap Snapshot");
            fmt.AppendField(sb, "Snapshot ID", snapshot.Id);
            fmt.AppendField(sb, "Process", label);
            fmt.AppendField(sb, "Total Heap", FormatBytes(snapshot.TotalHeapBytes));
            fmt.AppendField(sb, "Objects", snapshot.ObjectCount.ToString("N0"));
            fmt.AppendField(sb, "Gen0/Gen1/Gen2",
                $"{FormatBytes(snapshot.Gen0Bytes)} / {FormatBytes(snapshot.Gen1Bytes)} / {FormatBytes(snapshot.Gen2Bytes)}");
            fmt.AppendField(sb, "LOH / Pinned",
                $"{FormatBytes(snapshot.LargeObjectBytes)} / {FormatBytes(snapshot.PinnedObjectBytes)}");
            fmt.AppendSeparator(sb);

            var topTypes = snapshot.ByType.Values
                .OrderByDescending(t => t.TotalBytes)
                .Take(maxResults)
                .ToList();

            var columns = new[] { "#", "Bytes", "Count", "Type" };
            var rows = new List<string[]>();
            for (int i = 0; i < topTypes.Count; i++)
            {
                var t = topTypes[i];
                rows.Add([
                    (i + 1).ToString(),
                    FormatBytes(t.TotalBytes),
                    t.Count.ToString("N0"),
                    t.TypeName
                ]);
            }

            fmt.AppendTable(sb, "Top Types by Retained Bytes", columns, rows, topTypes.Count);

            fmt.AppendHints(sb,
                $"Take another snapshot after exercising the app, then MemoryCompare '{snapshot.Id}' with it to see growth",
                "Use MemoryPathsToRoot to find why instances of a suspicious type are kept alive",
                "System.String and arrays dominating is normal; look for your own types and unexpected counts");

            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "List heap snapshots taken with MemorySnapshot in the last 30 minutes.")]
    public static string ListMemorySnapshots(
        IOutputFormatter fmt,
        MemorySnapshotStore store)
    {
        var snapshots = store.List();
        var sb = new StringBuilder();
        fmt.AppendHeader(sb, "Heap Snapshots");

        if (snapshots.Count == 0)
        {
            fmt.AppendEmpty(sb, "No heap snapshots. Use MemorySnapshot with a PID first.");
            return sb.ToString();
        }

        var columns = new[] { "Snapshot ID", "Process", "Captured", "Heap", "Objects" };
        var rows = snapshots.Select(s => new[]
        {
            s.Id,
            s.Description,
            s.CapturedAt.ToLocalTime().ToString("HH:mm:ss"),
            FormatBytes(s.TotalHeapBytes),
            s.ObjectCount.ToString("N0")
        }).ToList();

        fmt.AppendTable(sb, "Snapshots", columns, rows, snapshots.Count);
        return sb.ToString();
    }

    [McpServerTool, Description(
        "Compare two heap snapshots and show which types grew (or shrank) in bytes and instance " +
        "count. This is the primary leak-hunting tool: snapshot, exercise the app, snapshot " +
        "again, compare.")]
    public static string MemoryCompare(
        [Description("Snapshot ID of the baseline (earlier) snapshot.")]
        string baseSnapshotId,
        [Description("Snapshot ID of the target (later) snapshot.")]
        string targetSnapshotId,
        IOutputFormatter fmt,
        MemorySnapshotStore store,
        [Description("Number of types to return. Default: 30.")]
        int maxResults = 30)
    {
        var baseline = store.Get(baseSnapshotId);
        if (baseline is null)
            return $"Error: Snapshot '{baseSnapshotId}' not found. Use ListMemorySnapshots to see available snapshots.";

        var target = store.Get(targetSnapshotId);
        if (target is null)
            return $"Error: Snapshot '{targetSnapshotId}' not found. Use ListMemorySnapshots to see available snapshots.";

        var deltas = MemorySnapshotStore.Diff(baseline, target, maxResults);

        var sb = new StringBuilder();
        fmt.AppendHeader(sb, "Heap Snapshot Comparison");
        fmt.AppendField(sb, "Baseline", $"{baseline.Id} ({baseline.Description}, {FormatBytes(baseline.TotalHeapBytes)})");
        fmt.AppendField(sb, "Target", $"{target.Id} ({target.Description}, {FormatBytes(target.TotalHeapBytes)})");
        fmt.AppendField(sb, "Heap Delta", FormatBytesDelta(target.TotalHeapBytes - baseline.TotalHeapBytes));
        fmt.AppendField(sb, "Object Delta", (target.ObjectCount - baseline.ObjectCount).ToString("+#,0;-#,0;0"));
        fmt.AppendSeparator(sb);

        if (deltas.Count == 0)
        {
            fmt.AppendEmpty(sb, "No per-type differences between the two snapshots.");
            return sb.ToString();
        }

        var columns = new[] { "#", "Bytes Δ", "Count Δ", "Now", "Type" };
        var rows = new List<string[]>();
        for (int i = 0; i < deltas.Count; i++)
        {
            var d = deltas[i];
            rows.Add([
                (i + 1).ToString(),
                FormatBytesDelta(d.BytesDelta),
                d.CountDelta.ToString("+#,0;-#,0;0"),
                FormatBytes(d.TargetBytes),
                d.TypeName
            ]);
        }

        fmt.AppendTable(sb, "Types by Growth", columns, rows, deltas.Count);

        fmt.AppendHints(sb,
            "Types at the top grew the most between the snapshots — leak candidates if growth keeps repeating",
            "Use MemoryPathsToRoot with a growing type to find what keeps its instances alive",
            "Steady growth across repeated compare cycles is the leak signature; one-time growth is often just caching");

        return sb.ToString();
    }

    [McpServerTool, Description(
        "Find why instances of a type are kept in memory: attaches to the process, finds " +
        "instances whose type name contains the given text, and walks the GC root paths that " +
        "keep them alive. Use after MemoryCompare points at a growing type.")]
    public static async Task<string> MemoryPathsToRoot(
        [Description("PID of the process to inspect (e.g. from RunProject).")]
        int processId,
        [Description("Type name (or substring, case-insensitive) to find retention paths for.")]
        string typeName,
        IOutputFormatter fmt,
        [Description("How many instances to trace. Default: 3.")]
        int maxInstances = 3,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return "Error: 'typeName' is required.";

            var paths = await MemorySnapshotService.FindPathsToRootAsync(
                processId, typeName, Math.Max(1, maxInstances), cancellationToken);

            var sb = new StringBuilder();
            fmt.AppendHeader(sb, $"GC Root Paths for '{typeName}'");
            fmt.AppendField(sb, "Process", $"pid {processId}");
            fmt.AppendField(sb, "Paths Found", paths.Count);
            fmt.AppendSeparator(sb);

            if (paths.Count == 0)
            {
                fmt.AppendEmpty(sb,
                    $"No live instances matching '{typeName}' were found, or they are unreachable " +
                    "(pending collection).");
                return sb.ToString();
            }

            for (int i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                sb.AppendLine($"**Path {i + 1}** (root: {path.Root}):");
                sb.AppendLine($"  {string.Join(" → ", path.Chain)}");
                sb.AppendLine();
            }

            fmt.AppendHints(sb,
                "The chain reads from the GC root down to the matched instance",
                "Static fields and event handlers near the root are the usual leak anchors",
                "Use FindUsages on the root-side types to locate the retaining code");

            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    internal static string FormatBytes(long bytes)
    {
        var absolute = Math.Abs(bytes);
        return absolute switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
            >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            >= 1024L => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B",
        };
    }

    private static string FormatBytesDelta(long bytes) =>
        bytes > 0 ? "+" + FormatBytes(bytes) : FormatBytes(bytes);
}
