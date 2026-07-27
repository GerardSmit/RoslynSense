using System.Globalization;
using System.Xml;

namespace RoslynMCP.Services;

/// <summary>
/// Parses the XML report produced by dotTrace's Reporter.exe into the same sample/stack
/// model as <see cref="SpeedscopeParser"/>, so .NET Framework profiles feed the exact same
/// investigation pipeline (callers, callees, hot paths) as dotnet-trace profiles.
/// </summary>
/// <remarks>
/// The report is generated with a match-everything pattern and <c>PrintCallstacks="Full"</c>,
/// which yields one <c>&lt;Instance&gt;</c> per call-tree node: its <c>CallStack</c> is the
/// root→node frame chain and its <c>OwnTime</c> is the self-time at that node. Emitting each
/// instance as one weighted sample therefore reconstructs the sampled profile exactly:
/// self-time aggregates back per leaf, subtree time per stack membership.
/// </remarks>
public static class DotTraceReportParser
{
    /// <summary>
    /// Parses a Reporter.exe XML report and returns the top-N hottest methods by self-time.
    /// Raw sample data is preserved for follow-up investigation queries.
    /// </summary>
    public static SpeedscopeParser.ProfilingResult Parse(string reportPath, int maxResults)
    {
        try
        {
            var frameIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var frameNames = new List<string>();
            var samples = new List<int[]>();
            var weights = new List<double>();

            using var reader = XmlReader.Create(reportPath, new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
            });

            string? currentFunction = null;
            double currentFunctionOwnTime = 0;
            bool currentFunctionHasInstances = false;

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    // A Function without Instance children (pattern without PrintCallstacks)
                    // still carries self-time; emit it as a single-frame stack so it is not lost.
                    if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "Function")
                    {
                        if (!currentFunctionHasInstances && currentFunction is not null && currentFunctionOwnTime > 0)
                            AddSample([currentFunction], currentFunctionOwnTime);
                        currentFunction = null;
                    }
                    continue;
                }

                switch (reader.Name)
                {
                    case "Function":
                        currentFunction = reader.GetAttribute("FQN");
                        currentFunctionOwnTime = ParseTime(reader.GetAttribute("OwnTime"));
                        currentFunctionHasInstances = false;

                        if (reader.IsEmptyElement)
                        {
                            if (currentFunction is not null && currentFunctionOwnTime > 0)
                                AddSample([currentFunction], currentFunctionOwnTime);
                            currentFunction = null;
                        }
                        break;

                    case "Instance":
                        currentFunctionHasInstances = true;
                        var ownTime = ParseTime(reader.GetAttribute("OwnTime"));
                        if (ownTime <= 0)
                            break;

                        var callStack = reader.GetAttribute("CallStack");
                        if (string.IsNullOrEmpty(callStack))
                            break;

                        AddSample(callStack.Split('/'), ownTime);
                        break;
                }
            }

            if (samples.Count == 0)
                return new([], 0, 0,
                    "The dotTrace report contains no self-time samples. " +
                    "The application may have been idle during the profiling window.");

            return SpeedscopeParser.Aggregate(
                [.. frameNames], [.. samples], [.. weights], maxResults);

            void AddSample(string[] stackFrames, double weightMs)
            {
                var stack = new int[stackFrames.Length];
                for (int i = 0; i < stackFrames.Length; i++)
                {
                    var frame = stackFrames[i];
                    if (!frameIndex.TryGetValue(frame, out int idx))
                    {
                        idx = frameNames.Count;
                        frameIndex[frame] = idx;
                        frameNames.Add(frame);
                    }
                    stack[i] = idx;
                }

                samples.Add(stack);
                weights.Add(weightMs);
            }
        }
        catch (XmlException ex)
        {
            return new([], 0, 0, $"Failed to parse dotTrace report XML: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new([], 0, 0, $"Error parsing dotTrace report: {ex.Message}");
        }
    }

    /// <summary>
    /// Reporter writes times in milliseconds with invariant formatting, but very small values
    /// may use a decimal point ("6.5") while whole values have none ("955").
    /// </summary>
    private static double ParseTime(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
}
