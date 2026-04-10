using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace RoslynMCP.Services;

/// <summary>
/// In-memory store for profiling sessions. Retains raw sample data so investigation
/// tools can compute callers, callees, and search methods after profiling completes.
/// Sessions auto-expire after 30 minutes of inactivity.
/// </summary>
public sealed class ProfilingSessionStore
{
    public record ProfilingSession(
        string Id,
        string Description,
        DateTime CapturedAt,
        string[] FrameNames,
        int[][] Samples,
        double[] Weights,
        double TotalDurationMs,
        int TotalSamples,
        List<SpeedscopeParser.MethodProfile> AllMethods);

    public record CallerCalleeEntry(
        string Name,
        string Module,
        string FullName,
        double TimeMs,
        double Percent,
        int SampleCount);

    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, (ProfilingSession Session, DateTime LastAccessed)> _sessions = new();

    /// <summary>Stores a profiling result as a named session, returns the session ID.</summary>
    public string Store(string description, SpeedscopeParser.ProfilingResult result)
    {
        EvictExpired();

        var id = $"profile-{DateTime.UtcNow:HHmmss}-{Guid.NewGuid().ToString()[..4]}";

        // Build full method list (not truncated) from raw data
        var allMethods = BuildAllMethods(result.FrameNames!, result.Samples!, result.Weights!, result.TotalDurationMs);

        var session = new ProfilingSession(
            id, description, DateTime.UtcNow,
            result.FrameNames!, result.Samples!, result.Weights!,
            result.TotalDurationMs, result.TotalSamples,
            allMethods);

        _sessions[id] = (session, DateTime.UtcNow);
        return id;
    }

