using System;
using System.Diagnostics;

namespace RoslynMCP.Services;

/// <summary>
/// Millisecond-resolution markers for the serial runway a cold open crosses before its first
/// BuildHost evaluation. Prints only under <c>ROSLYNMCP_EVAL_TIMING=1</c>, against process start,
/// so the phases that precede every lane — CLI plumbing, solution parse, classification, MEF —
/// can be read off one log instead of inferred from watches that each start somewhere else.
/// </summary>
internal static class RunwayTrace
{
    private static readonly bool s_enabled =
        Environment.GetEnvironmentVariable("ROSLYNMCP_EVAL_TIMING") == "1";

    private static readonly long s_processStart = Stopwatch.GetTimestamp()
        - (long)((DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds
            * Stopwatch.Frequency);

    public static void Mark(string label)
    {
        if (!s_enabled)
            return;

        double ms = (Stopwatch.GetTimestamp() - s_processStart) * 1000.0 / Stopwatch.Frequency;
        Console.Error.WriteLine($"[Runway] {ms:F0} ms {label}");
    }
}
