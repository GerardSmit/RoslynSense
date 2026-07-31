using System.Diagnostics;
using System.Text.RegularExpressions;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.Run;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// Everything the editor needs to launch and debug the user's own app: where the debug adapter
/// is, what can be launched, and a build that reports structured errors.
///
/// The adapter itself is netcoredbg in its DAP mode (<c>--interpreter=vscode</c>), so the
/// editor talks to a real debugger directly and we contribute no adapter code — watch windows,
/// conditional breakpoints, and setVariable all come from netcoredbg.
/// </summary>
internal static partial class LaunchHandler
{
    public static async Task<DebuggerPathResult> DebuggerPathAsync(CancellationToken ct)
    {
        try
        {
            bool cached = DebuggerService.HasCachedNetcoredbg();

            await using var progress = cached
                ? null
                : await ProgressReporter.BeginAsync("Downloading .NET debugger", ct);

            string? path = await DebuggerService.FindOrProvisionNetcoredbgAsync(ct);
            return path is null
                ? new DebuggerPathResult(null, false,
                    "netcoredbg was not found and could not be downloaded. Install it and put it " +
                    "on PATH, or set 'roslynSense.debuggerPath'.")
                : new DebuggerPathResult(path, !cached, null);
        }
        catch (Exception ex)
        {
            return new DebuggerPathResult(null, false, ex.Message);
        }
    }

