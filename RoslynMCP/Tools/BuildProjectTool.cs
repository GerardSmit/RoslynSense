using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class BuildProjectTool
{
    [McpServerTool, Description(
        "Build a .NET project or solution. Set background=true to build in the background " +
        "and continue working — check results later with GetBackgroundTaskResult.")]
    public static async Task<string> BuildProject(
        [Description("Path to the .csproj, .sln file, or a source file in the project.")]
        string projectPath,
        BackgroundTaskStore taskStore,
        BuildWarningsStore warningsStore,
        [Description("Build configuration. Default: 'Debug'.")]
        string configuration = "Debug",
        [Description("Set to true to build in the background. Returns a task ID immediately " +
                     "so you can continue working. Use GetBackgroundTaskResult to check results later.")]
        bool background = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string resolved = ResolveBuildTarget(projectPath);
            if (resolved.StartsWith("Error:", StringComparison.Ordinal))
                return resolved;

            if (background)
                return BackgroundTaskHelper.StartBuildBackground(
                    resolved, configuration, taskStore);

            string fileName;
            string arguments;

            if (PathHelper.RequiresMsBuild(resolved))
            {
                var msbuild = MsBuildLocator.FindMsBuild();
                if (msbuild is null)
                    return "Error: This project requires MSBuild (legacy .NET Framework project) but " +
                           "MSBuild could not be found. Install Visual Studio or Build Tools for Visual Studio.";

                fileName = msbuild;
                arguments = BuildMsBuildArgs(resolved, SanitizeConfiguration(configuration));
            }
            else
            {
                fileName = "dotnet";
                arguments = BuildDotnetArgs(resolved, SanitizeConfiguration(configuration));
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(resolved)
                }
            };

            // Disable terminal logger to get clean parseable output
            process.StartInfo.Environment["MSBUILDTERMINALLOGGER"] = "off";

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) stderr.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "Build was cancelled.";
            }

            return FormatBuildOutput(stdout.ToString(), stderr.ToString(), process.ExitCode, resolved, warningsStore);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    internal static string ResolveBuildTarget(string path)
    {
        var normalized = PathHelper.NormalizePath(path);

        if (normalized.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) && File.Exists(normalized))
            return normalized;

        if (normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(normalized))
            return normalized;

        // If it's a source file, find the nearest .csproj
        var csproj = RunTestsTool.ResolveCsprojPath(normalized);
        if (csproj is not null) return csproj;

        // If it's a directory, look for .sln or .csproj
        if (Directory.Exists(normalized))
        {
            var slnFiles = Directory.GetFiles(normalized, "*.sln", SearchOption.TopDirectoryOnly);
            if (slnFiles.Length > 0) return slnFiles[0];

            var csprojFiles = Directory.GetFiles(normalized, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojFiles.Length > 0) return csprojFiles[0];
        }

        return $"Error: Could not find a buildable target for '{path}'.";
    }

    private static string FormatBuildOutput(
        string stdout, string stderr, int exitCode, string target,
        BuildWarningsStore warningsStore)
    {
        var sb = new StringBuilder();

        if (exitCode == 0)
            sb.AppendLine("✅ **Build Succeeded**");
        else
            sb.AppendLine("❌ **Build Failed**");

        sb.AppendLine();
        sb.AppendLine($"**Target**: {Path.GetFileName(target)}");
        sb.AppendLine();

        var errors = new List<string>();
        var warningLines = new List<string>();

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.Contains(": error ", StringComparison.Ordinal))
                errors.Add(line);
            else if (line.Contains(": warning ", StringComparison.Ordinal))
                warningLines.Add(line);
        }

        // Store warnings for later retrieval via GetBuildWarnings
        warningsStore.Store(target, warningLines);

        if (errors.Count > 0)
        {
            sb.AppendLine($"**Errors ({errors.Count}):**");
            sb.AppendLine("```");
            foreach (var error in errors)
                sb.AppendLine(error);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (warningLines.Count > 0)
        {
            var grouped = warningsStore.GetAll(target)!;
            var sorted = grouped.OrderByDescending(kv => kv.Value.Count).ToList();
            int uniqueCodes = sorted.Count;

            sb.AppendLine($"**Warnings ({warningLines.Count} total, {uniqueCodes} unique code{(uniqueCodes == 1 ? "" : "s")}):**");
            sb.AppendLine("```");
            foreach (var (code, lines) in sorted)
            {
                var firstMessage = BuildWarningsStore.ExtractMessage(lines[0]);
                // Truncate long messages for readability
                if (firstMessage.Length > 100)
                    firstMessage = firstMessage[..97] + "...";
                sb.AppendLine($"{lines.Count,5}x  {code}  — {firstMessage}");
            }
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine($"> Use `GetBuildWarnings` with the project path and a warning code (e.g. `CS0414`) to see all occurrences.");
            sb.AppendLine();
        }

        // On failure with no structured errors found, include raw output
        if (exitCode != 0 && errors.Count == 0)
        {
            var raw = stdout.Trim();
            if (!string.IsNullOrEmpty(raw))
            {
                sb.AppendLine("**Output:**");
                sb.AppendLine("```");
                sb.AppendLine(raw);
                sb.AppendLine("```");
            }
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            var filtered = stderr.Trim();
            if (!string.IsNullOrWhiteSpace(filtered))
            {
                sb.AppendLine("**Stderr:**");
                sb.AppendLine("```");
                sb.AppendLine(filtered);
                sb.AppendLine("```");
            }
        }

        return sb.ToString();
    }

    private static string BuildDotnetArgs(string resolved, string configuration) =>
        $"build \"{resolved}\" --configuration \"{configuration}\" --nologo";

    private static string BuildMsBuildArgs(string resolved, string configuration) =>
        $"\"{resolved}\" /p:Configuration=\"{configuration}\" /nologo /v:minimal";

    /// <summary>
    /// Strips any characters that aren't alphanumeric, dash, underscore, or dot
    /// to prevent argument injection via the configuration parameter.
    /// </summary>
    private static string SanitizeConfiguration(string configuration)
    {
        var sanitized = new StringBuilder(configuration.Length);
        foreach (var c in configuration)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
                sanitized.Append(c);
        }
        return sanitized.Length > 0 ? sanitized.ToString() : "Debug";
    }
}
