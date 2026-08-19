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