    /// <summary>
    /// Every project in the loaded solution, annotated with how it would launch. Non-runnable
    /// projects are returned too, with a reason — a picker that silently omits a project the
    /// user is looking for is worse than one that explains itself.
    /// </summary>
    public static async Task<LaunchTarget[]> LaunchTargetsAsync(
        LaunchTargetsParams p, CancellationToken ct)
    {
        string configuration = string.IsNullOrWhiteSpace(p.Configuration) ? "Debug" : p.Configuration;

        var solution = WorkspaceService.TryGetMostRecentSolution();
        var projectPaths = solution?.Projects
            .Select(project => project.FilePath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        var targets = new List<LaunchTarget>(projectPaths.Count);
        foreach (var projectPath in projectPaths)
        {
            ct.ThrowIfCancellationRequested();
            targets.Add(Describe(projectPath, configuration));
        }

        return await Task.FromResult(targets
            .OrderByDescending(t => t.Runnable)
            .ThenBy(t => t.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private static LaunchTarget Describe(string projectPath, string configuration)
    {
        var classification = ProjectClassifier.Classify(projectPath);
        string name = Path.GetFileNameWithoutExtension(projectPath);
        bool isNetFramework = classification.DebugRuntime == DebugRuntime.NetFramework;

        if (!classification.IsRunnable)
        {
            return new LaunchTarget(
                projectPath, name, classification.Kind.ToString(), classification.TargetFramework,
                isNetFramework, classification.IsTestProject, Runnable: false,
                Program: null, Args: [], Cwd: null, Env: [], Url: null,
                Error: classification.IsTestProject
                    ? "Test project — run it from the Test Explorer."
                    : "Produces a library, so there is nothing to launch.");
        }

        // netcoredbg cannot debug .NET Framework. Say so here rather than letting the adapter
        // fail after the session starts, which reads as a broken debugger.
        if (isNetFramework)
        {
            return new LaunchTarget(
                projectPath, name, classification.Kind.ToString(), classification.TargetFramework,
                IsNetFramework: true, classification.IsTestProject, Runnable: false,
                Program: null, Args: [], Cwd: null, Env: [], Url: null,
                Error: ".NET Framework projects are not supported by this debugger yet. " +
                       "Ask the AI assistant to start a debug session instead — it uses ICorDebug.");
        }

        var spec = RunConfigResolver.Resolve(projectPath, configuration);
        if (!spec.CanRun)
        {
            // Usually "not built yet" — still a target, because the launch flow builds first.
            return new LaunchTarget(
                projectPath, name, classification.Kind.ToString(), classification.TargetFramework,
                isNetFramework, classification.IsTestProject, Runnable: true,
                Program: null, Args: [], Cwd: Path.GetDirectoryName(projectPath), Env: [],
                Url: null, Error: spec.Error);
        }

        return new LaunchTarget(
            projectPath, name, classification.Kind.ToString(), classification.TargetFramework,
            isNetFramework, classification.IsTestProject, Runnable: true,
            spec.Executable, spec.Arguments.ToArray(), spec.WorkingDirectory,
            new Dictionary<string, string>(spec.Environment), spec.Url, Error: null);
    }

    /// <summary>
    /// Attachable .NET processes. Anything this server launched is listed first and carries its
    /// project name, so the common case ("attach to the app I just started") does not require
    /// recognising a PID.
    /// </summary>
    public static AttachTarget[] AttachTargets()
    {
        var launched = RunningProcessRegistry.List()
            .ToDictionary(e => e.Pid, e => e);

        var targets = new List<AttachTarget>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                    continue;

                bool known = launched.TryGetValue(process.Id, out var entry);
                if (!known && !LooksLikeDotNet(process.ProcessName))
                    continue;

                targets.Add(new AttachTarget(
                    process.Id,
                    process.ProcessName,
                    known ? Path.GetFileNameWithoutExtension(entry!.ProjectPath) : null,
                    known ? entry!.Url : null));
            }
            catch
            {
                // Process exited or is not ours to inspect.
            }
            finally
            {
                process.Dispose();
            }
        }

        return targets
            .OrderByDescending(t => t.ProjectName is not null)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool LooksLikeDotNet(string processName) =>
        processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
        processName.EndsWith(".Server", StringComparison.OrdinalIgnoreCase) ||
        processName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
        processName.Contains("iisexpress", StringComparison.OrdinalIgnoreCase) ||
        processName.Contains("w3wp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a project and returns its diagnostics structured, so the client can put them in
    /// the Problems panel instead of dumping compiler output into a message box.
    /// </summary>
    public static async Task<BuildResult> BuildAsync(
        string projectPath, string configuration, CancellationToken ct)
    {
        if (!File.Exists(projectPath))
            return new BuildResult(false, $"Project '{projectPath}' not found.", [], []);

        await using var progress = await ProgressReporter.BeginAsync(
            $"Building {Path.GetFileNameWithoutExtension(projectPath)}", ct);

        var startInfo = new ProcessStartInfo("dotnet",
            $"build \"{projectPath}\" -c {configuration} --nologo -consoleloggerparameters:NoSummary")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(projectPath),
        };

        using var process = Process.Start(startInfo);
        if (process is null)
            return new BuildResult(false, "Failed to start dotnet build.", [], []);

        string stdout = await process.StandardOutput.ReadToEndAsync(ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var messages = ParseBuildOutput(stdout + "\n" + stderr).ToList();
        var errors = messages.Where(m => m.IsError).Select(m => m.Message).ToArray();
        var warnings = messages.Where(m => !m.IsError).Select(m => m.Message).ToArray();

        bool success = process.ExitCode == 0;
        return new BuildResult(
            success,
            success
                ? $"Built {Path.GetFileName(projectPath)} ({warnings.Length} warning(s))."
                : $"Build failed with {errors.Length} error(s).",
            errors,
            warnings);
    }

    private static IEnumerable<(bool IsError, BuildMessage Message)> ParseBuildOutput(string output)
    {
        foreach (Match match in MsBuildDiagnostic().Matches(output))
        {
            yield return (
                match.Groups["severity"].Value == "error",
                new BuildMessage(
                    match.Groups["file"].Value,
                    int.TryParse(match.Groups["line"].Value, out var line) ? line : 0,
                    int.TryParse(match.Groups["column"].Value, out var column) ? column : 0,
                    match.Groups["code"].Value,
                    match.Groups["message"].Value.Trim()));
        }
    }

    /// <summary>MSBuild's canonical diagnostic format: file(line,col): severity CODE: message.</summary>
    [GeneratedRegex(
        @"^(?<file>[^\r\n(]+)\((?<line>\d+),(?<column>\d+)\):\s*(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+):\s*(?<message>[^\r\n]+)",
        RegexOptions.Multiline)]
    private static partial Regex MsBuildDiagnostic();
}
