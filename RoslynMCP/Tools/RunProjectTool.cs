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
        [Description("Start the app so ApplyHotReload can patch it later without a restart. " +
                     "Has to be decided here: the runtime reads the settings only at startup.")]
        bool hotReload = false,
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
                resolved, configuration, profile, ParseEnvironment(environment), cancellationToken,
                hotReload);

            if (!outcome.Succeeded)
                return $"Error: {outcome.Error}";

            return Describe(outcome.Session!, fmt, hotReload);
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
        "started in this chat. Kills the whole process tree. A PID also works, which is how to " +
        "stop an app the user started in the editor — only do that when the user asks.")]
    public static async Task<string> StopProject(
        [Description("A session ID from RunProject, a .csproj path, a PID, or 'all'.")]
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
            {
                // Not ours: the registry owns the ones the editor or another chat started, and
                // it only accepts PIDs it knows about.
                if (int.TryParse(target, out int pid))
                    return RunningProcessRegistry.Kill(pid);

                return $"No running session matched '{target}'.";
            }

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
        "List the applications started in this chat, with their state, PID, URL and uptime — " +
        "plus the ones running outside it, started by the user in the editor or by another chat.")]
    public static string ListRunningProjects(AppSessionStore store)
    {
        var sessions = store.All();

        // Everything in the machine-wide registry that this chat did not start: the editor's own
        // F5 launches, and other chats'. Without these, "the app I have running" is invisible
        // here and the model starts a second copy on the same port.
        var elsewhere = RunningProcessRegistry.List()
            .Where(e => e.OwnerPid != Environment.ProcessId)
            .ToList();

        if (sessions.Count == 0 && elsewhere.Count == 0)
            return "No applications are running.";

        var sb = new StringBuilder();

        if (sessions.Count > 0)
        {
            sb.AppendLine("## Started in this chat");
            sb.AppendLine();
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
            sb.AppendLine();
        }

        if (elsewhere.Count > 0)
        {
            sb.AppendLine("## Running outside this chat");
            sb.AppendLine();
            sb.AppendLine("| Started by | Project | PID | URL | Uptime |");
            sb.AppendLine("|------------|---------|-----|-----|--------|");

            foreach (var entry in elsewhere)
            {
                sb.AppendLine(
                    $"| {(entry.SessionId.StartsWith("editor-", StringComparison.Ordinal) ? "the user (editor)" : "another chat")} | " +
                    $"{Path.GetFileNameWithoutExtension(entry.ProjectPath)} | {entry.Pid} | " +
                    $"{entry.Url ?? "-"} | {FormatDuration(DateTime.UtcNow - entry.StartedAtUtc)} |");
            }
            sb.AppendLine();
            sb.AppendLine(
                "These are not this chat's to restart: read them with DebugAttach on the PID, " +
                "and stop one with StopProject on its PID only if the user asks.");
        }

        return sb.ToString();
    }

    [McpServerTool, Description(
        "Read the captured stdout/stderr of a running or exited project started in this chat, or " +
        "of one running outside it — pass the PID from ListRunningProjects.")]
    public static string GetProjectOutput(
        [Description("Session ID from RunProject, or a PID for an app started outside this chat.")]
        string sessionId,
        AppSessionStore store,
        [Description("How many trailing lines to return. Defaults to 100.")]
        int lines = 100)
    {
        if (store.Get(sessionId) is not { } session)
        {
            // An app the editor launched: its output reaches the daemon as debug-adapter events
            // and is logged beside the registry, since there is no AppSession here to hold it.
            if (int.TryParse(sessionId, out int pid))
            {
                string logged = ProcessOutputLog.Tail(pid, Math.Max(1, lines));
                return logged.Length == 0
                    ? $"No output has been captured for pid {pid}. Apps started outside this chat " +
                      "are only captured while an editor is connected to the shared host."
                    : $"**pid {pid}**\n\n```\n{logged}\n```";
            }

            return $"Error: No session '{sessionId}'. Use ListRunningProjects to see the available IDs.";
        }

        var output = session.Tail(Math.Max(1, lines));
        if (output.Length == 0)
            return $"'{sessionId}' has produced no output yet.";

        var sb = new StringBuilder();
        sb.AppendLine($"**{sessionId} — {session.State.ToString().ToLowerInvariant()}**");
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

    private static string Describe(AppSession session, IOutputFormatter fmt, bool hotReload = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Started {Path.GetFileNameWithoutExtension(session.ProjectPath)} — session `{session.Id}`**");
        sb.AppendLine();
        sb.AppendLine($"- **PID**: {session.Pid}");
        if (session.Url is not null)
            sb.AppendLine($"- **URL**: {session.Url}");
        sb.AppendLine($"- **Runtime**: {(session.DebugRuntime == DebugRuntime.NetFramework ? ".NET Framework" : "CoreCLR")}");
        if (hotReload)
        {
            // What actually opened rather than the request flag: the launcher may not have found
            // the agent, and claiming an apply path that is not there sends the user debugging
            // their edit.
            sb.AppendLine(session.DebugRuntime == DebugRuntime.NetFramework
                ? "- **Hot reload**: unavailable — .NET Framework applies edits through a debug " +
                  "session, so use DebugAttach and then ApplyHotReload"
                : session.HotReloadOpen
                    ? "- **Hot reload**: enabled — use ApplyHotReload after editing"
                    : "- **Hot reload**: requested, but no session opened — the hot reload agent " +
                      "was not found beside the tool");
        }
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

        // A launched app belongs to the process that started it and is killed when that process
        // exits. In a chat that is the whole conversation; from the one-shot CLI it is the next
        // few milliseconds, so the session handles below would name something already dead.
        if (CliRunner.IsOneShot)
        {
            sb.AppendLine(
                "**This was started from `--cli`, which exits immediately — the app is being " +
                "stopped with it.** Run it from an MCP session, or start it yourself with:");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine($"{session.CommandLine}");
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
