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

            // Parse profiles — use the first sampled profile
            var profiles = root.GetProperty("profiles");
            JsonElement? sampledProfile = null;
            foreach (var profile in profiles.EnumerateArray())
            {
                if (profile.GetProperty("type").GetString() == "sampled")
                {
                    sampledProfile = profile;
                    break;
                }
            }

            if (sampledProfile is null)
                return new([], 0, 0, "No sampled CPU profile found in trace data.");

            var profileEl = sampledProfile.Value;
            var samplesArray = profileEl.GetProperty("samples");
            var weightsArray = profileEl.GetProperty("weights");
            int sampleCount = samplesArray.GetArrayLength();

            if (sampleCount == 0)
                return new([], 0, 0, "Profile contains no samples — the application may have exited too quickly.");

            // Materialize samples and weights for investigation tools
            var rawSamples = new int[sampleCount][];
            var rawWeights = new double[sampleCount];

            int si = 0;
            foreach (var sampleEl in samplesArray.EnumerateArray())
            {
                var stack = new int[sampleEl.GetArrayLength()];
                int fi = 0;
                foreach (var f in sampleEl.EnumerateArray())
                    stack[fi++] = f.GetInt32();
                rawSamples[si] = stack;
                si++;
            }

            si = 0;
            foreach (var w in weightsArray.EnumerateArray())
                rawWeights[si++] = w.GetDouble();

            // Compute self-time and total-time per frame
            var selfTime = new double[frameNames.Length];
            var totalTime = new double[frameNames.Length];
            var hitCount = new int[frameNames.Length];
            double totalDuration = 0;

            for (int i = 0; i < sampleCount; i++)
            {
                var stack = rawSamples[i];
                double weight = rawWeights[i];
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
                return new([], 0, sampleCount, "Profile has zero total duration.");

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
                topMethods, totalDuration, sampleCount, Error: null,
                FrameNames: frameNames, Samples: rawSamples, Weights: rawWeights);
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
