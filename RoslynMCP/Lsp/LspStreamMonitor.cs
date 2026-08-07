using System.Text;
using System.Text.Json;

namespace RoslynMCP.Lsp;

/// <summary>
/// Watches the bytes flowing over an LSP connection and reports the exact moment they stop being
/// well-framed. It never alters, delays, or reorders what it sees — it is fed a copy of every
/// chunk after that chunk has already been forwarded.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists for looks like two errors in the editor at once: a JSON parse error at
/// some byte offset, then "Header must provide a Content-Length property" as the client tries to
/// read the remaining bytes as a new message. By then the interesting evidence — which frame went
/// wrong, what the bytes around the splice were, and whether the corruption was already present on
/// the wire from the shared host — has scrolled away. Both errors are downstream symptoms of one
/// upstream event, and neither names it.
/// </para>
/// <para>
/// Placing this on the editor-bound stream of the proxy answers the question that decides where to
/// look next. A report here means the shared host (or the transport under it) emitted a malformed
/// frame. Silence here while the editor still reports corruption means something wrote to this
/// process's stdout that never came through the pump.
/// </para>
/// <para>
/// Header validation is always on: it costs a scan of the ~50 header bytes per message and a
/// countdown over the body. Setting <c>ROSLYNSENSE_LSP_TRACE=1</c> additionally parses every body
/// as JSON and tees the raw stream to a file, which is what turns a report into a reproducible
/// artifact.
/// </para>
/// </remarks>
internal sealed class LspStreamMonitor : IDisposable
{
    /// <summary>A header block longer than this is not a header block.</summary>
    private const int MaxHeaderBytes = 8 * 1024;

    /// <summary>How much of the stream before an anomaly is kept for the dump.</summary>
    private const int ContextBytes = 128 * 1024;

    /// <summary>
    /// Where the raw capture stops. A session left tracing all day would otherwise fill the disk,
    /// and the evidence that identifies a corruption is the dump around it, not the hours before.
    /// </summary>
    private const long MaxTraceBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// The largest body deep validation will hold in memory. Real messages are far below this;
    /// anything above it is a length read out of an already-broken stream.
    /// </summary>
    private const int MaxValidatedBodyBytes = 64 * 1024 * 1024;

    private static readonly Encoding s_ascii = Encoding.ASCII;

    private readonly string _label;
    private readonly bool _deep;
    private readonly Stream? _tee;
    private readonly Lock _gate = new();

    // Ring buffer of the most recent bytes, so a report can show what led up to the splice.
    private readonly byte[] _context = new byte[ContextBytes];
    private int _contextStart;
    private int _contextLength;

    private readonly List<byte> _header = new(256);
    private byte[]? _body;
    private int _bodyFilled;
    private int _bodyLength;
    private bool _inBody;

    private long _position;
    private long _frameStart;
    private long _frames;
    private long _traced;

    /// <summary>Reports are one-shot: after the stream desynchronizes every later frame is noise.</summary>
    private bool _reported;

    private LspStreamMonitor(string label, bool deep, Stream? tee)
    {
        _label = label;
        _deep = deep;
        _tee = tee;
    }

    /// <summary>True when the caller asked for body validation and a raw capture.</summary>
    public static bool TraceEnabled =>
        Environment.GetEnvironmentVariable("ROSLYNSENSE_LSP_TRACE") is "1" or "true" or "TRUE";

    /// <summary>The directory reports and captures are written to.</summary>
    public static string DiagnosticsDirectory =>
        Path.Combine(Path.GetTempPath(), "roslyn-mcp-lsp-diagnostics");

    public static LspStreamMonitor Create(string label)
    {
        bool deep = TraceEnabled;
        Stream? tee = null;
        if (deep)
        {
            try
            {
                Directory.CreateDirectory(DiagnosticsDirectory);
                string path = Path.Combine(
                    DiagnosticsDirectory,
                    $"lsp-{label}-{Environment.ProcessId}.bin");
                tee = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
                Console.Error.WriteLine($"[Lsp] Tracing the {label} stream to '{path}'.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Lsp] Could not open an LSP trace file: {ex.Message}");
            }
        }

        return new LspStreamMonitor(label, deep, tee);
    }

