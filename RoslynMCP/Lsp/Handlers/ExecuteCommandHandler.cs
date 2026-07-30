using System.Diagnostics;
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

    public static readonly string[] Commands = [RestoreCommand, ReloadCommand];

    public static async Task<string> ExecuteAsync(ExecuteCommandParams p, CancellationToken ct)
    {
        switch (p.Command)
        {
            case RestoreCommand:
                return await RestoreAsync(p, ct);

            case ReloadCommand:
                await WorkspaceService.EvictAllAsync(ct);
                return "Workspace reloaded.";

            default:
                return $"Unknown command '{p.Command}'.";
        }
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
