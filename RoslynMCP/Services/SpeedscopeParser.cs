using System.Text.Json;

namespace RoslynMCP.Services;

/// <summary>
/// Parses speedscope JSON format produced by dotnet-trace to extract CPU profiling data.
/// Computes self-time and total-time per method from sampled call stacks.
/// </summary>
public static class SpeedscopeParser
{
    public record MethodProfile(
        string Name,
        string Module,
        string FullName,
        double SelfTimeMs,
        double TotalTimeMs,
        double SelfPercent,
        double TotalPercent,
        int SampleCount);

    public record ProfilingResult(
        List<MethodProfile> HotMethods,
        double TotalDurationMs,
        int TotalSamples,
        string? Error,
        // Raw data retained for investigation tools
        string[]? FrameNames = null,
        int[][]? Samples = null,
        double[]? Weights = null);

    /// <summary>
    /// Parses a speedscope JSON file and returns the top-N hottest methods by self-time.
    /// Raw sample data is preserved for follow-up investigation queries.
    /// </summary>
    public static ProfilingResult Parse(string filePath, int maxResults)
    {
        try
        {
            var json = File.ReadAllBytes(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Parse frames
            var framesArray = root.GetProperty("shared").GetProperty("frames");
            var frameNames = new string[framesArray.GetArrayLength()];
            for (int i = 0; i < frameNames.Length; i++)
                frameNames[i] = framesArray[i].GetProperty("name").GetString() ?? $"<frame {i}>";

            // Parse profiles. A "sampled" profile carries stacks and weights directly. What
            // dotnet-trace (TraceEvent) actually exports is one "evented" profile per thread —
            // an openFrame/closeFrame stream — so those are converted: between two consecutive
            // events the open-frame stack was the thread's stack, and the elapsed time is its
            // weight. Threads are concatenated; the aggregation neither knows nor cares which
            // thread a stack ran on.
            var rawSamples = new List<int[]>();
            var rawWeights = new List<double>();
            bool sawProfile = false;

            foreach (var profile in root.GetProperty("profiles").EnumerateArray())
            {
                string? type = profile.GetProperty("type").GetString();
                if (type == "sampled")
                {
                    sawProfile = true;
                    var weightsArray = profile.GetProperty("weights");
                    int wi = 0;
                    var weights = new double[weightsArray.GetArrayLength()];
                    foreach (var w in weightsArray.EnumerateArray())
                        weights[wi++] = w.GetDouble();

                    int si = 0;
                    foreach (var sampleEl in profile.GetProperty("samples").EnumerateArray())
                    {
                        var stack = new int[sampleEl.GetArrayLength()];
                        int fi = 0;
                        foreach (var f in sampleEl.EnumerateArray())
                            stack[fi++] = f.GetInt32();
                        rawSamples.Add(stack);
                        rawWeights.Add(si < weights.Length ? weights[si] : 0);
                        si++;
                    }
                }
                else if (type == "evented")
                {
                    sawProfile = true;
                    ConvertEvented(profile, rawSamples, rawWeights);
                }
            }

            if (!sawProfile)
                return new([], 0, 0, "No sampled CPU profile found in trace data.");

            if (rawSamples.Count == 0)
                return new([], 0, 0, "Profile contains no samples — the application may have exited too quickly.");

            return Aggregate(frameNames, [.. rawSamples], [.. rawWeights], maxResults);
        }
        catch (JsonException ex)
        {
            return new([], 0, 0, $"Failed to parse speedscope JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new([], 0, 0, $"Error parsing profile data: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts one thread's "evented" profile — a time-ordered stream of
    /// <c>{"type":"O"|"C","frame":n,"at":t}</c> events — into stacks with weights: each interval
    /// between consecutive events contributes the then-open stack, weighted by the elapsed time.
    /// An empty stack's interval (the thread idle or off-CPU as the sampler saw it) contributes
    /// nothing.
    /// </summary>
    private static void ConvertEvented(
        JsonElement profile, List<int[]> samples, List<double> weights)
    {
        var stack = new List<int>();
        double prevAt = double.NaN;

        foreach (var evt in profile.GetProperty("events").EnumerateArray())
        {
            double at = evt.GetProperty("at").GetDouble();

            if (!double.IsNaN(prevAt) && at > prevAt && stack.Count > 0)
            {
                samples.Add([.. stack]);
                weights.Add(at - prevAt);
            }

            prevAt = at;

            string? kind = evt.GetProperty("type").GetString();
            if (kind is "O" or "openFrame")
                stack.Add(evt.GetProperty("frame").GetInt32());
            else if (kind is "C" or "closeFrame" && stack.Count > 0)
                stack.RemoveAt(stack.Count - 1);
        }
    }

    /// <summary>
    /// Computes self-time and total-time per frame from raw sampled call stacks and
    /// returns the top-N hottest methods by self-time. Shared by every profile source
    /// that produces stacks-with-weights (speedscope JSON, dotTrace reports).
    /// </summary>
    internal static ProfilingResult Aggregate(
        string[] frameNames, int[][] samples, double[] weights, int maxResults)
    {
        var selfTime = new double[frameNames.Length];
        var totalTime = new double[frameNames.Length];
        var hitCount = new int[frameNames.Length];
        double totalDuration = 0;

        for (int i = 0; i < samples.Length; i++)
        {
            var stack = samples[i];
            double weight = weights[i];
            totalDuration += weight;

            if (stack.Length == 0) continue;

            // Last element in the sample array = top of call stack (leaf/self)
            int leafFrame = stack[^1];
            if (leafFrame >= 0 && leafFrame < frameNames.Length)
            {
                selfTime[leafFrame] += weight;
                hitCount[leafFrame]++;
            }

            // All frames in the stack contribute to total-time
            // Use a HashSet to avoid double-counting recursive calls
            var seen = new HashSet<int>();
            foreach (int frameIdx in stack)
            {
                if (frameIdx >= 0 && frameIdx < frameNames.Length && seen.Add(frameIdx))
                    totalTime[frameIdx] += weight;
            }
        }

        if (totalDuration <= 0)
            return new([], 0, samples.Length, "Profile has zero total duration.");

        // Build method profiles sorted by self-time descending
        var methods = new List<MethodProfile>();
        for (int i = 0; i < frameNames.Length; i++)
        {
            if (selfTime[i] <= 0 && totalTime[i] <= 0)
                continue;

            var (name, module) = SplitMethodName(frameNames[i]);
            methods.Add(new MethodProfile(
                Name: name,
                Module: module,
                FullName: frameNames[i],
                SelfTimeMs: selfTime[i],
                TotalTimeMs: totalTime[i],
                SelfPercent: selfTime[i] / totalDuration * 100,
                TotalPercent: totalTime[i] / totalDuration * 100,
                SampleCount: hitCount[i]));
        }

        methods.Sort((a, b) => b.SelfTimeMs.CompareTo(a.SelfTimeMs));

        var topMethods = methods.Count > maxResults
            ? methods.GetRange(0, maxResults)
            : methods;

        return new ProfilingResult(
            topMethods, totalDuration, samples.Length, Error: null,
            FrameNames: frameNames, Samples: samples, Weights: weights);
    }

    /// <summary>
    /// Splits a fully qualified method name like "Namespace.Type.Method(params)"
    /// into a short name and a module/namespace part.
    /// </summary>
    internal static (string Name, string Module) SplitMethodName(string fullName)
    {
        // Handle generic parameters: strip everything after [ for generic instantiation
        int genericStart = fullName.IndexOf('[');

        // Find the parameter list
        int parenStart = fullName.IndexOf('(');

        // Work on the part before params/generics
        int prefixEnd = parenStart >= 0 ? parenStart :
                        genericStart >= 0 ? genericStart :
                        fullName.Length;

        string prefix = fullName[..prefixEnd];

        // Split on last '.' to get Type.Method
        int lastDot = prefix.LastIndexOf('.');
        if (lastDot < 0)
            return (fullName, "");

        int secondLastDot = prefix.LastIndexOf('.', lastDot - 1);

        string module;
        string shortName;
        if (secondLastDot >= 0)
        {
            module = prefix[..secondLastDot];
            shortName = prefix[(secondLastDot + 1)..];
        }
        else
        {
            module = "";
            shortName = prefix;
        }

        // Append parameter list if present
        if (parenStart >= 0)
            shortName += fullName[parenStart..];

        return (shortName, module);
    }
}
