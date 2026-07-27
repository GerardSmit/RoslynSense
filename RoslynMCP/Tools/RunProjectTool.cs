using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Services.Run;

namespace RoslynMCP.Tools;

/// <summary>
/// Starting and stopping applications. Marked <see cref="InProcessOnlyAttribute"/> so a launched
/// app belongs to the chat that started it and is torn down with that client, rather than being
/// shared through the host across chats.
/// </summary>
[McpServerToolType]
[InProcessOnly]
public static class RunProjectTool
{
    [McpServerTool, Description(
        "Build and run a project, leaving it running: ASP.NET Core and .NET console apps launch " +
        "directly, legacy ASP.NET sites launch under IIS Express. Builds first by default, like " +
        "Visual Studio — set build=false to launch existing output. For web projects this waits " +
        "until the port accepts connections and returns the URL and PID. Use GetProjectOutput to " +
        "read its output, DebugAttach with the PID to debug it, and StopProject to stop it.")]
    public static async Task<string> RunProject(
        [Description("Path to the .csproj to run.")]
        string projectPath,
        AppRunService runner,
        AppSessionStore store,
        BuildWarningsStore warningsStore,
        IOutputFormatter fmt,
        [Description("Build configuration to build and launch. Defaults to Debug.")]
        string configuration = "Debug",
        [Description("launchSettings.json profile name. Omit to use the first Project profile.")]
        string? profile = null,
        [Description("Extra environment variables as 'NAME=VALUE' pairs, semicolon-separated.")]
        string? environment = null,
        [Description("Build before launching. Defaults to true; set false to launch existing output.")]
        bool build = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return "Error: 'projectPath' is required.";

            var resolved = PathHelper.ResolveCsprojPath(projectPath);
            if (resolved is null)
                return $"Error: Could not find a .csproj for '{projectPath}'.";

            if (build)
            {
                // Launching stale output silently is the worst failure mode here: the app appears
                // to run but does not contain the change under test.
                var (built, output) = await BuildProjectTool.TryBuildAsync(
                    resolved, configuration, warningsStore, cancellationToken: cancellationToken);

                if (!built)
                    return "The build failed, so nothing was started.\n\n" + output;
            }

            var outcome = await runner.StartAsync(
                resolved, configuration, profile, ParseEnvironment(environment), cancellationToken);

            if (!outcome.Succeeded)
                return $"Error: {outcome.Error}";

            return Describe(outcome.Session!, fmt);
        }
        catch (OperationCanceledException)
        {
            return "Error: Starting the project was cancelled.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "Stop a running project by session ID, by project path, or pass 'all' to stop everything " +
        "started in this chat. Kills the whole process tree.")]
    public static async Task<string> StopProject(
        [Description("A session ID from RunProject, a .csproj path, or 'all'.")]
        string target,
        AppSessionStore store,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(target))
                return "Error: 'target' is required.";

            var sessions = ResolveTargets(target, store);
            if (sessions.Count == 0)
                return $"No running session matched '{target}'.";

            var stopped = new List<string>();
            foreach (var session in sessions)
            {
                if (await AppRunService.StopAsync(session))
                    stopped.Add($"{session.Id} (pid {session.Pid})");
                store.Remove(session.Id);
            }

            return stopped.Count == 0
                ? "Matched sessions had already exited; they have been cleared."
                : "Stopped " + string.Join(", ", stopped) + ".";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "List the applications started in this chat, with their state, PID, URL and uptime.")]
    public static string ListRunningProjects(AppSessionStore store)
    {
        var sessions = store.All();
        if (sessions.Count == 0)
            return "No applications have been started in this chat.";

        var sb = new StringBuilder();
        sb.AppendLine("| Session | Project | State | PID | URL | Uptime |");
        sb.AppendLine("|---------|---------|-------|-----|-----|--------|");

        foreach (var session in sessions)
        {
            var state = session.State == AppSessionState.Exited
                ? $"exited ({session.ExitCode?.ToString() ?? "?"})"
                : session.State.ToString().ToLowerInvariant();

            sb.AppendLine(
                $"| {session.Id} | {Path.GetFileNameWithoutExtension(session.ProjectPath)} | {state} | " +
                $"{(AppSessionStore.IsLive(session) ? session.Pid.ToString() : "-")} | " +
                $"{session.Url ?? "-"} | {FormatDuration(session.Uptime)} |");
        }

        return sb.ToString();
    }

    [McpServerTool, Description(
        "Read the captured stdout/stderr of a running or exited project started in this chat.")]
    public static string GetProjectOutput(
        [Description("Session ID from RunProject.")]
        string sessionId,
        AppSessionStore store,
        [Description("How many trailing lines to return. Defaults to 100.")]
        int lines = 100)
    {
        if (store.Get(sessionId) is not { } session)
            return $"Error: No session '{sessionId}'. Use ListRunningProjects to see the available IDs.";

        var output = session.Tail(Math.Max(1, lines));
        if (output.Length == 0)
            return $"'{sessionId}' has produced no output yet.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {sessionId} — {session.State.ToString().ToLowerInvariant()}");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.Append(output);
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static List<AppSession> ResolveTargets(string target, AppSessionStore store)
    {
        if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
            return [.. store.All()];

        if (store.Get(target) is { } byId)
            return [byId];

        var resolved = PathHelper.ResolveCsprojPath(target);
        return resolved is null ? [] : [.. store.LiveFor(resolved)];
    }

    private static string Describe(AppSession session, IOutputFormatter fmt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Started {Path.GetFileNameWithoutExtension(session.ProjectPath)} — session `{session.Id}`");
        sb.AppendLine();
        sb.AppendLine($"- **PID**: {session.Pid}");
        if (session.Url is not null)
            sb.AppendLine($"- **URL**: {session.Url}");
        sb.AppendLine($"- **Runtime**: {(session.DebugRuntime == DebugRuntime.NetFramework ? ".NET Framework" : "CoreCLR")}");
        sb.AppendLine();

        // A process that is already gone means startup failed; its output is the diagnosis.
        if (!AppSessionStore.IsLive(session) || session.Process.HasExited)
        {
            sb.AppendLine("The process exited immediately. Output:");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.Append(session.Tail(40));
            sb.AppendLine("```");
            return sb.ToString();
        }

        fmt.AppendHints(sb,
            $"Use GetProjectOutput with '{session.Id}' to read its output",
            $"Use StopProject with '{session.Id}' when finished",
            "Use DebugAttach with the PID above to debug it");

        return sb.ToString();
    }

    private static Dictionary<string, string>? ParseEnvironment(string? environment)
    {
        if (string.IsNullOrWhiteSpace(environment))
            return null;

        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in environment.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator > 0)
                parsed[entry[..separator].Trim()] = entry[(separator + 1)..].Trim();
        }

        return parsed.Count > 0 ? parsed : null;
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalSeconds < 60
        ? $"{duration.TotalSeconds:0}s"
        : duration.TotalMinutes < 60
            ? $"{duration.TotalMinutes:0}m"
            : $"{duration.TotalHours:0.0}h";
}
