using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Services.HotReload;

namespace RoslynMCP.Tools;

/// <summary>
/// Edit-and-Continue against a running application.
/// </summary>
/// <remarks>
/// <c>[InProcessOnly]</c> because an apply can only reach apps this process prepared: the agent
/// connects to the pipe named in the environment at launch, and <c>RunProject</c> is in-process
/// too, so the launcher and the applier have to be the same process. The editor's own launches go
/// through the daemon's copy of the same machinery via <c>roslynSense/hotReloadApply</c>.
/// </remarks>
[McpServerToolType]
[InProcessOnly]
public static class HotReloadTool
{
    [McpServerTool, Description(
        "Apply source edits to an already-running app without restarting it — real " +
        "Edit-and-Continue, not a rebuild. Opens a hot reload session on first use, capturing the " +
        "built output as the baseline. Reports rude edits (signature changes, new generics) that " +
        "cannot be applied and need a restart. The app must have been started with hot reload " +
        "enabled (RunProject with hotReload=true) for .NET Core, or be under a .NET Framework " +
        "debug session.")]
    public static async Task<string> ApplyHotReload(
        [Description("Path to the .csproj whose edits should be applied.")]
        string projectPath,
        IOutputFormatter fmt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = PathHelper.ResolveCsprojPath(projectPath);
            if (resolved is null)
                return $"Error: Could not find a .csproj for '{projectPath}'.";

            var session = HotReloadService.Get(resolved);
            if (session is null)
            {
                var (started, message) = await HotReloadService.StartAsync(resolved, cancellationToken);
                if (started is null)
                    return $"Error: {message}";
                session = started;
            }

            var outcome = await session.ApplyAsync(cancellationToken);
            return Describe(outcome, fmt);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "Close the hot reload session for a project, dropping its baseline. The next apply " +
        "starts from the built output again.")]
    public static string StopHotReload(
        [Description("Path to the .csproj, or 'all'.")]
        string projectPath)
    {
        try
        {
            if (projectPath.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                HotReloadService.StopAll();
                return "Closed every hot reload session.";
            }

            var resolved = PathHelper.ResolveCsprojPath(projectPath);
            if (resolved is null)
                return $"Error: Could not find a .csproj for '{projectPath}'.";

            if (HotReloadService.Get(resolved) is not { } session)
                return "No hot reload session is open for this project.";

            session.Stop();
            return "Closed the hot reload session.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static string Describe(HotReloadOutcome outcome, IOutputFormatter fmt)
    {
        var sb = new StringBuilder();
        sb.AppendLine(outcome.Ok ? "# Hot reload applied" : "# Hot reload did not apply");
        sb.AppendLine();
        sb.AppendLine(outcome.Summary);

        if (outcome.Diagnostics.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("| Severity | ID | Location | Message |");
            sb.AppendLine("|----------|----|----------|---------|");
            foreach (var diagnostic in outcome.Diagnostics)
            {
                string where = diagnostic.FilePath.Length > 0
                    ? $"{Path.GetFileName(diagnostic.FilePath)}:{diagnostic.Line}"
                    : "-";
                sb.AppendLine($"| {diagnostic.Severity} | {diagnostic.Id} | {where} | {diagnostic.Message} |");
            }
        }

        if (outcome.Errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Targets that rejected the update:");
            foreach (var error in outcome.Errors)
                sb.AppendLine($"- {error}");
        }

        if (!outcome.Ok)
        {
            fmt.AppendHints(sb,
                "A rude edit needs a restart: use StopProject then RunProject",
                "Nothing running? Start with RunProject and hotReload=true");
        }

        return sb.ToString();
    }
}
