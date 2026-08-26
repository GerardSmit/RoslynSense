using System.Diagnostics;

namespace RoslynMCP.Services.HotReload;

/// <summary>
/// Prepares a process to accept hot reload, which has to happen before it starts.
/// </summary>
/// <remarks>
/// <para>
/// Both settings are start-time only. <c>DOTNET_MODIFIABLE_ASSEMBLIES</c> tells the runtime to
/// load assemblies in a form that can be updated at all — set it afterwards and every apply is
/// accepted and silently does nothing. <c>DOTNET_STARTUP_HOOKS</c> is read once during startup, so
/// the agent has to be listed there rather than loaded later.
/// </para>
/// <para>
/// The second of those has a way around it that this is not: a debugger attached to a running app
/// can load the agent itself and call <c>StartupHook.Attach</c>, which the engine supports through
/// <c>IDebugEngine.InjectAgentAsync</c>. It is unreachable for now — CoreCLR sessions are routed to
/// netcoredbg, which cannot inject — so this remains the only way in practice. The first has no way
/// around it at all, which is why the injection path asks the process whether it can be updated
/// rather than assuming it.
/// </para>
/// </remarks>
internal static class HotReloadLauncher
{
    /// <summary>Where the agent is published, beside the tool like the debug workers.</summary>
    private const string AgentDirectory = "hotreload";

    private const string AgentFileName = "RoslynMCP.HotReloadAgent.dll";

    /// <summary>Locates the agent assembly, or null when the tool was built without it.</summary>
    public static string? FindAgent()
    {
        foreach (var root in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) })
        {
            if (string.IsNullOrEmpty(root))
                continue;

            string candidate = Path.Combine(root, AgentDirectory, AgentFileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Adds the agent to a launch, leaving any startup hook the user already configured in place.
    /// </summary>
    /// <param name="pipeName">
    /// The agent server to point the app at. Null uses this process's own, which is right only
    /// when there is no daemon to host it — see <see cref="HotReloadRouting"/>.
    /// </param>
    /// <returns>Whether hot reload will be available in the launched process.</returns>
    public static bool Inject(ProcessStartInfo startInfo, string? pipeName = null)
    {
        if (FindAgent() is not { } agent)
            return false;

        startInfo.Environment["DOTNET_MODIFIABLE_ASSEMBLIES"] = "debug";
        startInfo.Environment[HotReloadAgentServer.PipeVariableName] =
            pipeName ?? HotReloadAgentServer.Instance.PipeName;

        // Appending rather than assigning: a project may already run a hook of its own, and
        // replacing it would change how the app starts.
        string existing = startInfo.Environment.TryGetValue("DOTNET_STARTUP_HOOKS", out string? hooks)
            ? hooks ?? ""
            : Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS") ?? "";

        startInfo.Environment["DOTNET_STARTUP_HOOKS"] = existing.Length == 0
            ? agent
            : $"{existing}{Path.PathSeparator}{agent}";

        return true;
    }
}
