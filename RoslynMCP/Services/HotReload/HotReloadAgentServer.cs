using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace RoslynMCP.Services.HotReload;

/// <summary>
/// The tool's end of the channel to <c>RoslynMCP.HotReloadAgent</c>, which runs inside every app
/// launched with hot reload enabled.
/// </summary>
/// <remarks>
/// <para>
/// CoreCLR has no way to change a loaded assembly from outside the process, so this is not
/// optional plumbing — it is the only route. The agent connects on startup and stays connected;
/// applying an edit is a request on that connection rather than a fresh handshake, because the
/// user presses save far more often than they press run.
/// </para>
/// <para>
/// Deliberately not the debugger's channel: hot reload works on an app that is merely running,
/// which is the case that matters for the ASP.NET inner loop.
/// </para>
/// </remarks>
internal sealed class HotReloadAgentServer : IDisposable
{
    private const int OpApplyUpdate = 1;
    private const int ExpectedProtocol = 2;

    /// <summary>How long an agent gets to answer an apply. Without a bound, an app suspended by
    /// a debugger mid-apply parks the whole apply-on-save loop forever.</summary>
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The environment variable the agent reads to find this pipe. Must match
    /// <c>RoslynMCP.HotReloadAgent</c>, which cannot reference this assembly.</summary>
    public const string PipeVariableName = "ROSLYNSENSE_HOTRELOAD_PIPE";

    private static HotReloadAgentServer? s_instance;
    private static readonly Lock s_gate = new();

    /// <summary>Keyed by connection, not by process id: a registration is a connection, and
    /// keying by pid would let a recycled id silently displace a live agent.</summary>
    private readonly ConcurrentDictionary<int, Agent> _agents = new();

    private readonly CancellationTokenSource _cts = new();
    private readonly string _pipeName;
    private int _nextConnection;

    private sealed record Agent(
        int Connection,
        int ProcessId,
        string Name,
        string[] Capabilities,
        NamedPipeServerStream Pipe,
        BinaryReader Reader,
        SemaphoreSlim Gate);

    private HotReloadAgentServer(string pipeName)
    {
        _pipeName = pipeName;
        _ = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>The server for this process, started on first use. One pipe serves every app this
    /// process launched — named pipes accept many clients on one name.</summary>
    public static HotReloadAgentServer Instance
    {
        get
        {
            lock (s_gate)
                return s_instance ??= new HotReloadAgentServer($"roslyn-sense-hotreload-{Environment.ProcessId}");
        }
    }

    public string PipeName => _pipeName;

    public IReadOnlyList<HotReloadTargetInfo> Targets
    {
        get
        {
            Reap();
            return [.. _agents.Values.Select(a => new HotReloadTargetInfo(a.Name, a.ProcessId, "CoreCLR"))];
        }
    }

    /// <summary>
    /// Forgets agents whose process is gone.
    /// </summary>
    /// <remarks>
    /// A registration outlives its process: the pipe break is only noticed when something writes
    /// to it, so without this the first apply after an app exits reports a failure against a
    /// target that simply is not there any more. An app that stopped is not an app that rejected
    /// the edit, and saying so would send the user looking for a problem in their code.
    /// </remarks>
    private void Reap()
    {
        foreach (var agent in _agents.Values)
        {
            if (!IsAlive(agent.ProcessId))
                Drop(agent);
        }
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied querying it — a process we cannot ask about is more likely alive
            // than gone, and dropping a live agent loses the connection for good.
            return true;
        }
    }

    /// <summary>
    /// What the connected runtimes will accept, which is what Roslyn must be told before it
    /// computes an edit.
    /// </summary>
    /// <remarks>
    /// The intersection, not the union: a delta is emitted once and applied everywhere, so the
    /// weakest connected runtime sets the ceiling. With nothing connected the caller supplies its
    /// own baseline — there is nothing to intersect with yet.
    /// </remarks>
    public IReadOnlyList<string> Capabilities()
    {
        // Reap first, like Targets and ApplyAsync: an agent whose process exited must not keep
        // voting its old runtime's ceiling into every future edit session.
        Reap();

        var agents = _agents.Values.ToList();
        if (agents.Count == 0)
            return [];

        IEnumerable<string> shared = agents[0].Capabilities;
        foreach (var agent in agents.Skip(1))
            shared = shared.Intersect(agent.Capabilities, StringComparer.Ordinal);

        return [.. shared];
    }

