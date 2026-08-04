using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;
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

        // From the solution file as well as from Roslyn: a legacy project needs the
        // out-of-process build host to load at all, so asking only what Roslyn holds meant F5
        // on a WebForms project reported it was not in the solution it is plainly in.
        var projectPaths = SolutionProjectIndex.ProjectPaths();

        var targets = new List<LaunchTarget>(projectPaths.Count);
        foreach (var projectPath in projectPaths)
        {
            ct.ThrowIfCancellationRequested();
            targets.Add(Describe(projectPath, configuration, p.LaunchProfile));
        }

        return await Task.FromResult(targets
            .OrderByDescending(t => t.Runnable)
            .ThenBy(t => t.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    /// <summary>
    /// The launch target for the project a file belongs to.
    /// </summary>
    /// <remarks>
    /// What F5 needs to stop asking. The editor knows which file is in front of the user; the
    /// project that owns it is nearly always the one they meant to run, and making them pick it
    /// out of a list every time is the kind of question an IDE should answer for itself. Returns
    /// <c>null</c> rather than guessing when the file belongs to no project, so the caller can
    /// fall back to asking.
    /// </remarks>
    public static LaunchTarget? TargetForFile(TargetForFileParams p)
    {
        if (SolutionProjectIndex.ProjectForFile(p.FilePath ?? "") is not { } projectPath)
            return null;

        return Describe(
            projectPath,
            string.IsNullOrWhiteSpace(p.Configuration) ? "Debug" : p.Configuration,
            p.LaunchProfile);
    }

    /// <summary>What the machine offers for .NET Framework work, so the client can pick MSBuild
    /// over the dotnet CLI and explain a missing install instead of failing opaquely.</summary>
    public static ToolchainInfo Toolchain()
    {
        var info = NetFxToolchain.Info;
        return new ToolchainInfo(info.MsBuildPath, info.DesktopClr, info.PreferredIisExpress);
    }

    private static LaunchTarget Describe(
        string projectPath, string configuration, string? launchProfile = null)
    {
        var classification = ProjectClassifier.Classify(projectPath);
        string name = Path.GetFileNameWithoutExtension(projectPath);
        bool isNetFramework = classification.DebugRuntime == DebugRuntime.NetFramework;
        var profiles = DescribeProfiles(projectPath);

        if (!classification.IsRunnable)
        {
            return new LaunchTarget(
                projectPath, name, classification.Kind.ToString(), classification.TargetFramework,
                isNetFramework, classification.IsTestProject, Runnable: false,
                Program: null, Args: [], Cwd: null, Env: [], Url: null,
                Error: classification.IsTestProject
                    ? "Test project — run it from the Test Explorer."
                    : "Produces a library, so there is nothing to launch.",
                LaunchProfiles: profiles);
        }

        // A Framework target is launchable, but by the ICorDebug adapter rather than netcoredbg —
        // the client picks between them on IsNetFramework. What it does need is the toolchain:
        // without MSBuild there is nothing to build, and the failure would otherwise surface as a
        // missing executable. Only a legacy project needs it, though — an SDK-style project on
        // net48 is built by the dotnet CLI like any other.
        if (classification.BuildTool == BuildTool.VisualStudioMsBuild &&
            NetFxToolchain.Info.MsBuildPath.Length == 0)
        {
            return new LaunchTarget(
                projectPath, name, classification.Kind.ToString(), classification.TargetFramework,
                IsNetFramework: true, classification.IsTestProject, Runnable: false,
                Program: null, Args: [], Cwd: null, Env: [], Url: null,
                Error: "This is a legacy (non-SDK) project and Visual Studio's MSBuild was not found. " +
                       "Install Visual Studio or the Build Tools to build and debug it.",
                LaunchProfiles: profiles);
        }

        var spec = RunConfigResolver.Resolve(projectPath, configuration, launchProfile);
        if (!spec.CanRun)
        {
            // Usually "not built yet" — still a target, because the launch flow builds first.
            return new LaunchTarget(
                projectPath, name, classification.Kind.ToString(), classification.TargetFramework,
                isNetFramework, classification.IsTestProject, Runnable: true,
                Program: null, Args: [], Cwd: Path.GetDirectoryName(projectPath), Env: [],
                Url: null, Error: spec.Error, LaunchProfiles: profiles, LaunchProfile: launchProfile);
        }

        return new LaunchTarget(
            projectPath, name, classification.Kind.ToString(), classification.TargetFramework,
            isNetFramework, classification.IsTestProject, Runnable: true,
            spec.Executable, spec.Arguments.ToArray(), spec.WorkingDirectory,
            new Dictionary<string, string>(spec.Environment), spec.Url, Error: null,
            LaunchProfiles: profiles, LaunchProfile: spec.ProfileName,
            BrowseUrl: spec.BrowseUrl, LaunchBrowser: spec.LaunchBrowser);
    }

    /// <summary>
    /// The project's launch profiles. Only the launchable ones: a profile the server cannot start
    /// would appear in the client's run-configuration list and then fail when chosen.
    /// </summary>
    private static LaunchProfileDescriptor[] DescribeProfiles(string projectPath)
    {
        var projectDir = Path.GetDirectoryName(projectPath);
        if (projectDir is null || LaunchSettings.Load(projectDir) is not { } settings)
            return [];

        return [.. settings.Profiles
            .Where(p => p.IsLaunchable)
            .Select(p => new LaunchProfileDescriptor(
                p.Name, p.CommandName, p.ApplicationUrl, p.CommandLineArgs, p.LaunchBrowser,
                p.LaunchUrl, new Dictionary<string, string>(p.EnvironmentVariables)))];
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
        string projectPath, string configuration, CancellationToken ct) =>
        await BuildAsync(projectPath, configuration, "build", ct);

    /// <summary>
    /// Builds, rebuilds or cleans a project or a whole solution.
    /// </summary>
    /// <param name="reportProgress">
    /// False when the client is already showing progress for this build itself — which it does for
    /// a launch, because that one has to carry a Cancel button — so the two do not stack up.
    /// </param>
    public static async Task<BuildResult> BuildAsync(
        string projectPath, string configuration, string target, CancellationToken ct,
        bool reportProgress = true)
    {
        if (!File.Exists(projectPath))
            return new BuildResult(false, $"Project '{projectPath}' not found.", [], []);

        string verb = target switch
        {
            "rebuild" => "Rebuilding",
            "clean" => "Cleaning",
            _ => "Building",
        };

        await using var progress = reportProgress
            ? await ProgressReporter.BeginAsync(
                $"{verb} {Path.GetFileNameWithoutExtension(projectPath)}", ct)
            : null;

        // The dotnet CLI cannot build a non-SDK project at all — it needs Visual Studio's MSBuild,
        // which also has to run with the VS environment set for the legacy targets to resolve.
        // The reverse is just as true: an SDK-style project targeting net48 belongs to the dotnet
        // CLI, and handing it to VS MSBuild fails to resolve Microsoft.NET.Sdk.
        bool isLegacy = NeedsVisualStudioMsBuild(projectPath);
        string? msbuild = isLegacy ? MsBuildLocator.FindMsBuild() : null;

        if (isLegacy && msbuild is null)
        {
            return new BuildResult(false,
                "This is a legacy (non-SDK) project and Visual Studio's MSBuild was not found. " +
                "Install Visual Studio or the Build Tools for Visual Studio.", [], []);
        }

        var startInfo = msbuild is not null
            ? new ProcessStartInfo(msbuild,
                $"\"{projectPath}\" /nologo /v:minimal /p:Configuration={configuration} " +
                $"/t:{MsBuildTarget(target)}")
            : new ProcessStartInfo("dotnet", DotnetArguments(projectPath, configuration, target));

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WorkingDirectory = Path.GetDirectoryName(projectPath);

        if (msbuild is not null)
            MsBuildLocator.SetVsEnvironment(startInfo, msbuild);

        using var process = Process.Start(startInfo);
        if (process is null)
            return new BuildResult(false, "Failed to start the build.", [], []);

        // Cancelling has to reach MSBuild itself: abandoning the read would leave a compiler
        // running against the same output the next build wants to write.
        await using var cancellation = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone, which is the state the cancellation wanted.
            }
        });

        // Read line by line rather than to the end, so the progress notification says which
        // project is compiling instead of sitting on one title until the build is over.
        var output = new StringBuilder();
        void Capture(string? line)
        {
            if (line is null)
                return;

            lock (output)
                output.AppendLine(line);

            if (DescribeBuildLine(line) is { } step)
                progress?.Report(step);
        }

        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        var messages = ParseBuildOutput(output.ToString()).ToList();
        var errors = messages.Where(m => m.IsError).Select(m => m.Message).ToArray();
        var warnings = messages.Where(m => !m.IsError).Select(m => m.Message).ToArray();

        bool success = process.ExitCode == 0;
        string done = target switch
        {
            "rebuild" => "Rebuilt",
            "clean" => "Cleaned",
            _ => "Built",
        };

        return new BuildResult(
            success,
            success
                ? $"{done} {Path.GetFileName(projectPath)} ({warnings.Length} warning(s))."
                : $"{verb[..^3]} failed with {errors.Length} error(s).",
            errors,
            warnings);
    }

    /// <summary>
    /// What a line of build output means for a progress notification, or null when it means
    /// nothing the user needs to watch scroll past.
    /// </summary>
    private static string? DescribeBuildLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return null;

        if (trimmed.StartsWith("Determining projects to restore", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Restoring", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Restored", StringComparison.OrdinalIgnoreCase))
        {
            return "Restoring packages";
        }

        // MSBuild announces each finished assembly as "Project -> path\Project.dll".
        int arrow = trimmed.IndexOf(" -> ", StringComparison.Ordinal);
        return arrow > 0 ? $"Built {trimmed[..arrow].Trim()}" : null;
    }

    private static string MsBuildTarget(string target) => target switch
    {
        "rebuild" => "Rebuild",
        "clean" => "Clean",
        _ => "Build",
    };

    private static string DotnetArguments(string path, string configuration, string target) =>
        target == "clean"
            ? $"clean \"{path}\" -c {configuration} --nologo"
            : $"build \"{path}\" -c {configuration} --nologo " +
              $"-consoleloggerparameters:NoSummary" +
              (target == "rebuild" ? " -t:Rebuild" : string.Empty);

    /// <summary>
    /// Whether a build target needs Visual Studio's MSBuild rather than the dotnet CLI.
    /// </summary>
    /// <remarks>
    /// A solution has no build style of its own; it takes the strictest of its projects', because
    /// one legacy project in it is enough for the dotnet CLI to fail on the whole thing.
    /// </remarks>
    private static bool NeedsVisualStudioMsBuild(string path)
    {
        if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return SolutionFileService.Read(path).Any(node =>
                node is { IsFolder: false, Path: { Length: > 0 } project } &&
                ProjectClassifier.Classify(project).BuildTool == BuildTool.VisualStudioMsBuild);
        }

        return ProjectClassifier.Classify(path).BuildTool == BuildTool.VisualStudioMsBuild;
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
