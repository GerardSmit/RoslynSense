using System.Diagnostics;
using System.Text;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Shared background task execution logic used by RunTests, BuildProject, and RunCoverage
/// when their <c>background</c> parameter is true.
/// </summary>
internal static class BackgroundTaskHelper
{
    /// <summary>Starts a build→test run in the background and returns a task ID.</summary>
    internal static string StartTestsBackground(
        string csprojPath, string? filter, bool build, int timeoutSeconds,
        BackgroundTaskStore taskStore, BuildWarningsStore warningsStore)
    {
        var description = $"dotnet test {Path.GetFileNameWithoutExtension(csprojPath)}";
        if (!string.IsNullOrWhiteSpace(filter))
            description += $" --filter {filter}";

        var taskId = taskStore.CreateTask(BackgroundTaskStore.TaskKind.Tests, description);
        _ = RunBuildThenTestAsync(taskId, csprojPath, filter, build, timeoutSeconds, taskStore, warningsStore);

        return $"Tests started in background{(build ? " (build → test)" : "")}.\n" +
               $"**Task ID:** `{taskId}`\n" +
               $"**Command:** {description}\n\n" +
               $"You can continue working on other tasks. " +
               $"Check results later with `GetBackgroundTaskResult(\"{taskId}\")`.";
    }

    /// <summary>Starts a build in the background and returns a task ID.</summary>
    internal static string StartBuildBackground(
        string resolvedPath, string configuration, int timeoutSeconds,
        BackgroundTaskStore taskStore, BuildWarningsStore warningsStore)
    {
        string fileName;
        string args;
        string description;

        if (PathHelper.RequiresMsBuild(resolvedPath))
        {
            var msbuild = MsBuildLocator.FindMsBuild();
            if (msbuild is null)
                return "Error: This project requires MSBuild (legacy .NET Framework project) but " +
                       "MSBuild could not be found. Install Visual Studio or Build Tools for Visual Studio.";
            fileName = msbuild;
            args = $"\"{resolvedPath}\" /p:Configuration={configuration} /nologo /v:minimal " +
                   BuildProcessHelper.NoNodeReuseArg;
            description = $"msbuild {Path.GetFileName(resolvedPath)}";
        }
        else
        {
            fileName = "dotnet";
            args = $"build \"{resolvedPath}\" --nologo";
            if (!string.IsNullOrWhiteSpace(configuration))
                args += $" -c {configuration}";
            description = $"dotnet build {Path.GetFileName(resolvedPath)}";
        }

        var taskId = taskStore.CreateTask(BackgroundTaskStore.TaskKind.Build, description);
        _ = RunBuildInBackgroundAsync(taskId, fileName, args,
            Path.GetDirectoryName(resolvedPath)!, timeoutSeconds, resolvedPath, taskStore, warningsStore);

        return $"Build started in background.\n**Task ID:** `{taskId}`\n\n" +
               $"You can continue working on other tasks. " +
               $"Check results later with `GetBackgroundTaskResult(\"{taskId}\")`.";
    }

    private static async Task RunBuildInBackgroundAsync(
        string taskId, string fileName, string arguments,
        string workingDirectory, int timeoutSeconds, string resolvedPath,
        BackgroundTaskStore taskStore, BuildWarningsStore warningsStore)
    {
        try
        {
            var (exitCode, stdout, stderr) = await RunProcessAsync(
                fileName, arguments, workingDirectory, timeoutSeconds);
            var formatted = BuildProjectTool.FormatBuildOutput(stdout, stderr, exitCode, resolvedPath, warningsStore);
            taskStore.Complete(taskId, formatted, exitCode);
        }
        catch (OperationCanceledException)
        {
            taskStore.Cancel(taskId, $"Build timed out after {timeoutSeconds} seconds.");
        }
        catch (Exception ex)
        {
            taskStore.Complete(taskId, $"Error: {ex.Message}", -1);
        }
    }

