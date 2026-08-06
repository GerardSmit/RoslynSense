using System.Runtime.InteropServices;

namespace RoslynMCP;

/// <summary>
/// Opens this process's standard output as a stream that either delivers every byte it is given
/// or throws.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Console.OpenStandardOutput"/> is the obvious way to reach stdout and the wrong one
/// for a stream carrying a protocol. Its Windows implementation only counts a write as successful
/// when <c>WriteFile</c> reports <c>numBytesWritten == count</c>; when that check fails the last
/// error is <c>ERROR_SUCCESS</c>, which it maps back to success. It also deliberately swallows
/// <c>ERROR_NO_DATA</c>, <c>ERROR_BROKEN_PIPE</c> and <c>ERROR_PIPE_NOT_CONNECTED</c>. For console
/// text that is kind. For JSON-RPC it is silent data loss reported as success.
/// </para>
/// <para>
/// This was not theoretical. Capturing both ends of one language-server session showed the editor
/// short by exactly 131,072 contiguous bytes out of the middle of an 820 KB message — one pump
/// buffer, written, acknowledged, never delivered. Downstream that surfaces as a JSON parse error
/// at an unrelated offset, then "Header must provide a Content-Length property", then hundreds of
/// cascading errors as the client's reader walks through message bodies treating them as headers.
/// The server looks blameless throughout, because as far as its write calls were concerned it was.
/// </para>
/// <para>
/// A standalone harness writing 150 chunks of 128 KiB to a piped stdout lost a chunk from the
/// middle of the stream in 10 of 40 runs through the console stream, and in 0 of 40 through the
/// loop below. A <see cref="FileStream"/> over the same handle is no better — it also lost data
/// mid-stream in testing (4 of 20 runs), so the fix is the explicit loop rather than a different
/// wrapper.
/// </para>
/// <para>
/// Non-Windows keeps the console stream: its Unix implementation already loops until the buffer is
/// drained and only ignores <c>EPIPE</c>, which is a write after the reader has gone rather than a
/// hole in the middle of one.
/// </para>
/// </remarks>
internal static class StdIo
{
    private const int StdOutputHandle = -11;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    /// <summary>
    /// Standard output for callers that must not lose a byte. Falls back to
    /// <see cref="Console.OpenStandardOutput"/> where the handle cannot be used.
    /// </summary>
    public static Stream OpenProtocolOutput()
    {
        if (!OperatingSystem.IsWindows())
            return Console.OpenStandardOutput();

        IntPtr handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return Console.OpenStandardOutput();

        return new ProtocolOutputStream(handle);
    }

    /// <summary>
    /// Writes to a raw Windows handle, looping until every byte has been accepted and throwing
    /// when the handle stops taking them. Writes are serialized: a second writer interleaving
    /// mid-frame would corrupt the protocol just as surely as losing bytes does.
    /// </summary>
    private sealed class ProtocolOutputStream(IntPtr handle) : Stream
    {
        private readonly Lock _gate = new();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern unsafe bool WriteFile(
            IntPtr hFile,
            byte* lpBuffer,
            int nNumberOfBytesToWrite,
            out int lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        public override unsafe void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_gate)
            {
                while (!buffer.IsEmpty)
                {
                    int written;
                    fixed (byte* p = buffer)
                    {
                        if (!WriteFile(handle, p, buffer.Length, out written, IntPtr.Zero))
                        {
                            int error = Marshal.GetLastWin32Error();
                            // The peer closing its end is how a session normally ends; the pumps
                            // treat an IOException as exactly that.
                            throw new IOException($"Writing to standard output failed with error {error}.");
                        }
                    }

                    if (written <= 0)
                        throw new IOException("Standard output accepted no bytes; the peer is gone.");

                    buffer = buffer[written..];
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        // Deliberately synchronous: the write is a blocking handle operation either way, and
        // completing it on the caller's thread keeps one pump's frames strictly ordered.
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled(cancellationToken);
            try
            {
                Write(buffer.Span);
                return ValueTask.CompletedTask;
            }
            catch (Exception ex)
            {
                return ValueTask.FromException(ex);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        // Nothing is buffered here, so there is nothing to push.
        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        // The handle belongs to the process, not to this stream: closing it would take stdout
        // away from everything else.
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }
}
