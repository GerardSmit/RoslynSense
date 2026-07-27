using System.Diagnostics;

namespace RoslynMCP.Services;

/// <summary>
/// Centralizes process-startup configuration for build/test/coverage invocations.
/// Disables MSBuild node reuse (a frequent hang source: stale worker nodes deadlock
/// between runs) and the terminal logger (unparseable progress output).
/// </summary>
internal static class BuildProcessHelper
{
    /// <summary>
    /// Sets environment variables that make MSBuild safe to invoke from a long-lived
    /// host process. Apply to every <see cref="ProcessStartInfo"/> that spawns
    /// <c>dotnet</c>, <c>msbuild</c>, <c>dotnet-coverage</c>, or <c>vstest</c>.
    /// </summary>
    /// <summary>
    /// Variables MSBuildLocator sets on THIS process to pin it to the .NET SDK's MSBuild, so that
    /// MSBuildWorkspace can load projects in-process.
    /// </summary>
    /// <remarks>
    /// They must never reach a child build. A spawned Visual Studio <c>MSBuild.exe</c> that
    /// inherits them resolves <c>$(MSBuildExtensionsPath)</c> — and therefore
    /// <c>$(VSToolsPath)</c> — into the .NET SDK directory, where
    /// <c>Microsoft.WebApplication.targets</c> does not exist. Legacy web projects then fail to
    /// import it with MSB4226, even though Visual Studio is installed correctly.
    /// </remarks>
    private static readonly string[] LocatorEnvironmentKeys =
    [
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildExtensionsPath32",
        "MSBuildExtensionsPath64",
        "MSBuildSDKsPath",
    ];

    public static void ConfigureMsBuildEnvironment(ProcessStartInfo startInfo)
    {
        // Let the child resolve its own MSBuild layout from wherever it is installed.
        foreach (var key in LocatorEnvironmentKeys)
            startInfo.Environment.Remove(key);

        // Parseable diagnostic output (no spinners / progress bars).
        startInfo.Environment["MSBUILDTERMINALLOGGER"] = "off";

        // Disable long-lived MSBuild worker nodes. Without this, MSBuild keeps
        // node processes alive between builds; a wedged node (common with legacy
        // WebForms + source generators) makes the NEXT build hang forever.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        // Belt-and-braces: also disable the SDK build server, which caches Roslyn
        // and Razor compilers and can wedge the same way.
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
    }

    /// <summary>
    /// MSBuild command-line flag that disables node reuse. Append to any
    /// raw <c>msbuild.exe</c> invocation (the env var alone doesn't cover
    /// every code path inside MSBuild).
    /// </summary>
    public const string NoNodeReuseArg = "/nodeReuse:false";

    /// <summary>
    /// Kill the process tree and wait briefly for redirected stdout/stderr
    /// pipe readers to drain. Without the drain, async output event handlers
    /// can still be in-flight when the caller disposes the <see cref="Process"/>,
    /// occasionally producing truncated logs or AccessViolationException on the
    /// background reader thread.
    /// </summary>
    public static async Task KillAndDrainAsync(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { }

        try
        {
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(drainCts.Token);
        }
        catch { }
    }
}
