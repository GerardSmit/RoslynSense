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
    private const int ExpectedProtocol = 1;

    /// <summary>The environment variable the agent reads to find this pipe. Must match
    /// <c>RoslynMCP.HotReloadAgent</c>, which cannot reference this assembly.</summary>
    public const string PipeVariableName = "ROSLYNSENSE_HOTRELOAD_PIPE";

    private static HotReloadAgentServer? s_instance;
    private static readonly Lock s_gate = new();

    private readonly ConcurrentDictionary<int, Agent> _agents = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _pipeName;

    private sealed record Agent(
        int ProcessId,
        string Name,
        string[] Capabilities,
        NamedPipeServerStream Pipe,
        BinaryReader Reader,
        BinaryWriter Writer,
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

    public IReadOnlyList<HotReloadTargetInfo> Targets =>
        [.. _agents.Values.Select(a => new HotReloadTargetInfo(a.Name, a.ProcessId, "CoreCLR"))];

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
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                ok = false;
                errors.Add($"{agent.Name} (pid {agent.ProcessId}) disconnected.");
                Drop(agent);
            }
            finally
            {
                agent.Gate.Release();
            }

            if (ok)
                applied.Add($"{agent.Name} (pid {agent.ProcessId})");
        }

        return (applied, errors);
    }

    private static async Task<(bool Ok, string Error)> SendAsync(
        Agent agent, HotReloadDelta delta, CancellationToken cancellationToken)
    {
        agent.Writer.Write(OpApplyUpdate);
        agent.Writer.Write(delta.ModuleId.ToByteArray());
        WriteBlock(agent.Writer, delta.MetadataDelta);
        WriteBlock(agent.Writer, delta.IlDelta);
        WriteBlock(agent.Writer, delta.PdbDelta);
        agent.Writer.Flush();

        // The agent answers on the same connection, so the read is the acknowledgement.
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        bool ok = agent.Reader.ReadBoolean();
        string error = agent.Reader.ReadString();
        return (ok, ok ? "" : error);
    }

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
                return;
            }

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                await pipe.DisposeAsync();
                if (cancellationToken.IsCancellationRequested)
                    return;
                continue;
            }

            Register(pipe);
        }
    }

    private void Register(NamedPipeServerStream pipe)
    {
        try
        {
            var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
            var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);

            int version = reader.ReadInt32();
            if (version != ExpectedProtocol)
            {
                pipe.Dispose();
                return;
            }

            int processId = reader.ReadInt32();
            string name = reader.ReadString();
            string[] capabilities = reader.ReadString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            _agents[processId] = new Agent(
                processId, name, capabilities, pipe, reader, writer, new SemaphoreSlim(1, 1));
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            pipe.Dispose();
        }
    }

    private void Drop(Agent agent)
    {
        _agents.TryRemove(agent.ProcessId, out _);
        try { agent.Pipe.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var agent in _agents.Values)
            Drop(agent);
        _cts.Dispose();
    }
}
