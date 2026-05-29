using System.IO.Pipes;
using System.Reflection;
using RoslynMCP.Config;
using RoslynMCP.Services;

namespace RoslynMCP.Daemon;

/// <summary>
/// The shared-host daemon: a named-pipe server that owns the Roslyn workspaces for one
/// solution and executes tool calls forwarded by thin MCP-client processes. One request per
/// connection (so concurrent calls are just concurrent connections); disposes everything and
/// exits once idle. Entry point for <c>roslyn-sense --host &lt;solution&gt;</c>.
/// </summary>
internal sealed class DaemonServer
{
    private readonly IServiceProvider _services;
    private readonly DaemonLifecycle _lifecycle;
    private readonly string _pipeName;

    private DaemonServer(IServiceProvider services, DaemonLifecycle lifecycle, string pipeName)
    {
        _services = services;
        _lifecycle = lifecycle;
        _pipeName = pipeName;
    }

    public static async Task<int> RunHostAsync(string solutionPathArg)
    {
        string solutionKey = Path.GetFullPath(solutionPathArg);
        RedirectConsoleToLog(solutionKey);
        string workingDir = Path.GetDirectoryName(solutionKey) ?? Directory.GetCurrentDirectory();

        var (config, _, _) = RoslynSenseConfigLoader.Load(workingDir);
        var settings = EffectiveSettings.Resolve(Array.Empty<string>(), config, out _);

        // Acquire the single-owner lock BEFORE any expensive setup (MSBuild registration, DI
        // build). This is what guarantees exactly one live host per solution: a daemon that
        // loses the race exits immediately, before listening on the pipe — so two daemons can
        // never both serve. The OS releases the lock on process death, so a crash self-heals.
        using var shutdownCts = new CancellationTokenSource();
        var lifecycle = new DaemonLifecycle(TimeSpan.FromMinutes(settings.HostIdleMinutes), shutdownCts.Cancel);
        try
        {
            lifecycle.AcquireLock(HostPaths.LockFilePath(solutionKey));
        }
        catch (IOException)
        {
            Console.Error.WriteLine($"[Daemon] Another host already owns '{solutionKey}'; exiting.");
            lifecycle.Dispose();
            return 0;
        }

        WorkspaceService.MaxCachedWorkspaces = settings.MaxWorkspaces;
        WorkspaceService.EnsureRegistered();

        bool useToon = string.Equals(settings.TableFormat, "toon", StringComparison.OrdinalIgnoreCase);
        IOutputFormatter defaultFmt = useToon ? new ToonFormatter() : new MarkdownFormatter();
        var services = ToolHostServices.Build(settings, defaultFmt, workingDir);

        string pipeName = HostPaths.PipeName(solutionKey);
        Console.Error.WriteLine($"[Daemon] Host started for '{solutionKey}' (pipe '{pipeName}', idle {settings.HostIdleMinutes}m).");

        // No eager warm-up: projects load lazily on the first tool call that touches them
        // (open file X -> load X + its references only). Warming the whole solution here would
        // reintroduce the all-projects load the incremental workspace exists to avoid.

        var server = new DaemonServer(services, lifecycle, pipeName);
        try
        {
            await server.AcceptLoopAsync(shutdownCts.Token);
        }
        catch (OperationCanceledException) { /* idle shutdown */ }

        Console.Error.WriteLine("[Daemon] Idle/shutdown; disposing workspaces.");
        await WorkspaceService.EvictAllAsync();
        AnalyzerService.DisposeHost();
        ProjectIndexCacheService.DisposeAll();
        ShadowCopyService.DisposeIfCreated();
        lifecycle.Dispose();
        return 0;
    }

    /// <summary>
    /// Redirects the daemon's Console to a per-host log file so it never writes to the
    /// inherited standard streams of the spawning client (whose stdout is the MCP channel).
    /// </summary>
    private static void RedirectConsoleToLog(string solutionKey)
    {
        try
        {
            string dir = HostPaths.LockDirectory(solutionKey);
            Directory.CreateDirectory(dir);
            var writer = new StreamWriter(
                new FileStream(Path.Combine(dir, "host.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);
        }
        catch
        {
            // Keep the default console on failure.
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync();
                continue;
            }

            _ = HandleConnectionAsync(pipe, ct);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        _lifecycle.OnConnectionOpened();
        try
        {
            await using (pipe)
            {
                var request = await IpcProtocol.ReadMessageAsync<DaemonRequest>(pipe, ct);
                if (request is null)
                    return; // client disconnected without sending

                var response = await DispatchAsync(request, ct);
                await IpcProtocol.WriteMessageAsync(pipe, response, ct);
                if (OperatingSystem.IsWindows())
                {
                    try { pipe.WaitForPipeDrain(); } catch { /* client may have closed */ }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or EndOfStreamException)
        {
            // Client vanished mid-exchange — nothing to do.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Daemon] Connection error: {ex.Message}");
        }
        finally
        {
            _lifecycle.OnConnectionClosed();
        }
    }

    private async Task<DaemonResponse> DispatchAsync(DaemonRequest request, CancellationToken ct)
    {
        bool isResource = string.Equals(request.Kind, "resource", StringComparison.Ordinal);
        var method = isResource ? ToolInvoker.FindResource(request.Tool) : ToolInvoker.FindTool(request.Tool);
        if (method is null)
            return new DaemonResponse(request.Id, false, null,
                $"Unknown {(isResource ? "resource" : "tool")} '{request.Tool}'.");

        IOutputFormatter fmt = string.Equals(request.Format, "toon", StringComparison.OrdinalIgnoreCase)
            ? new ToonFormatter()
            : new MarkdownFormatter();

        try
        {
            string result = await ToolInvoker.InvokeAsync(method, request.Args, _services, fmt, ct);
            return new DaemonResponse(request.Id, true, result, null);
        }
        catch (Exception ex)
        {
            string message = (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;
            return new DaemonResponse(request.Id, false, null, message);
        }
    }
}
