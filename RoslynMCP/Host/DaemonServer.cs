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
    private volatile Microsoft.Extensions.DependencyInjection.ServiceProvider _services;
    private readonly DaemonLifecycle _lifecycle;
    private readonly string _pipeName;
    private readonly string _workingDir;

    /// <summary>
    /// Providers replaced by a configuration reload. Kept alive, never disposed mid-run:
    /// in-flight tool calls and already-attached LSP sessions still resolve from them, and
    /// disposing one would also dispose the stateful stores the current provider carried over.
    /// The process exits with the daemon, which is what reclaims them — same as the current
    /// provider today.
    /// </summary>
    private readonly List<IServiceProvider> _retired = [];

    private DaemonServer(
        Microsoft.Extensions.DependencyInjection.ServiceProvider services,
        DaemonLifecycle lifecycle, string pipeName, string workingDir)
    {
        _services = services;
        _lifecycle = lifecycle;
        _pipeName = pipeName;
        _workingDir = workingDir;
    }

    public static async Task<int> RunHostAsync(string solutionPathArg)
    {
        string solutionKey = Path.GetFullPath(solutionPathArg);
        RedirectConsoleToLog(solutionKey);
        string workingDir = Path.GetDirectoryName(solutionKey) ?? Directory.GetCurrentDirectory();

        var (config, _, configError) = RoslynSenseConfigLoader.Load(workingDir);

        // Said out loud, because the alternative is what it used to be: one field of the wrong
        // type stops the whole file from loading, every setting in it silently goes back to its
        // default, and the editor shows a solution configured by nobody with nothing anywhere to
        // say why. ConfigWatcher says the same on a reload; the first load said nothing at all.
        if (configError is not null)
            Console.Error.WriteLine($"[Config] {configError}; running on defaults.");

        var settings = EffectiveSettings.Resolve(Array.Empty<string>(), config, out var configWarnings);

        foreach (string warning in configWarnings)
            Console.Error.WriteLine($"[Config] {warning}");
        DebuggerViewOptions.Current = settings.DebugView;

        // A daemon lives long enough to reload; standby hosts are what make those reloads meet
        // warm MSBuild processes instead of paying initialisation again.
        SharedBuildHost.EnableStandbys();

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

        DebuggerViewOptions.Current = settings.DebugView;
        // A session that is stopped right now picks the new policy up on its next expansion,
        // which is the whole point of making these switchable rather than start-up only.
        Services.DebugSessionManager.GetSession()?.ApplyViewOptions(settings.DebugView);

        WorkspaceService.MaxCachedWorkspaces = settings.MaxWorkspaces;
        WorkspaceService.EnsureRegistered();

        bool useToon = string.Equals(settings.TableFormat, "toon", StringComparison.OrdinalIgnoreCase);
        IOutputFormatter defaultFmt = useToon ? new ToonFormatter() : new MarkdownFormatter();
        var services = ToolHostServices.Build(settings, defaultFmt, workingDir);

        string pipeName = HostPaths.PipeName(solutionKey);
        Console.Error.WriteLine($"[Daemon] Host started for '{solutionKey}' (pipe '{pipeName}', idle {settings.HostIdleMinutes}m).");

        // Say who we are, then make sure something is showing it. Both are advisory and neither
        // can fail the host.
        HostRegistry.Publish(solutionKey);
        TrayLauncher.EnsureRunning();

        // No eager warm-up: projects load lazily on the first tool call that touches them
        // (open file X -> load X + its references only). Warming the whole solution here would
        // reintroduce the all-projects load the incremental workspace exists to avoid.

        var server = new DaemonServer(services, lifecycle, pipeName, workingDir);

        // The daemon outlives every client, so it is the process that has to notice a config
        // edit — the thin clients and LSP proxies just forward here.
        using var configWatcher = ConfigWatcher.Start(workingDir, [], settings, server.ApplyConfigReload);

        try
        {
            await server.AcceptLoopAsync(shutdownCts.Token);
        }
        catch (OperationCanceledException) { /* idle shutdown */ }

        Console.Error.WriteLine("[Daemon] Idle/shutdown; disposing workspaces.");
        HostRegistry.Withdraw(solutionKey);
        await WorkspaceService.ShutdownAsync();
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

    /// <summary>
    /// Applies a <c>roslynsense.json</c> reload to the running host: rebuilds the tool-host
    /// container under the new settings (carrying the stateful stores over), republishes the
    /// language registry, and asks connected editors to re-pull. Tool calls already in flight
    /// finish on the provider they started with; everything after the swap sees the new one.
    /// </summary>
    /// <remarks>
    /// Attached LSP sessions keep the provider they initialized with, but almost every handler
    /// resolves packs through <c>LanguageRegistry.Current</c> — republished here — so behavior
    /// follows the new settings immediately; only the capabilities advertised at initialize
    /// stay until the editor reconnects.
    /// </remarks>
    internal void ApplyConfigReload(ConfigReload reload)
    {
        Console.Error.WriteLine(
            $"[Daemon] {Config.RoslynSenseConfigLoader.FileName} changed: {string.Join("; ", reload.Changes)}. Applying.");
        foreach (string warning in reload.Warnings)
            Console.Error.WriteLine($"[Daemon] Config warning: {warning}");

        var settings = reload.Settings;
        bool useToon = string.Equals(settings.TableFormat, "toon", StringComparison.OrdinalIgnoreCase);
        IOutputFormatter formatter = useToon ? new ToonFormatter() : new MarkdownFormatter();

        var previous = _services;
        var fresh = ToolHostServices.Build(settings, formatter, _workingDir, carryFrom: previous);
        lock (_retired)
        {
            _retired.Add(previous);
            _services = fresh;
        }

        WorkspaceService.MaxCachedWorkspaces = settings.MaxWorkspaces;
        _lifecycle.UpdateIdleTimeout(TimeSpan.FromMinutes(settings.HostIdleMinutes));

        // The reload log already described the debugger diff; this is what makes it true. The
        // editor's settings push takes the same two steps in ConfigurationHandler — without them
        // here, a roslynsense.json edit applied only at the next daemon start.
        DebuggerViewOptions.Current = settings.DebugView;
        Services.DebugSessionManager.GetSession()?.ApplyViewOptions(settings.DebugView);

        // Editors re-pull diagnostics, lenses and tokens so anything a toggle changed shows up
        // without a keystroke.
        Lsp.LspSessionRegistry.ScheduleRefresh(Lsp.RefreshKind.All, "config-reload");
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

                if (string.Equals(request.Kind, "lsp", StringComparison.Ordinal))
                {
                    // LSP handshake: this connection becomes a long-lived duplex LSP session.
                    // Handing the raw pipe over is safe because ReadMessageAsync reads exactly
                    // 4+N framed bytes and never buffers ahead — the next byte on the pipe is
                    // the first byte of LSP JSON-RPC. The `await using` above keeps disposal
                    // ownership here; the session runs until the editor disconnects, and
                    // OnConnectionOpened/Closed keeps the idle timer disarmed meanwhile.
                    Console.Error.WriteLine("[Daemon] LSP session attached.");
                    await Lsp.LspSessionHost.RunAsync(pipe, _services, ct);
                    Console.Error.WriteLine("[Daemon] LSP session ended.");
                    return;
                }

                var response = string.Equals(request.Kind, "editor-debug", StringComparison.Ordinal)
                    ? await RelayEditorDebugAsync(request, ct)
                    : string.Equals(request.Kind, "hot-reload", StringComparison.Ordinal)
                        ? await HotReloadHostAsync(request, ct)
                        : await DispatchAsync(request, ct);
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

    /// <summary>
    /// Relays a debug command from an MCP client into the editor's own debug session: the
    /// chat-owned debugger lives in the client process, but the editor's debugger belongs to
    /// VSCode — only a connected LSP session can drive it (DAP requests via the extension).
    /// </summary>
    private static async Task<DaemonResponse> RelayEditorDebugAsync(DaemonRequest request, CancellationToken ct)
    {
        if (!Lsp.LspSessionRegistry.HasSessions)
            return new DaemonResponse(request.Id, false, null,
                "No editor is connected to the shared host.");

        var p = new Lsp.Protocol.EditorDebugCommandParams(
            request.Tool,
            request.Args.GetValueOrDefault("expression"),
            request.Args.GetValueOrDefault("file"),
            int.TryParse(request.Args.GetValueOrDefault("line"), out int line) ? line : 0,
            request.Args.GetValueOrDefault("condition"));

        string? result = await Lsp.LspSessionRegistry.TryInvokeEditorDebugCommandAsync(p, ct);
        return result is null
            ? new DaemonResponse(request.Id, false, null,
                "The editor did not handle the debug command (no active debug session in the editor).")
            : new DaemonResponse(request.Id, true, result, null);
    }

    /// <summary>
    /// Serves the launch-time half of hot reload for a chat that is about to start an app.
    /// </summary>
    /// <remarks>
    /// The daemon owns the agent server for the whole solution, so an app is reachable no matter
    /// who started it: the chat injects this pipe name, the agent connects here, and an apply from
    /// either the editor or any chat lands in the one process that holds the connection. The
    /// alternative — each launcher running its own agent server — makes the app applicable only
    /// by whoever happened to start it.
    /// </remarks>
    private static async Task<DaemonResponse> HotReloadHostAsync(DaemonRequest request, CancellationToken ct)
    {
        switch (request.Tool)
        {
            case "pipe":
                return new DaemonResponse(
                    request.Id, true, Services.HotReload.HotReloadAgentServer.Instance.PipeName, null);

            case "start":
            {
                // Opened here, at launch, for the same reason the in-process path opens it there:
                // this is the one moment the built output provably matches the source, so the
                // baseline predates the user's next edit.
                string projectPath = request.Args.GetValueOrDefault("projectPath") ?? "";
                if (projectPath.Length == 0)
                    return new DaemonResponse(request.Id, false, null, "No project path.");

                var (session, message) = await Services.HotReload.HotReloadService.StartAsync(projectPath, ct);
                return new DaemonResponse(request.Id, session is not null, message, message);
            }

            default:
                return new DaemonResponse(request.Id, false, null, $"Unknown hot reload action '{request.Tool}'.");
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
