using RoslynMCP.Daemon;

namespace RoslynMCP.Services.HotReload;

/// <summary>
/// Points a launch at the agent server that everyone can reach.
/// </summary>
/// <remarks>
/// A hot reload agent connects to exactly one pipe, chosen before the app starts, and only the
/// process holding that connection can apply an edit. When each launcher ran its own server, an
/// app could only be hot-reloaded by whoever started it: the editor could not apply to a chat's
/// app, nor a chat to the user's. Naming the daemon's server at launch makes the owner the one
/// process both sides already talk to.
///
/// Falling back to the local server keeps <c>--cli</c> and <c>ROSLYNMCP_SHARED_HOST=0</c> working:
/// with no daemon there is only one process, so local is also shared.
/// </remarks>
internal static class HotReloadRouting
{
    /// <summary>
    /// The agent server to inject at launch: the daemon's when one is reachable, otherwise this
    /// process's own.
    /// </summary>
    public static async Task<string?> SharedPipeNameAsync(
        string projectPath, CancellationToken ct = default)
    {
        var (ok, result) = await AskDaemonAsync(projectPath, "pipe", ct);
        return ok ? result : null;
    }

    /// <summary>
    /// Opens the hot reload session in whichever process owns the agent, so the baseline is taken
    /// while the built output still matches the source.
    /// </summary>
    public static async Task<string?> StartSessionAsync(
        string projectPath, CancellationToken ct = default)
    {
        var (ok, result) = await AskDaemonAsync(projectPath, "start", ct);
        return ok ? result : null;
    }

    private static async Task<(bool Ok, string? Result)> AskDaemonAsync(
        string projectPath, string action, CancellationToken ct)
    {
        try
        {
            string? solutionKey = HostPaths.ResolveSolutionKey(
                Path.GetDirectoryName(projectPath) ?? Directory.GetCurrentDirectory());
            if (solutionKey is null)
                return (false, null);

            var pipe = await DaemonSpawner.ConnectOrSpawnAsync(solutionKey, ct);
            if (pipe is null)
                return (false, null);

            await using (pipe)
            {
                var request = new DaemonRequest(
                    Guid.NewGuid().ToString("N"), action,
                    new Dictionary<string, string> { ["projectPath"] = projectPath },
                    "markdown", Kind: "hot-reload");
                await IpcProtocol.WriteMessageAsync(pipe, request, ct);
                var response = await IpcProtocol.ReadMessageAsync<DaemonResponse>(pipe, ct);
                return response is { Ok: true } ? (true, response.Result) : (false, null);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // No daemon, or it cannot answer: the caller uses its own server, which is the
            // pre-daemon behaviour rather than a failure.
            return (false, null);
        }
    }
}
