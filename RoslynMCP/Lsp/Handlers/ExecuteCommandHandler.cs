using System.Diagnostics;
using System.Text.Json;
using RoslynMCP.Lsp.Completion;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>workspace/executeCommand: workspace maintenance commands. Both end with an
/// <see cref="WorkspaceService.EvictAllAsync"/> so the next request reloads projects fresh
/// (new packages after restore, regenerated sources, changed project files).</summary>
internal static class ExecuteCommandHandler
{
    public const string RestoreCommand = "roslynSense.restore";
    public const string ReloadCommand = "roslynSense.reloadWorkspace";
    public const string BuildCommand = "roslynSense.build";

    /// <summary>Sent by the client right after a completion item is inserted: [contextId, itemIdentity].</summary>
    public const string CompletionAcceptedCommand = "roslynSense.completionAccepted";

    public static readonly string[] Commands =
        [RestoreCommand, ReloadCommand, BuildCommand, CompletionAcceptedCommand];

    public static async Task<object> ExecuteAsync(ExecuteCommandParams p, CancellationToken ct)
    {
        switch (p.Command)
        {
            case RestoreCommand:
                return await RestoreAsync(p, ct);

            case BuildCommand:
                return await BuildAsync(p, ct);

            case CompletionAcceptedCommand:
                RecordCompletionAccepted(p);
                return "";

            case ReloadCommand:
                await using (await ProgressReporter.BeginAsync("Reloading workspace", ct))
                {
                    AnalyzerDiagnosticCache.Clear();
                    await WorkspaceService.EvictAllAsync(ct);
                }
                await LspSessionRegistry.RequestRefreshAsync(RefreshKind.All, ct);
                return "Workspace reloaded.";

            default:
                return $"Unknown command '{p.Command}'.";
        }
    }

    private static void RecordCompletionAccepted(ExecuteCommandParams p)
    {
        if (p.Arguments is not { Length: >= 2 })
            return;

        string? contextId = p.Arguments[0].ValueKind == JsonValueKind.String ? p.Arguments[0].GetString() : null;
        string? identity = p.Arguments[1].ValueKind == JsonValueKind.String ? p.Arguments[1].GetString() : null;
        if (contextId is not null && identity is not null)
            CompletionStatistics.Record(contextId, identity);
    }

    /// <summary>Builds before a debug launch. Returns structured diagnostics so the client can
    /// surface them in Problems rather than as a wall of text.</summary>
    private static async Task<BuildResult> BuildAsync(ExecuteCommandParams p, CancellationToken ct)
    {
        string? projectPath = p.Arguments is [{ ValueKind: System.Text.Json.JsonValueKind.String } arg, ..]
            ? arg.GetString()
            : null;
        if (string.IsNullOrEmpty(projectPath))
            return new BuildResult(false, "No project to build.", [], []);

        string configuration = p.Arguments is [_, { ValueKind: System.Text.Json.JsonValueKind.String } second, ..]
            ? second.GetString() ?? "Debug"
            : "Debug";

        return await LaunchHandler.BuildAsync(projectPath, configuration, ct);
    }

    private static async Task<string> RestoreAsync(ExecuteCommandParams p, CancellationToken ct)
    {
        // Target: explicit argument (solution/project path from the client) or the loaded solution.
        string? target = p.Arguments is [{ ValueKind: System.Text.Json.JsonValueKind.String } arg, ..]
            ? arg.GetString()
            : WorkspaceService.TryGetMostRecentSolution()?.FilePath;
        if (string.IsNullOrEmpty(target) || !File.Exists(target))
            return "No solution or project to restore. Pass a path or open a document first.";

        var startInfo = new ProcessStartInfo("dotnet", $"restore \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        await using var progress = await ProgressReporter.BeginAsync(
            $"Restoring {Path.GetFileName(target)}", ct);

        using var process = Process.Start(startInfo);
        if (process is null)
            return "Failed to start dotnet restore.";

        string stdout = await process.StandardOutput.ReadToEndAsync(ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            string detail = (stderr.Length > 0 ? stderr : stdout).Trim();
            return $"dotnet restore failed (exit {process.ExitCode}): {Truncate(detail, 2000)}";
        }

        await WorkspaceService.EvictAllAsync(ct);
        return $"Restored '{Path.GetFileName(target)}' and reloaded the workspace.";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
