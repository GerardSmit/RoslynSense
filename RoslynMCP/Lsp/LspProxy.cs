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

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Stream stdin = Console.OpenStandardInput();
        Stream stdout = Console.OpenStandardOutput();

        var (config, _, _) = RoslynSenseConfigLoader.Load(startPath);
        var settings = EffectiveSettings.Resolve(Array.Empty<string>(), config, out _);

        if (settings.SharedHost && solutionKey is not null)
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
                    var stdinToPipe = PumpAsync(stdin, pipe, cts.Token);
                    var pipeToStdout = PumpAsync(pipe, stdout, cts.Token);
                    await Task.WhenAny(stdinToPipe, pipeToStdout);
                    cts.Cancel();
                }
                return 0;
            }
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
        await WorkspaceService.EvictAllAsync();
        return 0;
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
    {
        try
        {
            await from.CopyToAsync(to, ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Peer closed — session over.
        }
    }
}
