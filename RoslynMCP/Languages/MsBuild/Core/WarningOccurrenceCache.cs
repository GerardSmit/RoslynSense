using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>How much work one suppression is actually doing.</summary>
/// <param name="Count">Occurrences found with the suppression lifted.</param>
/// <param name="Projects">Projects that were counted.</param>
/// <param name="Scope">Projects the file governs, counted or not.</param>
/// <param name="ComputedUtc">When the count was taken.</param>
internal sealed record WarningOccurrences(int Count, int Projects, int Scope, DateTime ComputedUtc)
{
    /// <summary>Whether some of the governed projects were left uncounted.</summary>
    public bool Partial => Projects < Scope;
}

/// <summary>
/// How many times a suppressed warning would still be reported.
/// </summary>
/// <remarks>
/// <para>
/// A <c>NoWarn</c> entry says nothing about whether it is still earning its place. The code that
/// needed silencing three years ago may have no occurrences left, and the only way to know is to
/// lift the suppression and look — which is what this does: it recompiles the governed projects
/// with that one code forced back to a warning and counts what comes out. A zero is the answer
/// worth having, because a zero means the line can go.
/// </para>
/// <para>
/// Scope follows the file. A <c>NoWarn</c> in a <c>.csproj</c> is counted in that project; one in a
/// <c>Directory.Build.props</c> applies to every project under that directory and is counted across
/// all of them, which is the difference between "this suppression is dead" and "it is dead here".
/// </para>
/// <para>
/// Warm-read, exactly like <see cref="PackageStatusCache"/> and for the same reason: the read
/// happens on a hover, and a full compile is not something a hover may wait for. A miss reports
/// nothing and starts the count behind it. NuGet's <c>NU</c> codes are not the compiler's to
/// answer for and go to <see cref="RestoreWarningCounts"/> instead; MSBuild's <c>MSB</c> codes are
/// not counted at all, because only a full build produces them and a build is not something a
/// hover may cause.
/// </para>
/// </remarks>
internal static class WarningOccurrenceCache
{
    /// <summary>
    /// How long a count is served before it is recomputed.
    /// </summary>
    /// <remarks>
    /// Short, because unlike a package's deprecation status this changes with every edit to the
    /// code it counts. Stale-but-shown still beats blank — the number is read to decide whether a
    /// suppression line can go, and being one edit behind does not change that answer.
    /// </remarks>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How many projects one count will compile.
    /// </summary>
    /// <remarks>
    /// A repository-root <c>Directory.Build.props</c> governs everything, and counting a code
    /// across ninety projects is ninety compilations for one popup. The count says how many
    /// projects it covered so a partial answer reads as partial rather than as a small number.
    /// </remarks>
    private const int MaxProjects = 12;

    /// <summary>One count at a time, so a hover cannot occupy the machine.</summary>
    private static readonly SemaphoreSlim s_gate = new(1, 1);

    private static readonly ConcurrentDictionary<string, WarningOccurrences> s_ready =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, byte> s_inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Counts computations, so tests can assert that a warm read started none.</summary>
    internal static long Counts;

    private static string Key(string filePath, string code) =>
        $"{Path.GetFullPath(filePath)}|{code.ToUpperInvariant()}";

    /// <summary>Whether a code can be counted at all.</summary>
    /// <remarks>
    /// Everything but MSBuild's own, which only a build produces. A third-party analyzer's rule is
    /// countable for the same reason a <c>CS</c> code is — the analyzer runs in this process — and
    /// a code nothing recognises costs one compilation to discover it has no occurrences, which is
    /// the honest answer for a code no analyzer in the project defines.
    /// </remarks>
    public static bool IsCountable(string code) =>
        !code.StartsWith("MSB", StringComparison.OrdinalIgnoreCase);

    private static bool IsRestoreCode(string code) =>
        code.StartsWith("NU", StringComparison.OrdinalIgnoreCase);

    /// <summary>What is known now, or null. Never compiles anything.</summary>
    public static WarningOccurrences? TryGet(string filePath, string code)
    {
        if (!s_ready.TryGetValue(Key(filePath, code), out var occurrences))
            return null;

        if (DateTime.UtcNow - occurrences.ComputedUtc > Lifetime)
            Prime(filePath, code);

        return occurrences;
    }