    /// <summary>Gets a session by ID, refreshing its expiry.</summary>
    public ProfilingSession? Get(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            _sessions[sessionId] = (entry.Session, DateTime.UtcNow);
            return entry.Session;
        }
        return null;
    }

    /// <summary>Lists all active sessions.</summary>
    public IReadOnlyList<(string Id, string Description, DateTime CapturedAt, int TotalSamples, double DurationMs)> ListSessions()
    {
        EvictExpired();
        return _sessions.Values
            .OrderByDescending(e => e.Session.CapturedAt)
            .Select(e => (e.Session.Id, e.Session.Description, e.Session.CapturedAt, e.Session.TotalSamples, e.Session.TotalDurationMs))
            .ToList();
    }

    /// <summary>
    /// Searches methods in a session by name pattern (case-insensitive substring or regex).
    /// </summary>
    public List<SpeedscopeParser.MethodProfile> SearchMethods(ProfilingSession session, string pattern, int maxResults)
    {
        // Try regex first, fall back to substring match
        Regex? regex = null;
        try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)); }
        catch { }

        var matches = new List<SpeedscopeParser.MethodProfile>();
        foreach (var m in session.AllMethods)
        {
            bool hit = regex is not null
                ? regex.IsMatch(m.FullName) || regex.IsMatch(m.Name)
                : m.FullName.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                  || m.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase);

            if (hit) matches.Add(m);
            if (matches.Count >= maxResults) break;
        }

        return matches;
    }

    /// <summary>
    /// Finds the direct callers of a method — methods that appear immediately before it in call stacks.
    /// </summary>
    public List<CallerCalleeEntry> GetCallers(ProfilingSession session, string methodPattern, int maxResults)
    {
        var frameIndices = FindFrameIndices(session, methodPattern);
        if (frameIndices.Count == 0) return [];

        var callerTime = new Dictionary<int, double>();
        var callerCount = new Dictionary<int, int>();

        for (int i = 0; i < session.Samples.Length; i++)
        {
            var stack = session.Samples[i];
            double weight = session.Weights[i];

            for (int j = 1; j < stack.Length; j++)
            {
                if (frameIndices.Contains(stack[j]))
                {
                    int callerIdx = stack[j - 1];
                    callerTime[callerIdx] = callerTime.GetValueOrDefault(callerIdx) + weight;
                    callerCount[callerIdx] = callerCount.GetValueOrDefault(callerIdx) + 1;
                }
            }
        }

        return BuildEntries(session, callerTime, callerCount, maxResults);
    }

    /// <summary>
    /// Finds the direct callees of a method — methods that appear immediately after it in call stacks.
    /// </summary>
    public List<CallerCalleeEntry> GetCallees(ProfilingSession session, string methodPattern, int maxResults)
    {
        var frameIndices = FindFrameIndices(session, methodPattern);
        if (frameIndices.Count == 0) return [];

        var calleeTime = new Dictionary<int, double>();
        var calleeCount = new Dictionary<int, int>();

        for (int i = 0; i < session.Samples.Length; i++)
        {
            var stack = session.Samples[i];
            double weight = session.Weights[i];

            for (int j = 0; j < stack.Length - 1; j++)
            {
                if (frameIndices.Contains(stack[j]))
                {
                    int calleeIdx = stack[j + 1];
                    calleeTime[calleeIdx] = calleeTime.GetValueOrDefault(calleeIdx) + weight;
                    calleeCount[calleeIdx] = calleeCount.GetValueOrDefault(calleeIdx) + 1;
                }
            }
        }

        return BuildEntries(session, calleeTime, calleeCount, maxResults);
    }

    /// <summary>
    /// Gets the hottest unique call paths through a method, showing how execution reaches it.
    /// </summary>
    public List<(string[] Path, double TimeMs, double Percent)> GetHotPaths(
        ProfilingSession session, string methodPattern, int maxResults)
    {
        var frameIndices = FindFrameIndices(session, methodPattern);
        if (frameIndices.Count == 0) return [];

        // Aggregate paths by their string representation
        var pathTimes = new Dictionary<string, (string[] Path, double TimeMs)>();

        for (int i = 0; i < session.Samples.Length; i++)
        {
            var stack = session.Samples[i];
            double weight = session.Weights[i];

            // Find the target method in this stack
            int targetPos = -1;
            for (int j = stack.Length - 1; j >= 0; j--)
            {
                if (frameIndices.Contains(stack[j]))
                {
                    targetPos = j;
                    break;
                }
            }

            if (targetPos < 0) continue;

            // Extract path from root to the target method (inclusive), simplify long paths
            int startPos = Math.Max(0, targetPos - 5);
            var pathFrames = new string[targetPos - startPos + 1];
            for (int j = startPos; j <= targetPos; j++)
            {
                int idx = stack[j];
                if (idx >= 0 && idx < session.FrameNames.Length)
                {
                    var (name, _) = SpeedscopeParser.SplitMethodName(session.FrameNames[idx]);
                    pathFrames[j - startPos] = name;
                }
                else
                {
                    pathFrames[j - startPos] = $"<frame {idx}>";
                }
            }

            // Prefix with ... if we truncated
            if (startPos > 0)
                pathFrames[0] = "... → " + pathFrames[0];

            var key = string.Join(" → ", pathFrames);
            if (pathTimes.TryGetValue(key, out var existing))
                pathTimes[key] = (existing.Path, existing.TimeMs + weight);
            else
                pathTimes[key] = (pathFrames, weight);
        }

        return pathTimes.Values
            .OrderByDescending(p => p.TimeMs)
            .Take(maxResults)
            .Select(p => (p.Path, p.TimeMs, p.TimeMs / session.TotalDurationMs * 100))
            .ToList();
    }

    private HashSet<int> FindFrameIndices(ProfilingSession session, string pattern)
    {
        var indices = new HashSet<int>();
        Regex? regex = null;
        try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)); }
        catch { }

        for (int i = 0; i < session.FrameNames.Length; i++)
        {
            bool match = regex is not null
                ? regex.IsMatch(session.FrameNames[i])
                : session.FrameNames[i].Contains(pattern, StringComparison.OrdinalIgnoreCase);
            if (match)
                indices.Add(i);
        }
        return indices;
    }

    private static List<CallerCalleeEntry> BuildEntries(
        ProfilingSession session,
        Dictionary<int, double> timesById,
        Dictionary<int, int> countsById,
        int maxResults)
    {
        return timesById
            .OrderByDescending(kv => kv.Value)
            .Take(maxResults)
            .Select(kv =>
            {
                int idx = kv.Key;
                string fullName = idx >= 0 && idx < session.FrameNames.Length
                    ? session.FrameNames[idx] : $"<frame {idx}>";
                var (name, module) = SpeedscopeParser.SplitMethodName(fullName);
                return new CallerCalleeEntry(
                    name, module, fullName,
                    kv.Value,
                    kv.Value / session.TotalDurationMs * 100,
                    countsById.GetValueOrDefault(idx));
            })
            .ToList();
    }

    private static List<SpeedscopeParser.MethodProfile> BuildAllMethods(
        string[] frameNames, int[][] samples, double[] weights, double totalDuration)
    {
        var selfTime = new double[frameNames.Length];
        var totalTime = new double[frameNames.Length];
        var hitCount = new int[frameNames.Length];

        for (int i = 0; i < samples.Length; i++)
        {
            var stack = samples[i];
            double weight = weights[i];
            if (stack.Length == 0) continue;

            int leaf = stack[^1];
            if (leaf >= 0 && leaf < frameNames.Length)
            {
                selfTime[leaf] += weight;
                hitCount[leaf]++;
            }

            var seen = new HashSet<int>();
            foreach (int idx in stack)
            {
                if (idx >= 0 && idx < frameNames.Length && seen.Add(idx))
                    totalTime[idx] += weight;
            }
        }

        var methods = new List<SpeedscopeParser.MethodProfile>();
        for (int i = 0; i < frameNames.Length; i++)
        {
            if (selfTime[i] <= 0 && totalTime[i] <= 0) continue;
            var (name, module) = SpeedscopeParser.SplitMethodName(frameNames[i]);
            methods.Add(new SpeedscopeParser.MethodProfile(
                name, module, frameNames[i],
                selfTime[i], totalTime[i],
                totalDuration > 0 ? selfTime[i] / totalDuration * 100 : 0,
                totalDuration > 0 ? totalTime[i] / totalDuration * 100 : 0,
                hitCount[i]));
        }

        methods.Sort((a, b) => b.SelfTimeMs.CompareTo(a.SelfTimeMs));
        return methods;
    }

    private void EvictExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _sessions.Keys)
        {
            if (_sessions.TryGetValue(key, out var entry) && now - entry.LastAccessed > SessionTtl)
                _sessions.TryRemove(key, out _);
        }
    }
}
