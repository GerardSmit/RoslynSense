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
        "List all active profiling sessions. Each session is created by ProfileTests or ProfileApp " +
        "and retained for 30 minutes. Returns session IDs for use with other profile investigation tools.")]
    public static string ListProfilingSessions(
        IOutputFormatter fmt,
        ProfilingSessionStore store)
    {
        var sessions = store.ListSessions();
        var sb = new StringBuilder();
        fmt.AppendHeader(sb, "Profiling Sessions");

        if (sessions.Count == 0)
        {
            fmt.AppendEmpty(sb, "No active profiling sessions. Run ProfileTests or ProfileApp first.");
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
            "Use ProfileCallers to see who calls a method",
            "Use ProfileCallees to see what a method calls",
            "Use ProfileHotPaths to see execution paths through a method");

        return sb.ToString();
    }

    [McpServerTool, Description(
        "Show the direct callers of a method in a profiling session. " +
        "Reveals which methods invoke the target and how much CPU time flows through each caller.")]
    public static string ProfileCallers(
        [Description("Session ID from ProfileTests/ProfileApp output.")]
        string sessionId,
        [Description("Method name or pattern to find callers for (substring or regex, case-insensitive).")]
        string methodPattern,
        IOutputFormatter fmt,
        ProfilingSessionStore store,
        [Description("Maximum callers to return. Default: 20.")]
        int maxResults = 20)
    {
        var session = store.Get(sessionId);
        if (session is null)
            return $"Error: Session '{sessionId}' not found. Use ListProfilingSessions to see active sessions.";

        var callers = store.GetCallers(session, methodPattern, maxResults);
        var sb = new StringBuilder();
        fmt.AppendHeader(sb, $"Callers of '{methodPattern}'");
        fmt.AppendField(sb, "Session", $"{session.Id} ({session.Description})");
        fmt.AppendSeparator(sb);

        if (callers.Count == 0)
        {
            fmt.AppendEmpty(sb, $"No callers found for '{methodPattern}'. The method may be a root frame or not present in the profile.");
            return sb.ToString();
        }

        var columns = new[] { "#", "Time%", "Time(ms)", "Samples", "Caller", "Module" };
        var rows = new List<string[]>();
        for (int i = 0; i < callers.Count; i++)
        {
            var c = callers[i];
            rows.Add([
                (i + 1).ToString(),
                $"{c.Percent:F1}%",
                $"{c.TimeMs:F1}",
                c.SampleCount.ToString(),
                c.Name,
                c.Module
            ]);
        }

        fmt.AppendTable(sb, "Direct Callers", columns, rows, callers.Count);

        fmt.AppendHints(sb,
            "Time% shows what fraction of total profile time flows through this caller into the target method",
            "Use ProfileCallees to see what the target method calls",
            "Use GoToDefinition to navigate to a caller's source code");

        return sb.ToString();
    }

    [McpServerTool, Description(
        "Show the direct callees of a method in a profiling session. " +
        "Reveals which methods the target calls and how much CPU time is spent in each callee.")]
    public static string ProfileCallees(
        [Description("Session ID from ProfileTests/ProfileApp output.")]
        string sessionId,
        [Description("Method name or pattern to find callees for (substring or regex, case-insensitive).")]
        string methodPattern,
        IOutputFormatter fmt,
        ProfilingSessionStore store,
        [Description("Maximum callees to return. Default: 20.")]
        int maxResults = 20)
    {
        var session = store.Get(sessionId);
        if (session is null)
            return $"Error: Session '{sessionId}' not found. Use ListProfilingSessions to see active sessions.";

        var callees = store.GetCallees(session, methodPattern, maxResults);
        var sb = new StringBuilder();
        fmt.AppendHeader(sb, $"Callees of '{methodPattern}'");
        fmt.AppendField(sb, "Session", $"{session.Id} ({session.Description})");
        fmt.AppendSeparator(sb);

        if (callees.Count == 0)
        {
            fmt.AppendEmpty(sb, $"No callees found for '{methodPattern}'. The method may be a leaf frame or not present in the profile.");
            return sb.ToString();
        }

        var columns = new[] { "#", "Time%", "Time(ms)", "Samples", "Callee", "Module" };
        var rows = new List<string[]>();
        for (int i = 0; i < callees.Count; i++)
        {
            var c = callees[i];
            rows.Add([
                (i + 1).ToString(),
                $"{c.Percent:F1}%",
                $"{c.TimeMs:F1}",
                c.SampleCount.ToString(),
                c.Name,
                c.Module
            ]);
        }

        fmt.AppendTable(sb, "Direct Callees", columns, rows, callees.Count);

        fmt.AppendHints(sb,
            "Time% shows what fraction of total profile time is spent in this callee when called by the target",
            "Use ProfileCallers on a callee to trace further down the hot path",
            "Use GoToDefinition to navigate to a callee's source code");

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
            "Use ProfileCallers/ProfileCallees to explore individual methods in the chain");

        return sb.ToString();
    }
}
