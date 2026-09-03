using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc;

namespace RoslynMCP.Lsp;

/// <summary>
/// Runs one LSP session over an arbitrary duplex stream (a daemon named-pipe connection, or
/// this process's stdio in fallback mode) until the peer disconnects or sends <c>exit</c>.
/// </summary>
internal static class LspSessionHost
{
    public static async Task RunAsync(Stream input, Stream output, IServiceProvider services, CancellationToken ct)
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

        var handler = new HeaderDelimitedMessageHandler(output, input, formatter);
        using var server = new LspServer(services);
        using var rpc = new JsonRpc(handler);
        rpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions { UseSingleObjectParameterDeserialization = true });
        server.Attach(rpc);

        rpc.StartListening();
        using var reg = ct.Register(() => rpc.Dispose());
        try
        {
            await rpc.Completion;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or ConnectionLostException)
        {
            // Peer vanished / shutdown — normal session end.
        }
    }

    public static Task RunAsync(Stream duplex, IServiceProvider services, CancellationToken ct) =>
        RunAsync(duplex, duplex, services, ct);
}