    /// <summary>Starts coverage collection in the background and returns a task ID.</summary>
    internal static string StartCoverageBackground(
        string csprojPath, string? filter,
        BackgroundTaskStore taskStore, BuildWarningsStore warningsStore)
    {
        var description = $"coverage {Path.GetFileNameWithoutExtension(csprojPath)}";
        if (!string.IsNullOrWhiteSpace(filter))
            description += $" --filter {filter}";

        var taskId = taskStore.CreateTask(BackgroundTaskStore.TaskKind.Coverage, description);
        _ = RunCoverageInBackgroundAsync(taskId, csprojPath, filter, taskStore, warningsStore);

        return $"Coverage collection started in background (build → test with coverage).\n" +
               $"**Task ID:** `{taskId}`\n\n" +
               $"You can continue working on other tasks. " +
               $"Check results later with `GetBackgroundTaskResult(\"{taskId}\")`.\n" +
               $"Once complete, use `GetCoverage` for project-wide overview, or `GetMethodCoverage(methodName: '...')` for per-line detail.";
    }

    /// <summary>Starts a MSBuild+VSTest run for a legacy .NET Framework project in the background.</summary>
    internal static string StartLegacyTestsBackground(
        string csprojPath, string? filter, bool build, int timeoutSeconds,
        BackgroundTaskStore taskStore, BuildWarningsStore warningsStore)
    {
        var description = $"vstest {Path.GetFileNameWithoutExtension(csprojPath)}";
        if (!string.IsNullOrWhiteSpace(filter))
            description += $" /TestCaseFilter:{filter}";

        var taskId = taskStore.CreateTask(BackgroundTaskStore.TaskKind.Tests, description);
        _ = RunBuildThenLegacyTestAsync(taskId, csprojPath, filter, build, timeoutSeconds, taskStore, warningsStore);

        return $"Tests started in background{(build ? " (build → vstest)" : "")}.\n" +
               $"**Task ID:** `{taskId}`\n" +
               $"**Command:** {description}\n\n" +
               $"You can continue working on other tasks. " +
               $"Check results later with `GetBackgroundTaskResult(\"{taskId}\")`.";
    }

    private static async Task RunBuildThenLegacyTestAsync(
        string taskId, string csprojPath, string? filter, bool build,
        int timeoutSeconds, BackgroundTaskStore taskStore, BuildWarningsStore warningsStore)
    {
        try
        {
            var workingDirectory = Path.GetDirectoryName(csprojPath)!;
            var result = new StringBuilder();

            var msbuild = MsBuildLocator.FindMsBuild();
            if (msbuild is null)
            {
                taskStore.Complete(taskId,
                    "Error: MSBuild not found. Install Visual Studio or Build Tools.", -1);
                return;
            }

            if (build)
            {
                var buildArgs = $"\"{csprojPath}\" /nologo /v:minimal " + BuildProcessHelper.NoNodeReuseArg;
                var (buildExitCode, buildOutput, buildErrors) = await RunProcessAsync(
                    msbuild, buildArgs, workingDirectory, Math.Max(60, timeoutSeconds / 2));

                result.AppendLine(BuildProjectTool.FormatBuildOutput(
                    buildOutput, buildErrors, buildExitCode, csprojPath, warningsStore));

                if (buildExitCode != 0)
                {
                    taskStore.Complete(taskId, result.ToString(), buildExitCode);
                    return;
                }
            }

            var targetPath = MsBuildLocator.GetTargetPath(csprojPath);
            if (targetPath is null || !File.Exists(targetPath))
            {
                taskStore.Complete(taskId,
                    "Error: Could not determine test assembly path. " +
                    "Ensure the project has been built successfully.", -1);
                return;
            }

            var vstestArgs = new StringBuilder($"vstest \"{targetPath}\"");
            if (!string.IsNullOrWhiteSpace(filter))
                vstestArgs.Append($" /TestCaseFilter:\"{filter!.Replace("\"", "\\\"")}\"");

            var (testExitCode, testOutput, testErrors) = await RunProcessAsync(
                "dotnet", vstestArgs.ToString(), workingDirectory, timeoutSeconds);

