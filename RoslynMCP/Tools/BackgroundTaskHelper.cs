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
        BackgroundTaskStore taskStore)
    {
        var description = $"dotnet test {Path.GetFileNameWithoutExtension(csprojPath)}";
        if (!string.IsNullOrWhiteSpace(filter))
            description += $" --filter {filter}";

        var taskId = taskStore.CreateTask(BackgroundTaskStore.TaskKind.Tests, description);
        _ = RunBuildThenTestAsync(taskId, csprojPath, filter, build, timeoutSeconds, taskStore);

        return $"Tests started in background{(build ? " (build → test)" : "")}.\n" +
               $"**Task ID:** `{taskId}`\n" +
               $"**Command:** {description}\n\n" +
               $"You can continue working on other tasks. " +
               $"Check results later with `GetBackgroundTaskResult(\"{taskId}\")`.";
    }

    /// <summary>Starts a build in the background and returns a task ID.</summary>
    internal static string StartBuildBackground(
        string resolvedPath, string configuration,
        BackgroundTaskStore taskStore)
    {
        var description = $"dotnet build {Path.GetFileName(resolvedPath)}";
        var taskId = taskStore.CreateTask(BackgroundTaskStore.TaskKind.Build, description);

        var args = $"build \"{resolvedPath}\" --verbosity quiet";
        if (!string.IsNullOrWhiteSpace(configuration))
            args += $" -c {configuration}";

        _ = RunInBackgroundAsync(taskId, "dotnet", args,
            Path.GetDirectoryName(resolvedPath)!, 300, taskStore);

        return $"Build started in background.\n**Task ID:** `{taskId}`\n\n" +
               $"You can continue working on other tasks. " +
               $"Check results later with `GetBackgroundTaskResult(\"{taskId}\")`.";
    }

    /// <summary>Starts coverage collection in the background and returns a task ID.</summary>
    internal static string StartCoverageBackground(
        string csprojPath, string? filter,
        BackgroundTaskStore taskStore)
    {
        var description = $"coverage {Path.GetFileNameWithoutExtension(csprojPath)}";
        if (!string.IsNullOrWhiteSpace(filter))
            description += $" --filter {filter}";

        var taskId = taskStore.CreateTask(BackgroundTaskStore.TaskKind.Coverage, description);
        _ = RunCoverageInBackgroundAsync(taskId, csprojPath, filter, taskStore);

        return $"Coverage collection started in background (build → test with coverage).\n" +
               $"**Task ID:** `{taskId}`\n\n" +
               $"You can continue working on other tasks. " +
               $"Check results later with `GetBackgroundTaskResult(\"{taskId}\")`.\n" +
               $"Once complete, use `GetCoverage` to query the cached results.";
    }

    private static async Task RunBuildThenTestAsync(
        string taskId, string csprojPath, string? filter, bool build,
        int timeoutSeconds, BackgroundTaskStore taskStore)
    {
        try
        {
            var workingDirectory = Path.GetDirectoryName(csprojPath)!;
            var result = new StringBuilder();

            if (build)
            {
                var buildArgs = $"build \"{csprojPath}\" --verbosity quiet";
                var (buildExitCode, buildOutput, buildErrors) = await RunProcessAsync(
                    "dotnet", buildArgs, workingDirectory, timeoutSeconds / 2);

                if (buildExitCode != 0)
                {
                    result.AppendLine("❌ **Build failed** — tests were not started.");
                    AppendProcessOutput(result, buildOutput, buildErrors, buildExitCode);
                    taskStore.Complete(taskId, result.ToString(), buildExitCode);
                    return;
                }

                result.AppendLine("✅ **Build succeeded**");
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
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
        BackgroundTaskStore taskStore)
    {
        try
        {
            var result = await CoverageService.RunCoverageAsync(csprojPath, filter, CancellationToken.None);
            taskStore.Complete(taskId, result.Message, result.Success ? 0 : 1);
        }
        catch (Exception ex)
        {
            taskStore.Complete(taskId, $"Error: {ex.Message}", -1);
        }
    }
}
