using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynMCP.Services;

namespace RoslynMCP.Tests;

/// <summary>
/// Provides information about the test execution environment.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>
    /// Turns the restore watcher off for the suite, before any test runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is a background daemon: it holds a directory handle on every loaded project's <c>obj</c>
    /// and evicts workspaces asynchronously when a restore lands there. Both are right in a running
    /// server and neither is welcome here. These tests write into fixture trees, build them and
    /// restore them constantly, so evictions — and the editor refreshes they raise — would arrive in
    /// whichever test happened to be running, and most of this suite runs in parallel.
    /// </para>
    /// <para>
    /// <see cref="RestoreWatcher.ArmForTests"/> turns it back on for the tests that are about it,
    /// scoped to the test that asks.
    /// </para>
    /// </remarks>
    [ModuleInitializer]
    internal static void DisableRestoreWatching() =>
        Environment.SetEnvironmentVariable("ROSLYNMCP_NO_RESTORE_WATCH", "1");

    /// <summary>
    /// Points the evaluation cache at a directory owned by this test run.
    /// </summary>
    /// <remarks>
    /// The default root is shared machine state, and this suite loads fixture projects whose
    /// content it mutates constantly. Left shared, a parallel testhost — or the user's live
    /// daemon — could serve this run a stale evaluation, or this run could poison theirs. Same
    /// hazard as the restore watcher above: machine-global background state and fixture churn do
    /// not mix.
    /// </remarks>
    [ModuleInitializer]
    internal static void SandboxEvaluationCache() =>
        Environment.SetEnvironmentVariable(
            "ROSLYNMCP_EVAL_CACHE_DIR",
            Path.Combine(Path.GetTempPath(), "roslyn-sense-tests", "eval-cache-" + Environment.ProcessId));

    /// <summary>
    /// Keeps the suite off the network.
    /// </summary>
    /// <remarks>
    /// Navigating into a dependency now reaches a symbol server and GitHub by default, which is
    /// right for a running server and wrong for a test run: the suite has to pass on a machine
    /// with no route out, and a test that quietly downloads a fourteen-megabyte PDB is neither
    /// fast nor repeatable. Source embedded in a PDB stays on, since it needs no network. The
    /// tests that are about fetching turn these back on for their own duration.
    /// </remarks>
    [ModuleInitializer]
    internal static void KeepExternalSourceOffline()
    {
        Environment.SetEnvironmentVariable("ROSLYNMCP_SOURCE_LINK", "0");
        Environment.SetEnvironmentVariable("ROSLYNMCP_SYMBOL_SERVER", "0");
        Environment.SetEnvironmentVariable("ROSLYNMCP_REFERENCE_SOURCE", "0");
    }

    /// <summary>
    /// Makes a background-thread crash name itself before it kills the testhost.
    /// </summary>
    /// <remarks>
    /// An exception that escapes a thread-pool or <see cref="FileSystemWatcher"/> callback takes
    /// the process down, and vstest then waits on the dead host until its hang timeout — a
    /// ten-minute run whose only symptom is "test host process crashed", with no stack and no
    /// guilty test. This cannot stop the crash (by the time the event fires the runtime is
    /// committed), but the full exception in stderr turns the next such incident from a Heisenbug
    /// hunt into a stack trace read.
    /// </remarks>
    [ModuleInitializer]
    internal static void ReportBackgroundCrashes() =>
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine(
                $"[TestEnvironment] Unhandled exception on a background thread "
                + $"(terminating={e.IsTerminating}): {e.ExceptionObject}");

    /// <summary>
    /// Returns <c>true</c> when Visual Studio or Build Tools MSBuild was registered
    /// by <see cref="WorkspaceService"/>, enabling legacy .csproj support.
    /// </summary>
    public static bool HasVisualStudioMSBuild => WorkspaceService.IsLegacyProjectSupported;

    /// <summary>
    /// Probes whether the bundled Razor source generator can load and produce
    /// source-generated documents for the Blazor fixture project. Result is cached.
    /// </summary>
    public static readonly Lazy<bool> IsRazorSourceGeneratorAvailable = new(ProbeRazorSourceGenerator);

    private static bool ProbeRazorSourceGenerator()
    {
        try
        {
            // Trigger MSBuildLocator registration before creating a bare workspace.
            WorkspaceService.EnsureRegistered();
            using var workspace = MSBuildWorkspace.Create();
            var project = workspace.OpenProjectAsync(FixturePaths.BlazorProjectFile).GetAwaiter().GetResult();
            var docs = project.GetSourceGeneratedDocumentsAsync().GetAwaiter().GetResult();
            return docs.Any();
        }
        catch
        {
            return false;
        }
    }
}