            result.AppendLine(testExitCode == 0 ? "✅ **Tests passed**" : "❌ **Tests failed**");
            AppendProcessOutput(result, testOutput, testErrors, testExitCode);
            taskStore.Complete(taskId, result.ToString(), testExitCode);
        }
        catch (OperationCanceledException)
        {
            taskStore.Cancel(taskId, $"Task timed out after {timeoutSeconds} seconds.");
        }
        catch (Exception ex)
        {
            taskStore.Complete(taskId, $"Error: {ex.Message}", -1);
        }
    }

    private static async Task RunBuildThenTestAsync(
        string taskId, string csprojPath, string? filter, bool build,
        int timeoutSeconds, BackgroundTaskStore taskStore, BuildWarningsStore warningsStore)
    {
        try
        {
            var workingDirectory = Path.GetDirectoryName(csprojPath)!;
            var result = new StringBuilder();

            if (build)
            {
                var buildArgs = $"build \"{csprojPath}\" --nologo";
                var (buildExitCode, buildOutput, buildErrors) = await RunProcessAsync(
                    "dotnet", buildArgs, workingDirectory, timeoutSeconds / 2);

                result.AppendLine(BuildProjectTool.FormatBuildOutput(
                    buildOutput, buildErrors, buildExitCode, csprojPath, warningsStore));

                if (buildExitCode != 0)
                {
                    taskStore.Complete(taskId, result.ToString(), buildExitCode);
                    return;
                }
            }

            var testArgs = new StringBuilder();
            testArgs.Append($"test \"{csprojPath}\" --verbosity quiet --no-build");
            if (!string.IsNullOrWhiteSpace(filter))
                testArgs.Append($" --filter \"{filter!.Replace("\"", "\\\"")}\"");

            var (testExitCode, testOutput, testErrors) = await RunProcessAsync(
                "dotnet", testArgs.ToString(), workingDirectory, timeoutSeconds);

            if (testExitCode == 0)
                result.AppendLine("✅ **Tests passed**");
            else
                result.AppendLine("❌ **Tests failed**");

            AppendProcessOutput(result, testOutput, testErrors, testExitCode);
            taskStore.Complete(taskId, result.ToString(), testExitCode);
        }
        catch (OperationCanceledException)
        {
            taskStore.Cancel(taskId, $"Task timed out after {timeoutSeconds} seconds.");
        }
        catch (Exception ex)
        {
            taskStore.Complete(taskId, $"Error: {ex.Message}", -1);
        }
    }

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string arguments, string workingDirectory, int timeoutSeconds)
    {
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
                WorkingDirectory = workingDirectory
            }
        };

        BuildProcessHelper.ConfigureMsBuildEnvironment(process.StartInfo);

        // When using VS MSBuild, set VSINSTALLDIR so $(VSToolsPath) resolves correctly
        if (fileName != "dotnet")
            MsBuildLocator.SetVsEnvironment(process.StartInfo, fileName);

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

        BuildProcessHelper.StartWithClosedInput(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            await BuildProcessHelper.KillAndDrainAsync(process);
            throw;
        }

        return (process.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    internal static void AppendProcessOutput(StringBuilder result, string output, string errors, int exitCode)
    {
        if (!string.IsNullOrEmpty(output))
        {
            result.AppendLine("```");
            if (output.Length > 8000)
                result.AppendLine(output[..8000] + "\n... (truncated)");
            else
                result.AppendLine(output);
            result.AppendLine("```");
        }

        if (!string.IsNullOrEmpty(errors) && exitCode != 0)
        {
            result.AppendLine("\n**Errors:**");
            result.AppendLine("```");
            result.AppendLine(errors.Length > 4000 ? errors[..4000] + "\n... (truncated)" : errors);
            result.AppendLine("```");
        }
    }

    private static async Task RunInBackgroundAsync(
        string taskId, string fileName, string arguments,
        string workingDirectory, int timeoutSeconds,
        BackgroundTaskStore taskStore)
    {
        try
        {
            var (exitCode, output, errors) = await RunProcessAsync(
                fileName, arguments, workingDirectory, timeoutSeconds);

            var result = new StringBuilder();
            if (exitCode == 0)
                result.AppendLine("✅ **Success**");
            else
                result.AppendLine("❌ **Failed**");

            AppendProcessOutput(result, output, errors, exitCode);
            taskStore.Complete(taskId, result.ToString(), exitCode);
        }
        catch (OperationCanceledException)
        {
            taskStore.Cancel(taskId, $"Task timed out after {timeoutSeconds} seconds.");
        }
        catch (Exception ex)
        {
            taskStore.Complete(taskId, $"Error: {ex.Message}", -1);
        }
    }

    private static async Task RunCoverageInBackgroundAsync(
        string taskId, string csprojPath, string? filter,
        BackgroundTaskStore taskStore, BuildWarningsStore warningsStore)
    {
        try
        {
            var result = await CoverageService.RunCoverageAsync(csprojPath, filter, 300, CancellationToken.None, warningsStore);
            taskStore.Complete(taskId, result.Message, result.Success ? 0 : 1);
        }
        catch (Exception ex)
        {
            taskStore.Complete(taskId, $"Error: {ex.Message}", -1);
        }
    }
}
