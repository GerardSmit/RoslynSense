using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Investigation tools for exploring profiling sessions after ProfileTests/ProfileApp.
/// Allows searching methods, finding callers/callees, and tracing hot execution paths.
/// </summary>
[McpServerToolType]
public static class ProfileInvestigateTool
{
    [McpServerTool, Description(
        "List all active profiling sessions and in-flight recordings. Sessions are created by " +
        "ProfileTests/ProfileApp/ProfileProcess/ProfileStop and retained for 30 minutes; " +
        "recordings are started by ProfileStart and still collecting. Returns the IDs used by " +
        "the other profile tools.")]
    public static string ListProfilingSessions(
        IOutputFormatter fmt,
        ProfilingSessionStore store,
        ProfileRecordingStore recordings)
    {
        var sessions = store.ListSessions();
        var active = recordings.All();
        var sb = new StringBuilder();
        fmt.AppendHeader(sb, "Profiling Sessions");

        if (active.Count > 0)
        {
            var recordingColumns = new[] { "Recording ID", "Process", "Elapsed" };
            var recordingRows = active.Select(r => new[]
            {
                r.Id,
                r.Description,
                $"{r.Elapsed.TotalSeconds:F0}s"
            }).ToList();
            fmt.AppendTable(sb, "Active Recordings (stop with ProfileStop)", recordingColumns, recordingRows, active.Count);
        }

        if (sessions.Count == 0)
        {
            fmt.AppendEmpty(sb, active.Count > 0
                ? "No finished sessions yet — ProfileStop an active recording to create one."
                : "No active profiling sessions. Run ProfileTests, ProfileApp, ProfileProcess, or ProfileStart first.");
            return sb.ToString();
        }

        var columns = new[] { "Session ID", "Description", "Captured", "Samples", "Duration" };
        var rows = sessions.Select(s => new[]
        {
            s.Id,
            s.Description,
            s.CapturedAt.ToLocalTime().ToString("HH:mm:ss"),
            s.TotalSamples.ToString(),
            $"{s.DurationMs:F0}ms"
        }).ToList();

        fmt.AppendTable(sb, "Active Sessions", columns, rows, sessions.Count);
        return sb.ToString();
    }

    [McpServerTool, Description(
        "Search for methods in a profiling session by name pattern. " +
        "Supports substring match or regex. Returns matching methods with their CPU time breakdown.")]
    public static string ProfileSearchMethods(
        [Description("Session ID from ProfileTests/ProfileApp output.")]
        string sessionId,
        [Description("Method name pattern to search for (substring or regex, case-insensitive).")]
        string pattern,
        IOutputFormatter fmt,
        ProfilingSessionStore store,
        [Description("Maximum results to return. Default: 20.")]
        int maxResults = 20)
    {
        var session = store.Get(sessionId);
        if (session is null)
            return $"Error: Session '{sessionId}' not found. Use ListProfilingSessions to see active sessions.";

        var matches = store.SearchMethods(session, pattern, maxResults);
        var sb = new StringBuilder();
        fmt.AppendHeader(sb, $"Methods matching '{pattern}'");
        fmt.AppendField(sb, "Session", $"{session.Id} ({session.Description})");
        fmt.AppendField(sb, "Matches", matches.Count);
        fmt.AppendSeparator(sb);

        if (matches.Count == 0)
        {
            fmt.AppendEmpty(sb, $"No methods matching '{pattern}' found in this profile.");
            return sb.ToString();
        }

        var columns = new[] { "#", "Self%", "Total%", "Self(ms)", "Method", "Module" };
        var rows = new List<string[]>();
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            rows.Add([
                (i + 1).ToString(),
                $"{m.SelfPercent:F1}%",
                $"{m.TotalPercent:F1}%",
                $"{m.SelfTimeMs:F1}",
                m.Name,
                m.Module
            ]);
        }

        fmt.AppendTable(sb, "Matching Methods", columns, rows, matches.Count);

        fmt.AppendHints(sb,
            "Use ProfileCalls (direction: callers or callees) to see who calls a method or what it calls",
            "Use ProfileHotPaths to see execution paths through a method");

