using System.Diagnostics;
using System.Text.Json;
using RoslynMCP.Languages;
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

    /// <summary>Pins the URL a project launches on: [projectPath, url?, profileName?].</summary>
    public const string SetLaunchUrlCommand = "roslynSense.setLaunchUrl";

    /// <summary>Sent by the client right after a completion item is inserted: [contextId, itemIdentity].</summary>
    public const string CompletionAcceptedCommand = "roslynSense.completionAccepted";

    /// <summary>
    /// Writes the code-behind method for an event attribute just committed in markup:
    /// [aspxUri, startTagOffset, attributeName, handlerName]. A completion item cannot carry an
    /// edit to another file, so the item asks for this instead and the method arrives through
    /// <c>workspace/applyEdit</c>.
    /// </summary>
    /// <remarks>
    /// Declared here because the string is protocol, but owned by the WebForms pack, which lists
    /// it in its <c>Capabilities.Commands</c>. That is what keeps it out of the <c>initialize</c>
    /// response — and therefore off the client's command palette — when the pack is off.
    /// </remarks>
    public const string GenerateEventHandlerCommand = "roslynSense.generateEventHandler";

    /// <summary>
    /// Brings every binding redirect in one <c>web.config</c> or <c>app.config</c> up to what the
    /// project ships: [configPath]. Invoked by the lens at the top of that file.
    /// </summary>
    public const string FixBindingRedirectsCommand = "roslynSense.fixBindingRedirects";

    /// <summary>The commands the server answers whatever languages are enabled. A pack's own
    /// commands are appended to these when capabilities are built.</summary>
    public static readonly string[] Commands =
    [
        RestoreCommand, ReloadCommand, BuildCommand, CompletionAcceptedCommand, SetLaunchUrlCommand,
        FixBindingRedirectsCommand,
    ];

    public static async Task<object> ExecuteAsync(
        ExecuteCommandParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        switch (p.Command)
        {
            case RestoreCommand:
                return await RestoreAsync(p, ct);

            case BuildCommand:
                return await BuildAsync(p, ct);

            case SetLaunchUrlCommand:
                return SetLaunchUrl(p);

            case FixBindingRedirectsCommand:
                return p.Arguments is [{ ValueKind: JsonValueKind.String } path, ..] &&
                    path.GetString() is { Length: > 0 } configPath
                        ? await BindingRedirectHandler.FixAllAsync(configPath, ct)
                        : "No config file to fix binding redirects in.";

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
                // A command carries no document, so a pack's own commands are dispatched by name
                // rather than resolved from a URI. Only the connection's enabled packs, because
                // a command it never saw advertised is one it must not be able to invoke.
                foreach (var pack in LanguageScope.Of(languages).Contributors<ILanguageCommandProvider>())
                {
                    if (pack.CanExecute(p.Command))
                        return await pack.ExecuteCommandAsync(p, ct);
                }

                return $"Unknown command '{p.Command}'.";
        }
    }

    /// <summary>
    /// Writes a project's launch URL into launchSettings.json, so an address the server derived
    /// becomes one the project states. [projectPath, url?, profileName?].
    /// </summary>
    private static string SetLaunchUrl(ExecuteCommandParams p)
    {
        string? Argument(int index) =>
            p.Arguments is { } args && args.Length > index &&
            args[index].ValueKind == JsonValueKind.String
                ? args[index].GetString()
                : null;

        string? projectPath = Argument(0);
        if (string.IsNullOrEmpty(projectPath) || !File.Exists(projectPath))
            return "No project to pin a launch URL for.";

        var projectDir = Path.GetDirectoryName(projectPath)!;
        var url = Argument(1);
        if (string.IsNullOrWhiteSpace(url))
        {
            var spec = Services.Run.RunConfigResolver.Resolve(projectPath);
            url = spec.Url
                ?? $"http://localhost:{Services.Run.RunConfigResolver.StablePort(projectPath)}";
        }

        var profile = Argument(2)
            ?? Services.Run.LaunchSettings.Load(projectDir)?.Select(null)?.Name
            ?? Path.GetFileNameWithoutExtension(projectPath);

        return Services.Run.LaunchSettings.SetApplicationUrl(projectDir, profile, url) is { } error
            ? error
            : $"'{profile}' now launches on {url}.";
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

        // [path, configuration, target] — "build" (the default), "rebuild" or "clean".
        string target = p.Arguments is [_, _, { ValueKind: System.Text.Json.JsonValueKind.String } third, ..]
            ? third.GetString() ?? "build"
            : "build";

        // [path, configuration, target, reportProgress] — false when the client shows its own.
        bool reportProgress = p.Arguments is not [_, _, _, { ValueKind: JsonValueKind.False }, ..];

        return await LaunchHandler.BuildAsync(projectPath, configuration, target, ct, reportProgress);
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