    /// <summary>
    /// Observes bytes that have already been forwarded. Safe to call from one pump; the lock is
    /// there so a report cannot interleave with a concurrent feed on the other direction's monitor
    /// sharing a console.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
            return;

        lock (_gate)
        {
            if (_tee is not null && _traced < MaxTraceBytes)
            {
                try
                {
                    _tee.Write(chunk);
                    // The session usually ends by being killed, which is exactly when the last
                    // bytes before the corruption matter most, so nothing is left in a buffer.
                    _tee.Flush();
                    _traced += chunk.Length;
                    if (_traced >= MaxTraceBytes)
                        Console.Error.WriteLine(
                            $"[Lsp] The {_label} capture reached {MaxTraceBytes / (1024 * 1024)} MB and stopped; " +
                            "validation continues.");
                }
                catch { /* diagnostics must never break the session */ }
            }

            Remember(chunk);

            if (_reported)
            {
                _position += chunk.Length;
                return;
            }

            Consume(chunk);
        }
    }

    private void Consume(ReadOnlySpan<byte> chunk)
    {
        int offset = 0;
        while (offset < chunk.Length)
        {
            if (_inBody)
            {
                int take = Math.Min(_bodyLength - _bodyFilled, chunk.Length - offset);
                if (_body is not null)
                    chunk.Slice(offset, take).CopyTo(_body.AsSpan(_bodyFilled));
                _bodyFilled += take;
                offset += take;
                _position += take;

                if (_bodyFilled == _bodyLength)
                {
                    _frames++;
                    if (!ValidateBody())
                        return;
                    _inBody = false;
                    _body = null;
                    _header.Clear();
                }
                continue;
            }

            byte b = chunk[offset++];
            _position++;
            if (_header.Count == 0)
                _frameStart = _position - 1;
            _header.Add(b);

            int n = _header.Count;
            bool terminated = n >= 4
                && _header[n - 4] == (byte)'\r' && _header[n - 3] == (byte)'\n'
                && _header[n - 2] == (byte)'\r' && _header[n - 1] == (byte)'\n';

            if (!terminated)
            {
                if (n > MaxHeaderBytes)
                {
                    Report(
                        "no header terminator",
                        $"Read {n:N0} bytes without the CRLFCRLF that ends a header block. " +
                        "The stream is no longer aligned to a message boundary.");
                    return;
                }
                continue;
            }

            if (!TryParseContentLength(out int length, out string headers))
            {
                Report(
                    "header without Content-Length",
                    $"A header block ended without a Content-Length. Raw header block:\n{Excerpt(headers)}");
                return;
            }

            _bodyLength = length;
            _bodyFilled = 0;
            _inBody = true;
            // Only buffer a body worth validating. The length comes off the wire, so on an already
            // desynchronized stream it can be any number that parses — allocating that would turn a
            // diagnostic into the thing that kills the session.
            _body = _deep && length > 0 && length <= MaxValidatedBodyBytes ? new byte[length] : null;
        }
    }

    private bool ValidateBody()
    {
        if (_body is null)
            return true;

        try
        {
            var reader = new Utf8JsonReader(_body, isFinalBlock: true, state: default);
            while (reader.Read()) { }
            return true;
        }
        catch (JsonException ex)
        {
            long at = ex.BytePositionInLine ?? 0;
            Report(
                "malformed JSON body",
                $"A frame of {_bodyLength:N0} bytes is not valid JSON: {ex.Message}\n" +
                $"Frame began at absolute byte {_frameStart:N0}; the body failed at byte {at:N0} " +
                $"of the body (0x{at:X}).\n" +
                $"Bytes around the failure:\n{BodyExcerpt(at)}",
                _body);
            return false;
        }
    }

    private bool TryParseContentLength(out int length, out string headers)
    {
        length = 0;
        headers = s_ascii.GetString(_header.ToArray());
        foreach (string line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), out length) && length >= 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Writes one line to stderr — which reaches the editor's output channel, where the symptom was
    /// seen — and puts the bytes themselves on disk, because the bytes are the only thing that
    /// distinguishes the possible causes.
    /// </summary>
    private void Report(string kind, string detail, byte[]? body = null)
    {
        _reported = true;

        string? dump = null;
        try
        {
            Directory.CreateDirectory(DiagnosticsDirectory);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            dump = Path.Combine(DiagnosticsDirectory, $"corruption-{_label}-{Environment.ProcessId}-{stamp}");

            File.WriteAllText(dump + ".txt",
                $"LSP protocol corruption on the {_label} stream\n" +
                $"kind:      {kind}\n" +
                $"process:   {Environment.ProcessId}\n" +
                $"frames OK: {_frames:N0}\n" +
                $"position:  {_position:N0} bytes into the session\n" +
                $"frame at:  {_frameStart:N0}\n\n" +
                detail + "\n");

            File.WriteAllBytes(dump + ".context.bin", ContextSnapshot());
            if (body is not null)
                File.WriteAllBytes(dump + ".frame.bin", body);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lsp] Could not write the corruption dump: {ex.Message}");
        }

        Console.Error.WriteLine(
            $"[Lsp] PROTOCOL CORRUPTION on the {_label} stream after {_frames:N0} good frames " +
            $"({kind}) at byte {_position:N0}." +
            (dump is null ? "" : $" Details and raw bytes: '{dump}.txt'.") +
            (_deep ? "" : " Set ROSLYNSENSE_LSP_TRACE=1 to capture the full stream next time."));
        Console.Error.WriteLine($"[Lsp] {detail}");
    }

    private void Remember(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length >= _context.Length)
        {
            chunk[^_context.Length..].CopyTo(_context);
            _contextStart = 0;
            _contextLength = _context.Length;
            return;
        }

        // Two spans rather than a byte-at-a-time loop with a modulo per byte: this runs for every
        // chunk the server sends even when tracing is off, so it has to cost nothing worth naming.
        int writeAt = (_contextStart + _contextLength) % _context.Length;
        int toEnd = Math.Min(chunk.Length, _context.Length - writeAt);
        chunk[..toEnd].CopyTo(_context.AsSpan(writeAt));
        if (toEnd < chunk.Length)
            chunk[toEnd..].CopyTo(_context);

        int room = _context.Length - _contextLength;
        if (chunk.Length <= room)
        {
            _contextLength += chunk.Length;
        }
        else
        {
            _contextStart = (_contextStart + (chunk.Length - room)) % _context.Length;
            _contextLength = _context.Length;
        }
    }

    private byte[] ContextSnapshot()
    {
        var snapshot = new byte[_contextLength];
        for (int i = 0; i < _contextLength; i++)
            snapshot[i] = _context[(_contextStart + i) % _context.Length];
        return snapshot;
    }

    private string BodyExcerpt(long at)
    {
        if (_body is null)
            return "(not captured; set ROSLYNSENSE_LSP_TRACE=1)";
        int start = (int)Math.Max(0, at - 120);
        int take = (int)Math.Min(280, _body.Length - start);
        return Excerpt(Encoding.UTF8.GetString(_body, start, take));
    }

    private static string Excerpt(string text) =>
        "----\n" + text.Replace("\r", "\\r").Replace("\n", "\\n\n") + "\n----";

    public void Dispose()
    {
        lock (_gate)
        {
            try { _tee?.Flush(); _tee?.Dispose(); }
            catch { /* nothing useful to do while shutting down */ }
        }
    }
}

/// <summary>
/// Forwards writes unchanged and shows each one to a <see cref="LspStreamMonitor"/> afterwards, so
/// the monitor observes exactly the bytes the peer received and can never affect them.
/// </summary>
internal sealed class MonitoredStream(Stream inner, LspStreamMonitor monitor) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        monitor.Feed(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        monitor.Feed(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        monitor.Feed(buffer.Span);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        monitor.Feed(buffer.AsSpan(offset, count));
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            monitor.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// The read-side twin of <see cref="MonitoredStream"/>: shows the monitor what was read before the
/// caller sees it, so the requests that produced a bad response are in the capture too.
/// </summary>
internal sealed class MonitoredReadStream(Stream inner, LspStreamMonitor monitor) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        if (read > 0)
            monitor.Feed(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken);
        if (read > 0)
            monitor.Feed(buffer.Span[..read]);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        if (read > 0)
            monitor.Feed(buffer.AsSpan(offset, read));
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            monitor.Dispose();
        base.Dispose(disposing);
    }
}