    /// <summary>
    /// Starts a count and returns immediately.
    /// </summary>
    /// <remarks>
    /// Deduplicated per file and code, and run under <see cref="CancellationToken.None"/>: the
    /// request that asked for it is cancelled by the next keystroke, and a count that died with it
    /// would mean the answer never arrives for anyone who keeps typing.
    /// </remarks>
    public static void Prime(string filePath, string code)
    {
        if (!IsCountable(code))
            return;

        string key = Key(filePath, code);
        if (!s_inFlight.TryAdd(key, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await s_gate.WaitAsync(CancellationToken.None);

                try
                {
                    if (await CountAsync(filePath, code, CancellationToken.None) is { } occurrences)
                        s_ready[key] = occurrences;
                }
                finally
                {
                    s_gate.Release();
                }
            }
            catch (Exception ex)
            {
                // A failed count costs the next hover a miss and nothing else.
                Console.Error.WriteLine($"[MsBuild] Counting '{code}' for '{filePath}' failed: {ex.Message}");
            }
            finally
            {
                s_inFlight.TryRemove(key, out _);
            }
        });
    }

    /// <summary>
    /// Waits a little for a count, then gives up.
    /// </summary>
    /// <remarks>
    /// The one place waiting is allowed. A hover is a deliberate gesture at one code and the client
    /// is already showing a spinner for it, so a small wait buys the answer on the first look
    /// instead of the second — but only a small one, and the miss is still an ordinary outcome
    /// rather than an error.
    /// </remarks>
    public static async Task<WarningOccurrences?> GetAsync(
        string filePath, string code, TimeSpan wait, CancellationToken ct)
    {
        if (TryGet(filePath, code) is { } warm)
            return warm;

        if (!IsCountable(code))
            return null;

        Prime(filePath, code);

        string key = Key(filePath, code);
        var deadline = DateTime.UtcNow + wait;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (s_ready.TryGetValue(key, out var occurrences))
                return occurrences;

            await Task.Delay(50, ct);
        }

        return null;
    }

    private static async Task<WarningOccurrences?> CountAsync(
        string filePath, string code, CancellationToken ct)
    {
        var projects = WorkspaceService.LoadedProjectsUnder(filePath);
        if (projects.IsEmpty)
            return null;

        Interlocked.Increment(ref Counts);

        int total = 0;
        int counted = 0;

        // Ordered, so a partial answer is the same partial answer next time rather than whichever
        // projects the cache enumerated first.
        foreach (var project in projects
            .OrderBy(p => p.FilePath ?? p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxProjects))
        {
            if (await CountInAsync(project, code, ct) is not { } found)
                continue;

            total += found;
            counted++;
        }

        return counted == 0 ? null : new WarningOccurrences(total, counted, projects.Length, DateTime.UtcNow);
    }

    /// <summary>
    /// Occurrences in one project, with the suppression lifted.
    /// </summary>
    /// <remarks>
    /// The lift is a compilation option rather than an edit to the project file: <c>NoWarn</c>
    /// reaches Roslyn as <c>SpecificDiagnosticOptions[code] = Suppress</c>, and putting it back to
    /// <c>Warn</c> is what the compiler would have done without the line. It has to be an option
    /// and not a filter over the existing diagnostics, because a suppressed rule is not merely
    /// hidden — an analyzer all of whose rules are suppressed is never run at all.
    /// </remarks>
    private static async Task<int?> CountInAsync(Project project, string code, CancellationToken ct)
    {
        if (IsRestoreCode(code))
        {
            return project.FilePath is { Length: > 0 } path
                && await RestoreWarningCounts.ForAsync(path, ct) is { } restore
                ? restore.GetValueOrDefault(code)
                : null;
        }

        if (await project.GetCompilationAsync(ct) is not { } compilation)
            return null;

        var unsuppressed = compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
            compilation.Options.SpecificDiagnosticOptions.SetItem(code, ReportDiagnostic.Warn)));

        var analyzers = AnalyzerService.GetAnalyzersFor(project)
            .Where(a => Declares(a, code))
            .ToImmutableArray();

        if (analyzers.IsEmpty)
            return unsuppressed.GetDiagnostics(ct).Count(d => d.Id == code);

        // Only the analyzers that define this rule. The full set is what a diagnostics pass runs;
        // here every other analyzer is work whose output would be discarded.
        var driver = unsuppressed.WithAnalyzers(
            analyzers,
            new CompilationWithAnalyzersOptions(
                project.AnalyzerOptions,
                onAnalyzerException: static (ex, analyzer, _) =>
                    Console.Error.WriteLine($"[MsBuild] {analyzer.GetType().Name} threw while counting: {ex.Message}"),
                concurrentAnalysis: true,
                logAnalyzerExecutionTime: false,
                reportSuppressedDiagnostics: false));

        var diagnostics = await driver.GetAnalyzerDiagnosticsAsync(ct);
        return diagnostics.Count(d => d.Id == code);
    }

    private static bool Declares(DiagnosticAnalyzer analyzer, string code)
    {
        try
        {
            return analyzer.SupportedDiagnostics.Any(d => string.Equals(d.Id, code, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // An analyzer that throws while describing itself is one this count does without.
            return false;
        }
    }

    internal static void Clear()
    {
        s_ready.Clear();
        s_inFlight.Clear();
        Interlocked.Exchange(ref Counts, 0);
    }

    /// <summary>Seeds a count, so tests can exercise the reporting without a compilation.</summary>
    internal static void Seed(string filePath, string code, WarningOccurrences occurrences) =>
        s_ready[Key(filePath, code)] = occurrences;
}
