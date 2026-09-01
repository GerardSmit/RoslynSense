using Microsoft.Extensions.DependencyInjection;
using RoslynMCP.Config;
using RoslynMCP.Daemon;
using RoslynMCP.Services;
using RoslynMCP.Services.Database;

namespace RoslynMCP.Lsp;

/// <summary>
/// Entry point for <c>roslyn-sense --lsp [--solution &lt;path&gt;]</c> — the process an editor
/// spawns as its C# language server. When a per-solution shared daemon is reachable (or can
/// be spawned), this process is a dumb duplex byte proxy: LSP JSON-RPC flows stdin→pipe and
/// pipe→stdout untouched, so the daemon's workspace (shared with MCP clients) serves the
/// editor too. Falls back to hosting the LSP session in-process over stdio.
/// </summary>
internal static class LspProxy
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? solutionArg = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals("--solution", StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 < args.Length)
                solutionArg = args[i + 1];
            else
                Console.Error.WriteLine("[roslyn-sense] --solution requires a path; ignoring.");
        }

        string startPath = solutionArg ?? Directory.GetCurrentDirectory();
        string? solutionKey = solutionArg is not null && PathHelper.IsSolutionFile(solutionArg)
            ? Path.GetFullPath(solutionArg)
            : HostPaths.ResolveSolutionKey(startPath);

        // Also on the in-process path, which serves the same requests when no daemon is reachable.
        WorkspaceService.BindSolution(solutionKey);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Stream stdin = Console.OpenStandardInput();

        // Not Console.OpenStandardOutput(): the stream it returns reports short and failed pipe
        // writes as successes, which loses whole buffers out of a JSON-RPC frame and leaves the
        // editor to report the damage as a parse error it cannot attribute. See StdIo.
        Stream stdout = StdIo.OpenProtocolOutput();

        // From here on stdout carries a protocol, so nothing else may write to it. Console.Out is
        // pointed at stderr rather than left alone: a stray Console.WriteLine anywhere in this
        // process — or in a library it loads during start-up — would otherwise land in the middle
        // of a JSON-RPC frame, which the editor reports as a parse error somewhere else entirely.
        Console.SetOut(Console.Error);

        using var monitor = LspStreamMonitor.Create("editor-bound");
        stdout = new MonitoredStream(stdout, monitor);

        // Only while tracing: the requests are what make a bad response reproducible, and capturing
        // them costs a copy of every keystroke's worth of traffic.
        LspStreamMonitor? inbound = null;
        if (LspStreamMonitor.TraceEnabled)
        {
            inbound = LspStreamMonitor.Create("host-bound");
            stdin = new MonitoredReadStream(stdin, inbound);
        }
        using var inboundScope = inbound;

        var (config, _, configError) = RoslynSenseConfigLoader.Load(startPath);

        // To stderr, which the editor shows in the server's output channel — the one place a
        // person looking for why nothing is configured will think to look. See DaemonServer for
        // why a file that failed to load must not fail quietly.
        if (configError is not null)
            Console.Error.WriteLine($"[Config] {configError}; running on defaults.");

        var settings = EffectiveSettings.Resolve(Array.Empty<string>(), config, out _);

        if (settings.SharedHost && solutionKey is not null)
        {
            // Never let a daemon hiccup kill the language server: any failure before the
            // first byte has flowed falls through to the in-process fallback below. Once
            // traffic flowed, the editor's own restart handling owns recovery.
            bool proxied = false;
            try
            {
                var pipe = await DaemonSpawner.ConnectOrSpawnAsync(solutionKey, cts.Token);
                if (pipe is not null)
                {
                    await using (pipe)
                    {
                        // Handshake: tell the daemon this connection is a long-lived LSP session,
                        // not a one-shot tool call. After this frame the pipe carries raw LSP
                        // JSON-RPC (Content-Length framed) in both directions.
                        var handshake = new DaemonRequest(
                            Guid.NewGuid().ToString("N"), Tool: "", Args: new(), Format: "", Kind: "lsp");
                        await IpcProtocol.WriteMessageAsync(pipe, handshake, cts.Token);

                        Console.Error.WriteLine($"[Lsp] Proxying to shared host for '{solutionKey}'.");
                        proxied = true;
                        var started = System.Diagnostics.Stopwatch.StartNew();
                        var stdinToPipe = PumpAsync("editor-to-host", stdin, pipe, cts.Token);
                        var pipeToStdout = PumpAsync("host-to-editor", pipe, stdout, cts.Token);

                        // Which one ended is the whole diagnosis. "editor-to-host ended first"
                        // means the editor closed us — a window shutting, or the client deciding
                        // to restart — and nothing here is at fault. "host-to-editor ended first"
                        // means the daemon stopped talking or the write to the editor failed,
                        // which is this process's problem and looks identical from the outside.
                        var first = await Task.WhenAny(stdinToPipe, pipeToStdout);
                        var outcome = await first;
                        cts.Cancel();

                        var other = ReferenceEquals(first, stdinToPipe) ? pipeToStdout : stdinToPipe;
                        var trailing = await SettledAsync(other);

                        Console.Error.WriteLine(
                            $"[Lsp] Session ended after {started.Elapsed.TotalSeconds:F1}s: "
                            + $"{outcome.Label} stopped first ({outcome.Reason}), "
                            + $"{outcome.Bytes:N0} bytes; {trailing.Label} carried "
                            + $"{trailing.Bytes:N0} bytes. Exiting 0, so the editor will restart us.");
                    }
                    return 0;
                }
            }
            catch (OperationCanceledException)
            {
                return 0; // editor closed us during connect — clean exit
            }
            catch (Exception ex)
            {
                if (proxied)
                    return 0; // session already ran over the pipe; nothing to fall back to
                Console.Error.WriteLine($"[Lsp] Shared host connection failed ({ex.Message}); running LSP in-process.");
            }
            if (!proxied)
                Console.Error.WriteLine("[Lsp] Shared host unreachable; running LSP in-process.");
        }

        // In-process fallback: host the workspace and the LSP session in this process.
        string workingDir = solutionKey is not null
            ? Path.GetDirectoryName(solutionKey) ?? startPath
            : startPath;
        WorkspaceService.MaxCachedWorkspaces = settings.MaxWorkspaces;
        WorkspaceService.EnsureRegistered();

        // This process now hosts workspaces for a whole editor session, which is long enough to
        // reload; standby hosts are what make those reloads meet warm MSBuild processes.
        SharedBuildHost.EnableStandbys();
        bool useToon = string.Equals(settings.TableFormat, "toon", StringComparison.OrdinalIgnoreCase);
        IOutputFormatter formatter = useToon ? new ToonFormatter() : new MarkdownFormatter();
        await using var services = Daemon.ToolHostServices.Build(settings, formatter, workingDir);

        // The in-process fallback is its own host, so it watches the config itself. The running
        // session keeps the provider it initialized with; what a reload can move is everything
        // routed through LanguageRegistry.Current — which is nearly every handler — so the
        // rebuilt registry is published and the editor asked to re-pull. Replaced providers are
        // parked undisposed: disposing one would take the carried stores with it, and this
        // process exits with the session anyway.
        var replaced = new List<Microsoft.Extensions.DependencyInjection.ServiceProvider>();
        var currentProvider = services;

        // In-process host, so nothing else watches the connection-string sources either. The
        // registry instance is carried across config reloads, so this stays valid throughout.
        using var dbWatcher = DbConnectionWatcher.Start(
            workingDir, settings, services.GetRequiredService<DbConnectionRegistry>());

        using var configWatcher = Daemon.ConfigWatcher.Start(workingDir, [], settings, reload =>
        {
            dbWatcher?.UpdateSettings(reload.Settings);
            Console.Error.WriteLine(
                $"[Lsp] {RoslynSenseConfigLoader.FileName} changed: {string.Join("; ", reload.Changes)}. Applying.");
            bool toon = string.Equals(reload.Settings.TableFormat, "toon", StringComparison.OrdinalIgnoreCase);
            IOutputFormatter fmt = toon ? new ToonFormatter() : new MarkdownFormatter();
            // Build resolves the new LanguageRegistry as it finishes, which publishes it as
            // LanguageRegistry.Current — the swap the static handlers see.
            var fresh = Daemon.ToolHostServices.Build(reload.Settings, fmt, workingDir, carryFrom: currentProvider);
            replaced.Add(currentProvider);
            currentProvider = fresh;
            WorkspaceService.MaxCachedWorkspaces = reload.Settings.MaxWorkspaces;
            LspSessionRegistry.ScheduleRefresh(RefreshKind.All, "config-reload");
        });

        await LspSessionHost.RunAsync(stdin, stdout, services, cts.Token);
        await WorkspaceService.ShutdownAsync();
        return 0;
    }

    /// <summary>How one direction of the proxy ended, and how much it had carried by then.</summary>
    /// <remarks>
    /// The byte count is the part that turns a restart loop into a diagnosis. A direction that
    /// ends having carried nothing never had a working peer; one that ends mid-session after
    /// megabytes was working until something stopped it, and the two have nothing to do with each
    /// other.
    /// </remarks>
    private readonly record struct PumpOutcome(string Label, long Bytes, string Reason);

    /// <summary>The outcome once <paramref name="pump"/> settles, without re-throwing.</summary>
    private static async Task<PumpOutcome> SettledAsync(Task<PumpOutcome> pump)
    {
        try { return await pump; }
        catch (Exception ex) { return new PumpOutcome("(unknown)", 0, ex.GetType().Name); }
    }

    private static async Task<PumpOutcome> PumpAsync(
        string label, Stream from, Stream to, CancellationToken ct)
    {
        // Copied by hand rather than with CopyToAsync so the byte count survives the exception:
        // CopyToAsync reports nothing about how far it got, which is exactly what is wanted when
        // a session dies and the question is whether anything ever flowed.
        var buffer = new byte[32 * 1024];
        long bytes = 0;

        try
        {
            while (true)
            {
                int read = await from.ReadAsync(buffer, ct);
                if (read <= 0)
                    return new PumpOutcome(label, bytes, "the peer closed its end");

                await to.WriteAsync(buffer.AsMemory(0, read), ct);
                await to.FlushAsync(ct);
                bytes += read;
            }
        }
        catch (OperationCanceledException)
        {
            // The other direction finished first and cancelled us — the ordinary way a session ends.
            return new PumpOutcome(label, bytes, "cancelled once the other direction ended");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Named rather than swallowed: a session that ends here ends for everything, and
            // "the editor closed" and "the write failed" look identical from the outside.
            Console.Error.WriteLine($"[Lsp] The {label} pump stopped: {ex.GetType().Name}: {ex.Message}");
            return new PumpOutcome(label, bytes, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
