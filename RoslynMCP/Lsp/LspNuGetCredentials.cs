using RoslynMCP.Services.Packages;
using StreamJsonRpc;

namespace RoslynMCP.Lsp;

/// <summary>
/// Routes a feed's 401 into a sign-in prompt in the editor
/// (server→client request <c>roslynSense/nuget/credentialRequest</c>).
/// </summary>
/// <remarks>
/// This is the only way a credential that lives in the OS keychain rather than in NuGet.config can
/// reach the daemon. It deliberately answers <c>null</c> the moment no editor is attached: in an
/// MCP-only process there is nobody to prompt, and blocking would hang the tool call rather than
/// reporting the feed as unauthenticated.
/// </remarks>
internal static class LspNuGetCredentials
{
    /// <summary>Installs this as the process-wide credential prompt. Idempotent.</summary>
    public static void Install() => NuGetCredentialPrompt.Handler = RequestAsync;

    private static async Task<NuGetCredentialReply?> RequestAsync(
        NuGetCredentialRequest request, CancellationToken ct)
    {
        foreach (var rpc in LspSessionRegistry.ActiveSessions())
        {
            try
            {
                var reply = await rpc.InvokeWithParameterObjectAsync<NuGetCredentialReply?>(
                    "roslynSense/nuget/credentialRequest", request, ct);
                if (reply is not null)
                    return reply;
            }
            catch (Exception ex) when (ex is RemoteRpcException or ObjectDisposedException)
            {
                // RemoteRpcException rather than RemoteInvocationException on purpose: a client
                // that predates this request answers RemoteMethodNotFoundException, which is a
                // sibling type. Catching only the narrower one turned "no sign-in prompt
                // available" into a hard failure of every package operation.
            }
        }
        return null;
    }
}
