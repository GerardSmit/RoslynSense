using System.Buffers.Binary;
using System.Text.Json;

namespace RoslynMCP.Daemon;

/// <summary>A tool (or resource) invocation forwarded from a thin MCP client to the shared host.</summary>
internal sealed record DaemonRequest(
    string Id,
    string Tool,
    Dictionary<string, string> Args,
    string Format,
    string Kind = "tool"); // "tool" | "resource"

/// <summary>The result of a forwarded tool invocation.</summary>
internal sealed record DaemonResponse(
    string Id,
    bool Ok,
    string? Result,
    string? Error);

/// <summary>
/// Length-prefixed JSON framing over a pipe stream: a 4-byte big-endian payload length
/// followed by the UTF-8 JSON payload. Robust to arbitrary string content (no delimiter
/// escaping) and trivial to read incrementally.
/// </summary>
internal static class IpcProtocol
{
    private const int MaxMessageBytes = 256 * 1024 * 1024; // 256 MB safety ceiling

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task WriteMessageAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, s_json);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Reads one framed message, or <c>null</c> on a clean EOF (peer disconnected).</summary>
    public static async Task<T?> ReadMessageAsync<T>(Stream stream, CancellationToken ct) where T : class
    {
        byte[] header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct))
            return null;

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > MaxMessageBytes)
            throw new InvalidDataException($"IPC message length {length} is out of range.");

        byte[] payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct))
            throw new EndOfStreamException("IPC stream ended mid-message.");

        return JsonSerializer.Deserialize<T>(payload, s_json);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
                return offset != 0 ? throw new EndOfStreamException("IPC stream ended mid-frame.") : false;
            offset += read;
        }
        return true;
    }
}
