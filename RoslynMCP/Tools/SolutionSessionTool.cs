using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Services.Designers;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class SolutionSessionTool
{
    [McpServerTool, Description(
        "Open a solution: load its projects into the workspace, report each project's runtime and " +
        "run kind, report the .NET Framework toolchain (MSBuild, IIS Express, SqlMetal), and start " +
        "watching markup so .designer.cs files regenerate automatically on save. " +
        "Call this once before working in a legacy WebForms solution so you never have to " +
        "hand-edit a generated designer file. Omit solutionPath to auto-discover.")]
    public static async Task<string> OpenSolution(
        SolutionSessionService session,
        IOutputFormatter fmt,
        [Description("Path to a .sln/.slnx, or a directory to search. Omit to auto-discover from the working directory.")]
        string? solutionPath = null,
        [Description("Watch .aspx/.ascx/.master/.dbml files and regenerate their designer files on change.")]
        bool watch = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = ResolveSolution(solutionPath);
            if (resolved is null)
            {
                return solutionPath is null
                    ? "Error: No .sln/.slnx found in the working directory. Pass 'solutionPath' explicitly."
                    : $"Error: No solution found at '{solutionPath}'.";
            }

            var projects = PathHelper.GetProjectsFromSolution(resolved);
            if (projects.Count == 0)
                return $"Error: '{Path.GetFileName(resolved)}' contains no projects.";

            var directories = projects
                .Select(Path.GetDirectoryName)
                .Where(d => !string.IsNullOrEmpty(d))
                .Select(d => d!)
                .ToList();

            session.Open(resolved, directories, watch);

            var classifications = projects.Select(ProjectClassifier.Classify).ToList();
            await WarmWorkspaceAsync(projects, cancellationToken);

            return Format(resolved, classifications, session, watch, fmt);
        }
        catch (OperationCanceledException)
        {
            return "Error: Opening the solution was cancelled.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "Close the currently open solution: stop the designer file watcher and release its state. " +
        "The workspace cache is left alone; it evicts on its own schedule.")]
    public static string CloseSolution(SolutionSessionService session)
    {
        var previous = session.SolutionPath;
        session.Close();

        return previous is null
            ? "No solution was open."
            : $"Closed '{Path.GetFileName(previous)}' and stopped watching for designer changes.";
    }

    [McpServerTool, Description(
        "Report the currently open solution, whether designer files are being watched, and the " +
        "most recent automatic designer regenerations.")]
    public static string GetSolutionStatus(SolutionSessionService session, IOutputFormatter fmt)
    {
        if (session.SolutionPath is not { } solutionPath)
            return "No solution is open. Call OpenSolution first.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {Path.GetFileName(solutionPath)}");
        sb.AppendLine();
        sb.AppendLine($"- **Path**: {solutionPath}");
        sb.AppendLine($"- **Watching designers**: {(session.IsWatching ? "yes" : "no")}");
        if (session.PendingCount > 0)
            sb.AppendLine($"- **Pending regenerations**: {session.PendingCount}");
        sb.AppendLine();

        var history = session.History;
        if (history.Count == 0)
        {
            sb.AppendLine("No designer files have been regenerated automatically yet.");
            return sb.ToString();
        }

        sb.AppendLine("## Recent automatic regenerations");
        sb.AppendLine();
        sb.AppendLine("| Time (UTC) | File | Result |");
        sb.AppendLine("|------------|------|--------|");
        foreach (var entry in history.Reverse())
        {
            var status = entry.Outcome == DesignerOutcome.Failed
                ? "failed: " + string.Join("; ", entry.Errors)
                : entry.Outcome.ToString().ToLowerInvariant();
            sb.AppendLine($"| {entry.AtUtc:HH:mm:ss} | {Path.GetFileName(entry.SourcePath)} | {status} |");
        }

        return sb.ToString();
    }

    private static string? ResolveSolution(string? solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            var candidates = PathHelper.FindSolutionFiles(Directory.GetCurrentDirectory());
            return candidates.Length > 0 ? PathHelper.NormalizePath(candidates[0]) : null;
        }

        var resolved = PathHelper.NormalizePath(solutionPath);

        if (PathHelper.IsSolutionFile(resolved))
            return File.Exists(resolved) ? resolved : null;

        if (Directory.Exists(resolved))
        {
            var candidates = PathHelper.FindSolutionFiles(resolved);
            return candidates.Length > 0 ? PathHelper.NormalizePath(candidates[0]) : null;
        }

        return null;
    }

    /// <summary>
    /// Loads the projects so later tool calls hit a warm workspace. One project is enough to pull
    /// in its whole solution, and a project that fails to load must not fail the open — the report
    /// still tells the caller what is there.
    /// </summary>
    private static async Task WarmWorkspaceAsync(
        IReadOnlyList<string> projects, CancellationToken cancellationToken)
    {
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await WorkspaceService.GetOrOpenProjectAsync(project, cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[OpenSolution] Could not load '{Path.GetFileName(project)}': {ex.Message}");
            }
        }
    }

    private static string Format(
        string solutionPath,
        List<ProjectClassification> projects,
        SolutionSessionService session,
        bool watch,
        IOutputFormatter fmt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Opened {Path.GetFileName(solutionPath)}");
        sb.AppendLine();

        sb.AppendLine("| Project | Framework | Kind | Builds with |");
        sb.AppendLine("|---------|-----------|------|-------------|");
        foreach (var project in projects)
        {
            var name = Path.GetFileNameWithoutExtension(project.ProjectPath);
            var buildTool = project.BuildTool == BuildTool.VisualStudioMsBuild ? "VS MSBuild" : "dotnet";
            sb.AppendLine(
                $"| {name} | {project.TargetFramework ?? project.Runtime.ToString()} | " +
                $"{DescribeKind(project)} | {buildTool} |");
        }
        sb.AppendLine();

        AppendToolchain(sb, projects);

        sb.AppendLine(watch
            ? session.IsWatching
                ? "Watching .aspx/.ascx/.master/.dbml — their .designer.cs files regenerate on save. " +
                  "Do not edit generated designer files directly."
                : "Requested watching, but no project directory could be watched."
            : "Designer watching is off. Use RegenerateDesigner to update designer files manually.");

        // Surfaced here rather than on every tool: this is the "start here" call, so it is seen
        // once per session instead of repeatedly.
        if (UpdateCheckService.GetHint() is { } updateHint)
        {
            sb.AppendLine();
            sb.AppendLine($"> {updateHint}");
        }

        return sb.ToString();
    }

    private static string DescribeKind(ProjectClassification project) => project.Kind switch
    {
        AppKind.AspNetCore => "ASP.NET Core (dotnet run)",
        AppKind.AspNetClassic => "ASP.NET (IIS Express)",
        AppKind.ConsoleApp => "Console",
        AppKind.WindowsApp => "Windows app",
        AppKind.ClassLibrary => project.IsTestProject ? "Test library" : "Library",
        _ => "Unknown",
    };

    /// <summary>
    /// Reports the legacy toolchain only when the solution actually contains a legacy project, and
    /// only the pieces that are missing — a complete toolchain needs no commentary.
    /// </summary>
    private static void AppendToolchain(StringBuilder sb, List<ProjectClassification> projects)
    {
        var needsMsBuild = projects.Any(p => p.BuildTool == BuildTool.VisualStudioMsBuild);
        var needsIis = projects.Any(p => p.Kind == AppKind.AspNetClassic);
        if (!needsMsBuild && !needsIis)
            return;

        var toolchain = NetFxToolchain.Info;
        var missing = new List<string>();

        if (needsMsBuild && toolchain.MsBuildPath.Length == 0)
            missing.Add("Visual Studio MSBuild (required to build legacy projects) — install Build Tools for Visual Studio.");
        if (needsIis && !toolchain.WebApplicationTargets)
            missing.Add("Microsoft.WebApplication.targets (required to build legacy web projects).");
        if (needsIis && toolchain.PreferredIisExpress is null)
            missing.Add("IIS Express (required to run legacy ASP.NET sites).");
        if (!toolchain.ReferenceAssemblies)
            missing.Add(".NET Framework reference assemblies — install the .NET Framework Developer Pack.");

        sb.AppendLine("## .NET Framework toolchain");
        sb.AppendLine();

        if (missing.Count == 0)
        {
            sb.AppendLine("All required tools were found.");
        }
        else
        {
            sb.AppendLine("Missing:");
            foreach (var item in missing)
                sb.AppendLine($"- {item}");
        }

        sb.AppendLine();
    }
}
