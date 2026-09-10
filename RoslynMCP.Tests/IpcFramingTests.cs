using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using RoslynMCP.Daemon;
using Xunit;

namespace RoslynMCP.Tests;

public sealed class IpcFramingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task FragmentedFramesPreserveUnicodeAndTheFollowingMessage(int chunkSize)
    {
        using var output = new MemoryStream();
        var first = new DaemonResponse("one", true, "line\n\"quoted\" — \U0001F600", null);
        var second = new DaemonResponse("two", false, null, "failure");
        await IpcProtocol.WriteMessageAsync(output, first, default);
        await IpcProtocol.WriteMessageAsync(output, second, default);
        using var input = new FragmentedStream(output.ToArray(), chunkSize);

        Assert.Equal(first, await IpcProtocol.ReadMessageAsync<DaemonResponse>(input, default));
        Assert.Equal(second, await IpcProtocol.ReadMessageAsync<DaemonResponse>(input, default));
        Assert.Null(await IpcProtocol.ReadMessageAsync<DaemonResponse>(input, default));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task IncompleteHeadersAndPayloadsAreNotTreatedAsCleanDisconnects(int bytes)
    {
        byte[] frame = Frame("{\"Id\":\"one\"}");
        using var input = new FragmentedStream(frame[..bytes], 1);
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            IpcProtocol.ReadMessageAsync<DaemonResponse>(input, default));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(268435457)]
    [InlineData(int.MaxValue)]
    public async Task InvalidLengthsAreRejectedBeforeReadingPayload(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        using var input = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcProtocol.ReadMessageAsync<DaemonResponse>(input, default));
        Assert.Equal(4, input.Position);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{broken}")]
    public async Task InvalidJsonCannotBecomeASuccessfulMessage(string json)
    {
        using var input = new MemoryStream(Frame(json));
        await Assert.ThrowsAsync<JsonException>(() =>
            IpcProtocol.ReadMessageAsync<DaemonResponse>(input, default));
    }

    [Fact]
    public async Task CancelledReadsPropagateCancellation()
    {
        using var input = new MemoryStream(Frame("{}"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IpcProtocol.ReadMessageAsync<DaemonResponse>(input, cancellation.Token));
        Assert.Equal(0, input.Position);
    }

    private static byte[] Frame(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private sealed class FragmentedStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }
}
