using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>
/// What a restore would report if nothing were suppressed.
/// </summary>
/// <remarks>
/// <para>
/// NuGet's codes cannot be counted the way the compiler's can. <c>NU1605</c> is decided while the
/// dependency graph is walked, by a process this one does not host, and a suppressed one leaves no
/// trace: NuGet filters it before it reaches <c>project.assets.json</c>, so the artefacts on disk
/// are silent about exactly the codes a <c>NoWarn</c> names.
/// </para>
/// <para>
/// So the restore is run again, with <c>-p:NoWarn=</c> lifting every project-level suppression — a
/// global property, which a project cannot override — and its output written to a scratch directory
/// so the real <c>obj/</c> keeps the assets the build and the workspace are using. What comes back
/// is the whole log, so one restore answers for every code in the file rather than one per hover.
/// </para>
/// <para>
/// Item-level suppressions are out of reach here, and that is why the caller only counts codes
/// written as a property. A <c>NoWarn</c> on a <c>PackageReference</c> is metadata, no global
/// property lifts it, and a count taken with it still applied would report zero for a code that is
/// being suppressed exactly as intended — the one wrong answer that reads as "delete this line".
/// </para>
/// </remarks>
internal static class RestoreWarningCounts
{
    /// <summary>
    /// How long a restore's answer is served.
    /// </summary>
    /// <remarks>
    /// Longer than the compiler counts': this changes when a package version or a feed changes,
    /// not when someone types.
    /// </remarks>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>A restore that has not finished in two minutes is one this is not waiting for.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a failure is remembered.
    /// </summary>
    /// <remarks>
    /// Remembered at all, because the alternative is a two-minute restore per hover on a project
    /// whose restore cannot succeed — an offline machine, a feed asking for credentials. Shorter
    /// than a success, because the fix for it usually happens in the next few minutes.
    /// </remarks>
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(5);

    private static readonly string s_scratch =
        Path.Combine(Path.GetTempPath(), "roslyn-sense", "nowarn-restore");

    private static readonly ConcurrentDictionary<string, (ImmutableDictionary<string, int>? Counts, DateTime When)>
        s_ready = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Counts restores, so tests can assert that a warm read started none.</summary>
    internal static long Restores;

    /// <summary>
    /// Every restore warning the project would report, by code, or null when the restore could not
    /// be run.
    /// </summary>
    /// <remarks>
    /// Null and empty are different answers and are kept different. An empty log means the restore
    /// ran and found nothing, which is what makes a suppression removable; a failed restore knows
    /// nothing at all, and reporting it as zero would recommend deleting a line on no evidence.
    /// </remarks>
    public static async Task<ImmutableDictionary<string, int>?> ForAsync(string projectPath, CancellationToken ct)
    {
        string key = Path.GetFullPath(projectPath);

        if (s_ready.TryGetValue(key, out var cached)
            && DateTime.UtcNow - cached.When < (cached.Counts is null ? FailureLifetime : Lifetime))
        {
            return cached.Counts;
        }

        var counts = await RestoreAsync(key, ct);
        s_ready[key] = (counts, DateTime.UtcNow);
        return counts;
    }

    private static async Task<ImmutableDictionary<string, int>?> RestoreAsync(
        string projectPath, CancellationToken ct)
    {
        string output = Path.Combine(s_scratch, Fingerprint(projectPath));
        Interlocked.Increment(ref Restores);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            },
        };

        var arguments = process.StartInfo.ArgumentList;
        arguments.Add("restore");
        arguments.Add(projectPath);
        arguments.Add("--verbosity");
        arguments.Add("quiet");

        // The lift, and the two properties that would otherwise turn a lifted warning into a failed
        // restore that reports nothing.
        arguments.Add("-p:NoWarn=");
        arguments.Add("-p:WarningsAsErrors=");
        arguments.Add("-p:TreatWarningsAsErrors=false");

        // Somewhere that is not the project's obj/. The workspace, the build and the editor are all
        // reading the real assets file, and a restore run to answer a hover must not touch it.
        arguments.Add($"-p:RestoreOutputPath={output}");

        BuildProcessHelper.ConfigureMsBuildEnvironment(process.StartInfo);

        try
        {
            BuildProcessHelper.StartWithClosedInput(process);

            // Drained in parallel: a restore that fills one pipe while we wait on the other
            // deadlocks.
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Timeout);

            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[MsBuild] Counting restore warnings for '{Path.GetFileName(projectPath)}' failed: {ex.Message}");

            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already gone, which is the outcome that was wanted.
            }

            return null;
        }

        return Read(Path.Combine(output, "project.assets.json"));
    }

    /// <summary>The <c>logs</c> section of an assets file, counted by code.</summary>
    private static ImmutableDictionary<string, int>? Read(string assetsPath)
    {
        if (!File.Exists(assetsPath))
            return null;

        try
        {
            using var stream = File.OpenRead(assetsPath);
            using var document = JsonDocument.Parse(stream);

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (document.RootElement.TryGetProperty("logs", out var logs)
                && logs.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in logs.EnumerateArray())
                {
                    if (entry.TryGetProperty("code", out var code) && code.GetString() is { Length: > 0 } id)
                        counts[id] = counts.GetValueOrDefault(id) + 1;
                }
            }

            return counts.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.Error.WriteLine($"[MsBuild] Restore log '{assetsPath}' could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A stable directory name for a project.
    /// </summary>
    /// <remarks>
    /// Stable rather than a fresh temp directory per restore, so the second restore of a project is
    /// incremental instead of a full graph walk. Hashed because the project's path is not a
    /// directory name and two projects called <c>Api.csproj</c> are not the same project.
    /// </remarks>
    private static string Fingerprint(string projectPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(projectPath.ToLowerInvariant()));
        return Path.GetFileNameWithoutExtension(projectPath) + "-" + Convert.ToHexString(hash)[..12];
    }

    internal static void Clear()
    {
        s_ready.Clear();
        Interlocked.Exchange(ref Restores, 0);
    }

    /// <summary>Seeds an answer, so tests can exercise the reporting without a restore.</summary>
    internal static void Seed(string projectPath, ImmutableDictionary<string, int>? counts) =>
        s_ready[Path.GetFullPath(projectPath)] = (counts, DateTime.UtcNow);
}
