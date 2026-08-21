using System.Collections.Immutable;
using System.Text.Json;

namespace RoslynMCP.Services;

/// <summary>
/// Learns, per machine, how many BuildHosts are actually worth running, from the throughput of
/// the cold loads this machine has already paid for.
/// </summary>
/// <remarks>
/// <para>
/// The pool size cannot be derived from the hardware. Measured on a 32-core machine, MSBuild
/// evaluation throughput stopped scaling at about six hosts — the same solution prewarmed in
/// ~10.5s with 6, ~13s with 8, and 16 hosts were three times slower than 4 — because evaluation
/// contends on something machine-wide, not on cores. Where that ceiling sits on another machine
/// (different disk, different antivirus, different SDK spread) is unknowable up front, so it is
/// measured instead: every cold load big enough to be a fair sample records the pool size it ran
/// with and the evaluations per second it achieved.
/// </para>
/// <para>
/// Sizing then walks the measured curve. The pick is the <em>smallest</em> pool within 90% of the
/// best observed throughput — each host is a whole <c>dotnet</c> process holding a parsed SDK, so
/// ties go to fewer of them. When the pick sits at an unexplored edge of the curve, the next cold
/// load probes one step beyond it: downward first (an unneeded host freed is pure win, and a probe
/// too far down costs a little parallelism once), upward only from the top of the sampled range
/// (so the climb stops at the first size that fails the 90% test, well short of the spawn-storm
/// regime). One probe per cold load, and cold loads are rare, so a machine converges over its
/// first few solution opens and then holds still.
/// </para>
/// <para>
/// <c>ROSLYNMCP_BUILDHOST_POOL</c> bypasses all of this with an explicit size;
/// <c>ROSLYNMCP_NO_POOL_CALIBRATION=1</c> keeps the heuristic default without recording. The
/// state lives beside the evaluation cache, so the test suite's sandboxing covers it too.
/// </para>
/// </remarks>
internal static class BuildHostPoolCalibration
{
    private const int Version = 1;

    /// <summary>Fewer host-evaluations than this is noise, not a sample: a mostly-hot load's
    /// stragglers say nothing about what a saturated pool can do.</summary>
    private const int MinEvaluations = 12;

    internal sealed record Sample(int Pool, double EvalsPerSecond, int Count);

    private sealed record State(int Version, ImmutableArray<Sample> Samples);

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("ROSLYNMCP_NO_POOL_CALIBRATION") is not ("1" or "true" or "on");

    private static string FilePath => Path.Combine(EvaluationCache.Root, "buildhost-pool.json");

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    /// <summary>
    /// The pool size this machine's measurements recommend, or <paramref name="heuristic"/> when
    /// there are none (or calibration is off). <paramref name="upperBound"/> caps how far an
    /// upward probe may ever climb.
    /// </summary>
    public static int ChoosePoolSize(int heuristic, int upperBound)
    {
        if (!Enabled)
            return heuristic;

        try
        {
            return Choose(Load().Samples, heuristic, upperBound);
        }
        catch (Exception)
        {
            return heuristic;
        }
    }

    /// <summary>The walk described in the class remarks, separated from the disk for tests.</summary>
    internal static int Choose(ImmutableArray<Sample> samples, int heuristic, int upperBound)
    {
        if (samples.IsDefaultOrEmpty)
            return heuristic;

        double best = samples.Max(s => s.EvalsPerSecond);
        var sampled = samples.Select(s => s.Pool).ToHashSet();

        int pick = samples
            .Where(s => s.EvalsPerSecond >= best * 0.9)
            .Min(s => s.Pool);

        if (pick > 1 && pick == sampled.Min() && !sampled.Contains(pick - 1))
            return pick - 1;

        if (pick < upperBound && pick == sampled.Max() && !sampled.Contains(pick + 1))
            return pick + 1;

        return Math.Clamp(pick, 1, upperBound);
    }

    /// <summary>
    /// Records one cold load: <paramref name="hostEvaluations"/> genuine BuildHost evaluations
    /// across a pool of <paramref name="poolSize"/> in <paramref name="elapsedSeconds"/>.
    /// Ignored when the load was too small to say anything about saturation.
    /// </summary>
    public static void Record(int poolSize, int hostEvaluations, double elapsedSeconds)
    {
        if (!Enabled || elapsedSeconds <= 0.5
            || hostEvaluations < Math.Max(MinEvaluations, poolSize * 2))
        {
            return;
        }

        try
        {
            double observed = hostEvaluations / elapsedSeconds;
            var state = Load();

            var existing = state.Samples.FirstOrDefault(s => s.Pool == poolSize);
            // A light EMA rather than a plain mean: the machine underneath drifts (antivirus,
            // thermal state, what else is running), so recent loads should outweigh old ones.
            var updated = existing is null
                ? new Sample(poolSize, observed, 1)
                : existing with
                {
                    EvalsPerSecond = existing.EvalsPerSecond * 0.6 + observed * 0.4,
                    Count = existing.Count + 1,
                };

            var samples = state.Samples.RemoveAll(s => s.Pool == poolSize).Add(updated);

            Directory.CreateDirectory(EvaluationCache.Root);
            string temp = FilePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(
                new State(Version, samples), s_json));
            File.Move(temp, FilePath, overwrite: true);

            Console.Error.WriteLine(
                $"[BuildHost] Calibration: pool {poolSize} measured at {observed:0.0} evaluations/s "
                + $"({hostEvaluations} in {elapsedSeconds:0.0}s).");
        }
        catch (Exception ex)
        {
            // Two processes cold-loading at once, a read-only disk — the sample is lost, the
            // load already succeeded, and the next cold load samples again.
            ServiceLog.Warn($"Could not record pool calibration: {ex.Message}", key: "pool-calibration");
        }
    }

    private static State Load()
    {
        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllBytes(FilePath), s_json);
            if (state is { Version: Version } && !state.Samples.IsDefault)
                return state;
        }
        catch (Exception)
        {
            // Absent or torn — either way, start over; it is only ever a few samples.
        }

        return new State(Version, []);
    }
}
