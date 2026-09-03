using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.HotReload;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// The editor's side of Edit-and-Continue.
/// </summary>
/// <remarks>
/// The session lives in the daemon rather than in the extension, so the baseline is shared: the
/// user saving a file and the AI calling <c>ApplyHotReload</c> diff against the same one. Two
/// independent baselines would each resend the other's changes.
/// </remarks>
internal static class HotReloadHandler
{
    public static async Task<HotReloadResultDto> StartAsync(HotReloadParams p, CancellationToken ct)
    {
        if (Resolve(p.ProjectPath) is not { } project)
            return Failed($"Could not find a .csproj for '{p.ProjectPath}'.");

        var (session, message) = await HotReloadService.StartAsync(project, ct);
        return new HotReloadResultDto(session is not null, message, [], [], []);
    }

    public static async Task<HotReloadResultDto> ApplyAsync(HotReloadParams p, CancellationToken ct)
    {
        if (Resolve(p.ProjectPath) is not { } project)
            return Failed($"Could not find a .csproj for '{p.ProjectPath}'.");

        // Applying without a session would diff against nothing and report the whole project as
        // changed, so the first apply opens one rather than refusing.
        var session = HotReloadService.Get(project);
        bool openedNow = session is null;
        if (session is null)
        {
            var (started, message) = await HotReloadService.StartAsync(project, ct);
            if (started is null)
                return Failed(message);
            session = started;
        }

        var outcome = await session.ApplyAsync(ct);

        // A lazily-opened session took the already-edited source as its baseline, so the edit
        // that prompted this apply may be invisible to it. F5 opens the session at launch to
        // avoid this; when that did not happen, say what "no changes" actually means.
        if (openedNow && outcome.Ok && outcome.AppliedTo.Count == 0)
        {
            outcome = outcome with
            {
                Summary = outcome.Summary +
                    " Note: the session was opened by this apply, so edits made before it " +
                    "cannot be detected — restart the app if the edit did not land.",
            };
        }

        return new HotReloadResultDto(
            outcome.Ok,
            outcome.Summary,
            [.. outcome.Diagnostics.Select(d => new HotReloadDiagnosticDto(
                d.Id, d.Message, d.Severity, d.FilePath, d.Line))],
            [.. outcome.AppliedTo],
            [.. outcome.Errors]);
    }

    public static HotReloadResultDto Stop(HotReloadParams p)
    {
        if (Resolve(p.ProjectPath) is not { } project)
            return Failed($"Could not find a .csproj for '{p.ProjectPath}'.");

        if (HotReloadService.Get(project) is not { } session)
            return new HotReloadResultDto(true, "No hot reload session was open.", [], [], []);

        session.Stop();
        return new HotReloadResultDto(true, "Closed the hot reload session.", [], [], []);
    }

    public static HotReloadStatusDto Status() => new(
        [.. HotReloadService.OpenSessions],
        [.. HotReloadAgentServer.Instance.Targets.Select(t =>
            new HotReloadTargetDto(t.Name, t.ProcessId, t.Runtime))]);

    /// <summary>
    /// What to add to a launch so the started process can be hot reloaded.
    /// </summary>
    public static HotReloadEnvironmentDto Environment()
    {
        if (HotReloadLauncher.FindAgent() is not { } agent)
        {
            return new HotReloadEnvironmentDto(false, [],
                "The hot reload agent was not found beside the tool, so edits cannot be applied " +
                "to a running .NET Core app. .NET Framework hot reload goes through a debug " +
                "session instead and needs nothing here.");
        }

        return new HotReloadEnvironmentDto(true, new Dictionary<string, string>
        {
            ["DOTNET_MODIFIABLE_ASSEMBLIES"] = "debug",
            ["DOTNET_STARTUP_HOOKS"] = agent,
            [HotReloadAgentServer.PipeVariableName] = HotReloadAgentServer.Instance.PipeName,
        }, "");
    }

    private static string? Resolve(string projectPath) => PathHelper.ResolveCsprojPath(projectPath);

    private static HotReloadResultDto Failed(string message) => new(false, message, [], [], []);
}