    /// <summary>Sends every delta to every connected app, reporting each failure separately —
    /// one app rejecting an edit says nothing about the others.</summary>
    public async Task<(IReadOnlyList<string> Applied, IReadOnlyList<string> Errors)> ApplyAsync(
        IReadOnlyList<HotReloadDelta> deltas, CancellationToken cancellationToken = default)
    {
        var applied = new List<string>();
        var errors = new List<string>();

        Reap();

        foreach (var agent in _agents.Values)
        {
            bool ok = true;

            await agent.Gate.WaitAsync(cancellationToken);
            try
            {
                foreach (var delta in deltas)
                {
                    var (sent, error) = await SendAsync(agent, delta, cancellationToken);
                    if (sent)
                        continue;

                    ok = false;
                    errors.Add($"{agent.Name} (pid {agent.ProcessId}): {error}");
                }
            }
            catch (OperationCanceledException)
            {
                // The request may already be on the wire, so the connection is mid-frame and
                // cannot be reused: keeping it would have the next apply read this one's answer.
                Drop(agent);
                throw;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                ok = false;
                errors.Add($"{agent.Name} (pid {agent.ProcessId}) disconnected.");
                Drop(agent);
            }
            finally
            {
                try { agent.Gate.Release(); } catch (ObjectDisposedException) { }
            }

            if (ok)
                applied.Add($"{agent.Name} (pid {agent.ProcessId})");
        }

        return (applied, errors);
    }

    private static async Task<(bool Ok, string Error)> SendAsync(
        Agent agent, HotReloadDelta delta, CancellationToken cancellationToken)
    {
        // The frame is built in memory and written in one cancellable call, so a cancelled
        // apply never leaves half a request on the wire.
        using var frame = new MemoryStream();
        using (var writer = new BinaryWriter(frame, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(OpApplyUpdate);
            writer.Write(delta.ModuleId.ToByteArray());
            WriteBlock(writer, delta.MetadataDelta);
            WriteBlock(writer, delta.IlDelta);
            WriteBlock(writer, delta.PdbDelta);
            writer.Write(delta.UpdatedTypes.Length);
            foreach (int token in delta.UpdatedTypes)
                writer.Write(token);
        }

        await agent.Pipe.WriteAsync(frame.GetBuffer().AsMemory(0, (int)frame.Length), cancellationToken);
        await agent.Pipe.FlushAsync(cancellationToken);

        // The agent answers on the same connection, so the read is the acknowledgement. The
        // read itself is synchronous; the bound is what makes it safe — on timeout or
        // cancellation the caller drops the agent, which also unblocks this read.
        var read = Task.Run(() =>
        {
            bool ok = agent.Reader.ReadBoolean();
            string error = agent.Reader.ReadString();
            return (Ok: ok, Error: error);
        }, CancellationToken.None);

        var completed = await Task.WhenAny(read, Task.Delay(ApplyTimeout, cancellationToken));
        if (completed != read)
        {
            Observe(read);
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException($"{agent.Name} (pid {agent.ProcessId}) did not answer the apply in time.");
        }

        var (okResult, errorResult) = await read;
        return (okResult, okResult ? "" : errorResult);
    }

    /// <summary>Keeps an abandoned read's eventual failure from surfacing as an unobserved
    /// task exception when the pipe under it is torn down.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

    private static void WriteBlock(BinaryWriter writer, byte[] block)
    {
        writer.Write(block.Length);
        if (block.Length > 0)
            writer.Write(block);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            catch (IOException)
            {
                // Transient (the name can be briefly busy while an old instance closes). Giving
                // up would orphan every future launch: the pipe name is still handed to each of
                // them, so a dead accept loop reads as "nothing is running" forever.
                try { await Task.Delay(1000, cancellationToken); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                await pipe.DisposeAsync();
                if (cancellationToken.IsCancellationRequested)
                    return;
                continue;
            }

            // On its own task, under a timeout: a client that connects and never completes the
            // handshake must not block every later registration.
            _ = RegisterAsync(pipe, cancellationToken);
        }
    }

    private async Task RegisterAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var handshake = Task.Run(() => ReadHandshake(pipe), CancellationToken.None);

        var completed = await Task.WhenAny(handshake, Task.Delay(HandshakeTimeout, cancellationToken));
        if (completed != handshake)
        {
            Observe(handshake);
            try { pipe.Dispose(); } catch { }
            return;
        }

        try
        {
            if (await handshake is not { } agent)
            {
                pipe.Dispose();
                return;
            }

            int connection = Interlocked.Increment(ref _nextConnection);
            _agents[connection] = agent with { Connection = connection };
        }
        catch
        {
            try { pipe.Dispose(); } catch { }
        }
    }

    private static Agent? ReadHandshake(NamedPipeServerStream pipe)
    {
        var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);

        int version = reader.ReadInt32();
        if (version != ExpectedProtocol)
            return null;

        int processId = reader.ReadInt32();
        string name = reader.ReadString();
        string[] capabilities = reader.ReadString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return new Agent(Connection: 0, processId, name, capabilities, pipe, reader, new SemaphoreSlim(1, 1));
    }

    private void Drop(Agent agent)
    {
        _agents.TryRemove(agent.Connection, out _);
        try { agent.Pipe.Dispose(); } catch { }
        try { agent.Gate.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var agent in _agents.Values)
            Drop(agent);
        _cts.Dispose();
    }
}