        return sb.ToString();
    }

    [McpServerTool, Description(
        "Show the direct callers or callees of a method in a profiling session. " +
        "direction='callers' reveals which methods invoke the target and how much CPU time flows through each; " +
        "direction='callees' reveals which methods the target calls and how much CPU time is spent in each.")]
    public static string ProfileCalls(
        [Description("Session ID from ProfileTests/ProfileApp output.")]
        string sessionId,
        [Description("Method name or pattern to look up (substring or regex, case-insensitive).")]
        string methodPattern,
        IOutputFormatter fmt,
        ProfilingSessionStore store,
        [Description("'callers' (default) for who calls the method, 'callees' for what the method calls.")]
        string direction = "callers",
        [Description("Maximum results to return. Default: 20.")]
        int maxResults = 20)
    {
        var session = store.Get(sessionId);
        if (session is null)
            return $"Error: Session '{sessionId}' not found. Use ListProfilingSessions to see active sessions.";

        bool callers = direction.Equals("callers", StringComparison.OrdinalIgnoreCase);
        if (!callers && !direction.Equals("callees", StringComparison.OrdinalIgnoreCase))
            return $"Error: Unknown direction '{direction}'. Use: callers, callees.";

        var calls = callers
            ? store.GetCallers(session, methodPattern, maxResults)
            : store.GetCallees(session, methodPattern, maxResults);
        var sb = new StringBuilder();
        fmt.AppendHeader(sb, $"{(callers ? "Callers" : "Callees")} of '{methodPattern}'");
        fmt.AppendField(sb, "Session", $"{session.Id} ({session.Description})");
        fmt.AppendSeparator(sb);

        if (calls.Count == 0)
        {
            fmt.AppendEmpty(sb, callers
                ? $"No callers found for '{methodPattern}'. The method may be a root frame or not present in the profile."
                : $"No callees found for '{methodPattern}'. The method may be a leaf frame or not present in the profile.");
            return sb.ToString();
        }

        var columns = new[] { "#", "Time%", "Time(ms)", "Samples", callers ? "Caller" : "Callee", "Module" };
        var rows = new List<string[]>();
        for (int i = 0; i < calls.Count; i++)
        {
            var c = calls[i];
            rows.Add([
                (i + 1).ToString(),
                $"{c.Percent:F1}%",
                $"{c.TimeMs:F1}",
                c.SampleCount.ToString(),
                c.Name,
                c.Module
            ]);
        }

        fmt.AppendTable(sb, callers ? "Direct Callers" : "Direct Callees", columns, rows, calls.Count);

        fmt.AppendHints(sb,
            callers
                ? "Time% shows what fraction of total profile time flows through this caller into the target method"
                : "Time% shows what fraction of total profile time is spent in this callee when called by the target",
            callers
                ? "Use ProfileCalls with direction='callees' to see what the target method calls"
                : "Use ProfileCalls with direction='callers' on a callee to trace further down the hot path",
            "Use GoToDefinition to navigate to the source code");

        return sb.ToString();
    }

    [McpServerTool, Description(
        "Show the hottest execution paths through a method in a profiling session. " +
        "Displays the call chain from callers down to the target method, ranked by CPU time.")]
    public static string ProfileHotPaths(
        [Description("Session ID from ProfileTests/ProfileApp output.")]
        string sessionId,
        [Description("Method name or pattern to trace hot paths for (substring or regex, case-insensitive).")]
        string methodPattern,
        IOutputFormatter fmt,
        ProfilingSessionStore store,
        [Description("Maximum paths to return. Default: 10.")]
        int maxResults = 10)
    {
        var session = store.Get(sessionId);
        if (session is null)
            return $"Error: Session '{sessionId}' not found. Use ListProfilingSessions to see active sessions.";

        var paths = store.GetHotPaths(session, methodPattern, maxResults);
        var sb = new StringBuilder();
        fmt.AppendHeader(sb, $"Hot Paths through '{methodPattern}'");
        fmt.AppendField(sb, "Session", $"{session.Id} ({session.Description})");
        fmt.AppendSeparator(sb);

        if (paths.Count == 0)
        {
            fmt.AppendEmpty(sb, $"No execution paths found for '{methodPattern}'.");
            return sb.ToString();
        }

        for (int i = 0; i < paths.Count; i++)
        {
            var (path, timeMs, percent) = paths[i];
            sb.AppendLine($"**Path {i + 1}** ({percent:F1}%, {timeMs:F1}ms):");
            sb.AppendLine($"  {string.Join(" → ", path)}");
            sb.AppendLine();
        }

        fmt.AppendHints(sb,
            "Paths are shown from caller → ... → target method (up to 6 frames deep)",
            "Higher Time% means more CPU time flows through this specific call chain",
            "Use ProfileCalls to explore individual methods in the chain");

        return sb.ToString();
    }
}
