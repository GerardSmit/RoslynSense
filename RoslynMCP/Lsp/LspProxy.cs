using RoslynMCP.Config;
using RoslynMCP.Daemon;
using RoslynMCP.Services;

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

        var (config, _, _) = RoslynSenseConfigLoader.Load(startPath);
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
                        var stdinToPipe = PumpAsync("editor-to-host", stdin, pipe, cts.Token);
                        var pipeToStdout = PumpAsync("host-to-editor", pipe, stdout, cts.Token);
                        await Task.WhenAny(stdinToPipe, pipeToStdout);
                        cts.Cancel();
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
        bool useToon = string.Equals(settings.TableFormat, "toon", StringComparison.OrdinalIgnoreCase);
        IOutputFormatter formatter = useToon ? new ToonFormatter() : new MarkdownFormatter();
        await using var services = Daemon.ToolHostServices.Build(settings, formatter, workingDir);

        await LspSessionHost.RunAsync(stdin, stdout, services, cts.Token);
        await WorkspaceService.ShutdownAsync();
        return 0;
    }

    private static async Task PumpAsync(string label, Stream from, Stream to, CancellationToken ct)
    {
        try
        {
            await from.CopyToAsync(to, ct);
        }
        catch (OperationCanceledException)
        {
            // The other direction finished first and cancelled us — the ordinary way a session ends.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Named rather than swallowed: a session that ends here ends for everything, and
            // "the editor closed" and "the write failed" look identical from the outside.
            Console.Error.WriteLine($"[Lsp] The {label} pump stopped: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
