using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RoslynMCP.Services;

/// <summary>
/// Manages netcoredbg debugging sessions via the MI (Machine Interface) protocol.
/// Supports starting test debug sessions (VSTEST_HOST_DEBUG), attaching to processes,
/// breakpoint management, stepping, expression evaluation, and stack inspection.
/// </summary>
internal sealed partial class DebuggerService : IDebugBackend
{
    [GeneratedRegex(@"Process Id:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TestHostPidRegex();

    [GeneratedRegex(@"\^done,bkpt=\{number=""(\d+)""")]
    private static partial Regex BreakpointInsertedRegex();

    [GeneratedRegex(@"frame=\{([^}]+)\}")]
    private static partial Regex StackFrameRegex();

    private Process? _netcoredbgProcess;
    private Process? _testHostProcess;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private int _tokenCounter;
    private CancellationTokenSource? _sessionCts;

    private volatile DebugState _state = DebugState.NotStarted;
    private StoppedFrame? _currentFrame;
    private readonly ConcurrentDictionary<int, BreakpointInfo> _breakpoints = new();
    private readonly List<string> _consoleOutput = [];
    private readonly object _outputLock = new();
    private int _expectedToken;
    private TaskCompletionSource<string>? _responseWaiter;

    public DebugState State => _state;
    public StoppedFrame? CurrentFrame { get { lock (_outputLock) return _currentFrame; } }
    public IReadOnlyDictionary<int, BreakpointInfo> Breakpoints => _breakpoints;

    /// <summary>
    /// Starts a test debugging session.
    /// Builds the project, launches dotnet test with VSTEST_HOST_DEBUG, captures PID,
    /// then attaches netcoredbg.
    /// </summary>
    public async Task<string> StartTestSessionAsync(
        string csprojPath,
        string? filter,
        IEnumerable<(string file, int line)>? initialBreakpoints = null,
        CancellationToken cancellationToken = default)
    {
        if (_state != DebugState.NotStarted)
            return "Error: A debug session is already active. Call 'stop' first.";

        if (!File.Exists(csprojPath))
            return $"Error: Project file not found: {csprojPath}";

        // Verify netcoredbg is available
        var netcoredbgPath = await FindOrProvisionNetcoredbgAsync(cancellationToken);
        if (netcoredbgPath is null)
            return "Error: netcoredbg not found and auto-download failed. " +
                   "Install it from https://github.com/Samsung/netcoredbg/releases " +
                   "and ensure it is on your PATH.";

        _state = DebugState.Starting;

        // Build the test project in Debug configuration for debugging
        var buildResult = await RunProcessAsync("dotnet", $"build \"{csprojPath}\" -c Debug", cancellationToken);
        if (buildResult.exitCode != 0)
        {
            _state = DebugState.NotStarted;
            return $"Error: Build failed:\n{buildResult.output}";
        }

        // Start dotnet test with VSTEST_HOST_DEBUG
        var testArgs = new StringBuilder($"test \"{csprojPath}\" -c Debug --no-build");
        if (!string.IsNullOrWhiteSpace(filter))
            testArgs.Append($" --filter \"{filter.Replace("\"", "\\\"")}\"");

        _testHostProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = testArgs.ToString(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(csprojPath)
            }
        };
        _testHostProcess.StartInfo.Environment["VSTEST_HOST_DEBUG"] = "1";

        var pidTcs = new TaskCompletionSource<int>();
        var outputBuffer = new StringBuilder();

        _testHostProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            outputBuffer.AppendLine(e.Data);
            var match = TestHostPidRegex().Match(e.Data);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var pid))
                pidTcs.TrySetResult(pid);
        };
        _testHostProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) outputBuffer.AppendLine(e.Data);
        };

        _testHostProcess.Start();
        _testHostProcess.BeginOutputReadLine();
        _testHostProcess.BeginErrorReadLine();

        // Wait for the test host to report its PID
        var pidTask = pidTcs.Task;
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        var completed = await Task.WhenAny(pidTask, timeoutTask);

        if (completed != pidTask)
        {
            CleanupProcesses();
            _state = DebugState.NotStarted;
            return $"Error: Timed out waiting for test host PID.\nOutput:\n{outputBuffer}";
        }

        var testHostPid = pidTask.Result;

        // Start netcoredbg and connect to test host, passing initial breakpoints
        // so they are written to stdin immediately (before library loading completes).
        var attachResult = await StartNetcoredbgAsync(
            netcoredbgPath, testHostPid, initialBreakpoints, cancellationToken);
        if (attachResult is not null)
        {
            CleanupProcesses();
            _state = DebugState.NotStarted;
            return attachResult;
        }

        // With VSTEST_HOST_DEBUG and JMC enabled, the test host auto-resumes after
        // attach (Debugger.Break() is skipped because testhost.dll is a Release build).
        // If immediate breakpoints were set, the test will hit them during this phase.
        // Wait for either a breakpoint hit, process exit, or timeout.
        var waitResult = await WaitForInitialStopAsync(
            hasBreakpoints: initialBreakpoints?.Any() == true, cancellationToken);

        var sb = new StringBuilder();
        sb.Append($"Debug session started. Test host PID: {testHostPid}.");
        if (_breakpoints.Count > 0)
            sb.Append($" Breakpoints set: {_breakpoints.Count}.");

        if (waitResult is not null)
        {
            sb.AppendLine();
            sb.Append(waitResult);
        }
        else
        {
            sb.Append(" Use 'continue' to start test execution.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Attaches to a running .NET process by PID.
    /// </summary>
    public async Task<string> AttachToProcessAsync(
        int pid,
        IEnumerable<(string file, int line)>? initialBreakpoints = null,
        CancellationToken cancellationToken = default)
    {
        if (_state != DebugState.NotStarted)
            return "Error: A debug session is already active. Call 'stop' first.";

        var netcoredbgPath = await FindOrProvisionNetcoredbgAsync(cancellationToken);
        if (netcoredbgPath is null)
            return "Error: netcoredbg not found and auto-download failed. " +
                   "Install it from https://github.com/Samsung/netcoredbg/releases " +
                   "and ensure it is on your PATH.";

        try
        {
            Process.GetProcessById(pid);
        }
        catch
        {
            return $"Error: No process found with PID {pid}.";
        }

        _state = DebugState.Starting;

        var attachResult = await StartNetcoredbgAsync(netcoredbgPath, pid, initialBreakpoints, cancellationToken);
        if (attachResult is not null)
        {
            _state = DebugState.NotStarted;
            return attachResult;
        }

        // Breakpoints written immediately are auto-tracked by ProcessMiOutput.
        // For attach, also set any breakpoints via the normal command path as
        // the process may not exit immediately like test hosts do.
        if (initialBreakpoints is not null)
        {
            foreach (var (file, line) in initialBreakpoints)
            {
                var normalizedPath = PathHelper.NormalizePath(file);
                if (!_breakpoints.Values.Any(bp =>
                    bp.FilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) && bp.Line == line))
                {
                    await SetBreakpointAsync(file, line, condition: null, cancellationToken: cancellationToken);
                }
            }
        }

        _state = DebugState.Stopped;
        return $"Attached to process {pid}. Breakpoints set: {_breakpoints.Count}. " +
               "Use 'continue' to resume execution.";
    }

    /// <summary>
    /// Lists running .NET processes that can be attached to.
    /// </summary>
    public static async Task<string> ListDotNetProcessesAsync(CancellationToken cancellationToken)
    {
        // Try dotnet-trace ps first (check PATH, then our tools cache)
        var dotnetTracePath = await FindOrProvisionDotnetTraceAsync(cancellationToken);
        if (dotnetTracePath is not null)
        {
            var (exitCode, output) = await RunProcessAsync(dotnetTracePath, "ps", cancellationToken);
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
                return output;
        }

        // Fallback: list processes manually
        var sb = new StringBuilder();
        sb.AppendLine("PID    | Name                 | Command");
        sb.AppendLine("-------|----------------------|--------");

        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                try
                {
                    var name = proc.ProcessName;
                    if (name.Contains("dotnet", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("testhost", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"{proc.Id,-6} | {name,-20} | (use --attach {proc.Id})");
                    }
                }
                catch
                {
                    // Ignore inaccessible processes
                }
            }
        }

        return sb.ToString();
    }

    internal static async Task<string?> FindOrProvisionDotnetTraceAsync(CancellationToken cancellationToken)
    {
        // Check if dotnet-trace is on PATH
        var (exitCode, _) = await RunProcessAsync("dotnet-trace", "--version", cancellationToken);
        if (exitCode == 0)
            return "dotnet-trace";

        // Check our tools cache
        var cachedPath = Path.Combine(s_toolsDirectory, "dotnet-trace");
        var toolManifest = Path.Combine(cachedPath, ".store");
        if (Directory.Exists(toolManifest))
        {
            var exeName = OperatingSystem.IsWindows() ? "dotnet-trace.exe" : "dotnet-trace";
            var exePath = Directory.EnumerateFiles(cachedPath, exeName, SearchOption.AllDirectories).FirstOrDefault();
            if (exePath is not null)
                return exePath;
        }

        // Auto-install via dotnet tool
        try
        {
            Directory.CreateDirectory(cachedPath);
            var (installExitCode, _) = await RunProcessAsync(
                "dotnet",
                $"tool install dotnet-trace --tool-path \"{cachedPath}\"",
                cancellationToken);

            if (installExitCode == 0)
            {
                var exeName = OperatingSystem.IsWindows() ? "dotnet-trace.exe" : "dotnet-trace";
                var exePath = Path.Combine(cachedPath, exeName);
                if (File.Exists(exePath)) return exePath;
            }
        }
        catch
        {
            // Ignore install failures
        }

        return null;
    }

    public async Task<(string Message, int? BreakpointId)> SetBreakpointAsync(
        string filePath, int line, string? condition = null, string? hitCondition = null,
        string? logMessage = null, CancellationToken cancellationToken = default)
    {
        // hitCondition and logMessage are emulated a layer up; netcoredbg has no MI for either.
        _ = (hitCondition, logMessage);

        if (_state == DebugState.NotStarted)
            return ("Error: No active debug session.", null);

        var normalizedPath = PathHelper.NormalizePath(filePath);
        var conditionArg = !string.IsNullOrWhiteSpace(condition)
            ? $" -c \"{EscapeMiString(condition)}\""
            : "";

        // A decompiled or fetched file names no path netcoredbg's symbol lookup knows. There is
        // no IL-offset insert over MI, so the closest honest translations are: the path the PDB
        // itself records for the line (embedded/Source Link — exact), or the method's name
        // (decompiled/reference source — the entry of the method holding the line).
        string note = "";
        string locationArg = $"\"{EscapeMiString(normalizedPath)}:{line}\"";
        var mapped = await ExternalSource.DebugSourceMapper.TryMapAsync(
            normalizedPath, line, cancellationToken);
        if (mapped is not null)
        {
            if (mapped.DocumentPath.Length > 0)
            {
                locationArg = $"\"{EscapeMiString(mapped.DocumentPath)}:{mapped.Line}\"";
                line = mapped.Line;
                note = $" ({mapped.Origin})";
            }
            else if (mapped.MethodDisplayName.Length > 0)
            {
                locationArg = $"\"{EscapeMiString(mapped.MethodDisplayName)}\"";
                note = $" ({mapped.Origin}: bound at the entry of {mapped.MethodDisplayName} — " +
                       "netcoredbg cannot target an IL offset inside it)";
            }
        }

        var response = await SendCommandAsync($"-break-insert{conditionArg} {locationArg}", cancellationToken);

        var match = BreakpointInsertedRegex().Match(response);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var bpNumber))
        {
            _breakpoints[bpNumber] = new BreakpointInfo(bpNumber, normalizedPath, line);
            var conditionNote = !string.IsNullOrWhiteSpace(condition) ? $" (condition: {condition})" : "";
            return ($"Breakpoint #{bpNumber} set at {Path.GetFileName(normalizedPath)}:{line}{conditionNote}{note}", bpNumber);
        }

        if (response.Contains("^error", StringComparison.Ordinal))
            return ($"Error setting breakpoint: {ExtractError(response)}", null);

        return ($"Breakpoint set at {Path.GetFileName(normalizedPath)}:{line}", null);
    }

    public async Task<string> RemoveBreakpointAsync(int breakpointId, CancellationToken cancellationToken = default)
    {
        if (_state == DebugState.NotStarted)
            return "Error: No active debug session.";

        await SendCommandAsync($"-break-delete {breakpointId}", cancellationToken);
        _breakpoints.TryRemove(breakpointId, out _);
        return $"Breakpoint #{breakpointId} removed.";
    }

    public async Task<string> ContinueAsync(CancellationToken cancellationToken = default)
    {
        if (_state == DebugState.NotStarted)
            return "Error: No active debug session.";

        _state = DebugState.Running;
        lock (_outputLock) _currentFrame = null;

        await SendCommandAsync("-exec-continue", cancellationToken);

        // Wait for stopped event or process exit
        var result = await WaitForStopAsync(cancellationToken);
        return result;
    }

    public async Task<string> StepInAsync(CancellationToken cancellationToken = default)
    {
        if (_state != DebugState.Stopped)
            return "Error: Debugger is not stopped. Cannot step.";

        _state = DebugState.Running;
        await SendCommandAsync("-exec-step", cancellationToken);
        return await WaitForStopAsync(cancellationToken);
    }

    public async Task<string> StepOverAsync(CancellationToken cancellationToken = default)
    {
        if (_state != DebugState.Stopped)
            return "Error: Debugger is not stopped. Cannot step.";

        _state = DebugState.Running;
        await SendCommandAsync("-exec-next", cancellationToken);
        return await WaitForStopAsync(cancellationToken);
    }

    public async Task<string> StepOutAsync(CancellationToken cancellationToken = default)
    {
        if (_state != DebugState.Stopped)
            return "Error: Debugger is not stopped. Cannot step.";

        _state = DebugState.Running;
        await SendCommandAsync("-exec-finish", cancellationToken);
        return await WaitForStopAsync(cancellationToken);
    }

    public async Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default)
    {
        if (_state != DebugState.Stopped)
            return "Error: Debugger is not stopped. Cannot evaluate.";

        // Use unique variable name via Interlocked to avoid races
        var varName = $"eval{Interlocked.Increment(ref _tokenCounter)}";
        try
        {
            var response = await SendCommandAsync($"-var-create {varName} * \"{EscapeMiString(expression)}\"", cancellationToken);

            var value = ExtractMiField(response, "value");
            if (value is not null)
                return value;

            if (response.Contains("^error", StringComparison.Ordinal))
                return $"Error: {ExtractError(response)}";

            return response;
        }
        finally
        {
            // Always clean up the variable, even if cancelled
            try { await SendCommandAsync($"-var-delete {varName}", CancellationToken.None); }
            catch { /* best effort cleanup */ }
        }
    }

    public async Task<string> GetLocalsAsync(CancellationToken cancellationToken = default)
    {
        if (_state != DebugState.Stopped)
            return "Error: Debugger is not stopped.";

        var response = await SendCommandAsync("-stack-list-variables 1", cancellationToken);
        return FormatLocals(response);
    }

    public async Task<string> GetStackTraceAsync(CancellationToken cancellationToken = default)
    {
        if (_state != DebugState.Stopped)
            return "Error: Debugger is not stopped.";

        return FormatStackTrace(await GetStackFramesAsync(cancellationToken));
    }

    public string GetStatus()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"**State:** {_state}");

        if (!_breakpoints.IsEmpty)
        {
            sb.AppendLine();
            sb.AppendLine("**Breakpoints:**");
            foreach (var bp in _breakpoints.Values.OrderBy(b => b.Id))
            {
                var fileName = string.IsNullOrEmpty(bp.FilePath)
                    ? $"breakpoint {bp.Id}"
                    : $"{Path.GetFileName(bp.FilePath.Replace('\\', '/'))}:{bp.Line}";
                sb.AppendLine($"  #{bp.Id} — {fileName}");
            }
        }

        StoppedFrame? frame;
        lock (_outputLock) frame = _currentFrame;
        if (frame is not null)
        {
            sb.AppendLine();
            sb.Append(FormatCurrentPosition(frame));
        }

        return sb.ToString();
    }

    public string Stop()
    {
        _sessionCts?.Cancel();
        CleanupProcesses();
        _breakpoints.Clear();
        lock (_outputLock) _currentFrame = null;
        _state = DebugState.NotStarted;
        return "Debug session stopped.";
    }

    public void Dispose()
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        CleanupProcesses();
        _commandLock.Dispose();
    }

    // --- Private helpers ---

    private static readonly string s_toolsDirectory = Path.Combine(
        Path.GetTempPath(), "RoslynMCP", "Tools");

    /// <summary>True when the debugger is already on disk, so callers can decide whether to
    /// announce a download before one happens.</summary>
    public static bool HasCachedNetcoredbg() => GetCachedNetcoredbgPath() is not null;

    /// <summary>
    /// Locates netcoredbg — tools cache, PATH, common install locations — downloading it as a
    /// last resort. Shared by the AI-owned MI sessions and the editor's DAP sessions, which
    /// both drive the same binary in different interpreter modes.
    /// </summary>
    public static async Task<string?> FindOrProvisionNetcoredbgAsync(CancellationToken cancellationToken)
    {
        // 1. Check our tools cache first
        var cachedPath = GetCachedNetcoredbgPath();
        if (cachedPath is not null)
            return cachedPath;

        // 2. Check PATH
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".exe";
        var extensions = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);

        foreach (var dir in pathDirs)
        {
            var basePath = Path.Combine(dir, "netcoredbg");
            if (File.Exists(basePath)) return basePath;
            foreach (var ext in extensions)
            {
                var fullPath = basePath + ext;
                if (File.Exists(fullPath)) return fullPath;
            }
        }

        // 3. Check common installation locations
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] commonPaths =
        [
            Path.Combine(programFiles, "netcoredbg", "netcoredbg.exe"),
            Path.Combine(programFiles, "netcoredbg", "netcoredbg"),
            "/usr/local/bin/netcoredbg",
            "/usr/bin/netcoredbg"
        ];

        foreach (var path in commonPaths)
        {
            if (File.Exists(path)) return path;
        }

        // 4. Auto-download
        return await DownloadNetcoredbgAsync(cancellationToken);
    }

    private static string? GetCachedNetcoredbgPath()
    {
        var dir = Path.Combine(s_toolsDirectory, "netcoredbg");
        if (!Directory.Exists(dir)) return null;

        var exePath = Path.Combine(dir, "netcoredbg.exe");
        if (File.Exists(exePath)) return exePath;

        exePath = Path.Combine(dir, "netcoredbg");
        if (File.Exists(exePath)) return exePath;

        return null;
    }

    private static async Task<string?> DownloadNetcoredbgAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RoslynSense/1.0");

            // Get latest release info
            var releaseUrl = "https://api.github.com/repos/Samsung/netcoredbg/releases/latest";
            var releaseJson = await http.GetStringAsync(releaseUrl, cancellationToken);

            var assetName = OperatingSystem.IsWindows() ? "netcoredbg-win64.zip"
                : OperatingSystem.IsMacOS() ? "netcoredbg-osx-amd64.tar.gz"
                : "netcoredbg-linux-amd64.tar.gz";

            // Extract download URL from JSON
            var assetUrlPattern = $"\"browser_download_url\":\"(https://[^\"]*{assetName.Replace(".", "\\.")})\"";
            var match = Regex.Match(releaseJson, assetUrlPattern);
            if (!match.Success) return null;

            var downloadUrl = match.Groups[1].Value;

            var installDir = Path.Combine(s_toolsDirectory, "netcoredbg");
            Directory.CreateDirectory(installDir);

            var tempFile = Path.Combine(s_toolsDirectory, assetName);

            // Download
            using (var stream = await http.GetStreamAsync(downloadUrl, cancellationToken))
            using (var fileStream = File.Create(tempFile))
            {
                await stream.CopyToAsync(fileStream, cancellationToken);
            }

            // Extract
            if (tempFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(tempFile, s_toolsDirectory, overwriteFiles: true);
            }
            else
            {
                // tar.gz — use tar command
                var (exitCode, _) = await RunProcessAsync("tar", $"xzf \"{tempFile}\" -C \"{s_toolsDirectory}\"", cancellationToken);
                if (exitCode != 0) return null;
            }

            File.Delete(tempFile);
            return GetCachedNetcoredbgPath();
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> StartNetcoredbgAsync(
        string netcoredbgPath,
        int pid,
        IEnumerable<(string file, int line)>? immediateBreakpoints,
        CancellationToken cancellationToken)
    {
        _netcoredbgProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = netcoredbgPath,
                Arguments = $"--interpreter=mi --attach {pid}",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _netcoredbgProcess.Start();

        // Write breakpoint commands to stdin IMMEDIATELY after starting netcoredbg,
        // before the library-loading phase completes. VSTEST_HOST_DEBUG pauses the
        // test host until a debugger attaches; once attached the test runs instantly.
        // Breakpoints sent now will be resolved during library loading and be active
        // when -exec-continue is issued later.
        if (immediateBreakpoints is not null)
        {
            foreach (var (file, line) in immediateBreakpoints)
            {
                var normalizedPath = PathHelper.NormalizePath(file);
                var escapedPath = EscapeMiString(normalizedPath);
                await _netcoredbgProcess.StandardInput.WriteLineAsync(
                    $"-break-insert \"{escapedPath}:{line}\"".AsMemory(), cancellationToken);
            }
            await _netcoredbgProcess.StandardInput.FlushAsync(cancellationToken);
        }

        // Use a session-scoped CTS so the output loop survives individual request cancellations
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = new CancellationTokenSource();

        // Start reading stdout asynchronously (session-scoped)
        _ = Task.Run(() => ReadOutputLoop(_sessionCts.Token));

        // Drain stderr in the background to prevent pipe buffer deadlock
        _ = Task.Run(() => DrainStderrAsync(_sessionCts.Token));

        // Wait for initial response
        await Task.Delay(500, cancellationToken);

        // Check if process is still running
        if (_netcoredbgProcess.HasExited)
        {
            var error = await _netcoredbgProcess.StandardError.ReadToEndAsync(cancellationToken);
            return $"Error: netcoredbg exited immediately. {error}";
        }

        await ApplyViewSettingsAsync(cancellationToken);

        return null;
    }

    /// <summary>
    /// Tells netcoredbg what it can be told about the debugger attributes.
    /// </summary>
    /// <remarks>
    /// Only Just My Code: netcoredbg implements <c>DebuggerDisplay</c>, <c>DebuggerTypeProxy</c>
    /// and <c>DebuggerBrowsable</c> itself with no switch to turn them off, so those settings
    /// govern the ICorDebug engine (.NET Framework targets) alone. Sent only when it differs from
    /// the default, and failures are ignored — an older build answers <c>^error</c> and keeps its
    /// own default, which is the behaviour we would fall back to anyway.
    /// </remarks>
    private async Task ApplyViewSettingsAsync(CancellationToken cancellationToken)
    {
        if (Config.DebuggerViewOptions.Current.JustMyCode)
            return;

        try
        {
            await SendCommandAsync("-gdb-set just-my-code 0", cancellationToken);
        }
        catch
        {
            // Best effort: the session is usable either way.
        }
    }

    private async Task ReadOutputLoop(CancellationToken cancellationToken)
    {
        if (_netcoredbgProcess?.StandardOutput is null) return;

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   _netcoredbgProcess is { HasExited: false })
            {
                var line = await _netcoredbgProcess.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null) break;

                ProcessMiOutput(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private async Task DrainStderrAsync(CancellationToken cancellationToken)
    {
        if (_netcoredbgProcess?.StandardError is null) return;

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   _netcoredbgProcess is { HasExited: false })
            {
                // Read and discard stderr to prevent pipe buffer from filling
                var line = await _netcoredbgProcess.StandardError.ReadLineAsync(cancellationToken);
                if (line is null) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    internal void ProcessMiOutput(string line)
    {
        lock (_outputLock)
        {
            // Parse leading numeric token prefix (e.g. "5^done" → token=5, parseLine="^done")
            var parseLine = line;
            int responseToken = -1;
            var i = 0;
            while (i < parseLine.Length && char.IsDigit(parseLine[i])) i++;
            if (i > 0)
            {
                int.TryParse(parseLine.AsSpan(0, i), out responseToken);
                parseLine = parseLine[i..];
            }

            if (parseLine.StartsWith('~'))
            {
                // Console output
                var text = ExtractQuotedString(parseLine[1..]);
                if (text is not null)
                    _consoleOutput.Add(text);
            }
            else if (parseLine.StartsWith("*stopped", StringComparison.Ordinal))
            {
                _currentFrame = ParseStoppedFrame(parseLine);
                // Variable references name objects that only exist for this stop; carrying them
                // over would answer an expansion with a different object's fields.
                _handles.Reset();
                _selectedFrame = 0;
                // Distinguish exit from actual stop
                var reason = ExtractMiField(parseLine, "reason");
                _state = reason is "exited" or "exited-normally" or "exited-signalled"
                    ? DebugState.Exited
                    : DebugState.Stopped;
                // *stopped is an async event (no token) — always resolve
                _responseWaiter?.TrySetResult(parseLine);
            }
            else if (parseLine.StartsWith("*running", StringComparison.Ordinal))
            {
                _state = DebugState.Running;
            }
            else if (parseLine.StartsWith("^done", StringComparison.Ordinal) || parseLine.StartsWith("^error", StringComparison.Ordinal) || parseLine.StartsWith("^exit", StringComparison.Ordinal))
            {
                // Auto-track breakpoints from ^done,bkpt={number="N",...} responses
                if (parseLine.StartsWith("^done,bkpt=", StringComparison.Ordinal))
                {
                    var bpMatch = BreakpointInsertedRegex().Match(parseLine);
                    if (bpMatch.Success && int.TryParse(bpMatch.Groups[1].Value, out var bpNum))
                    {
                        var bpFile = ExtractMiField(parseLine, "fullname")
                            ?? ExtractMiField(parseLine, "file") ?? "";
                        var bpLineStr = ExtractMiField(parseLine, "line");
                        _ = int.TryParse(bpLineStr, out var bpLine);

                        // For pending breakpoints, try to extract path from orig-location
                        if (string.IsNullOrEmpty(bpFile))
                        {
                            var origLocation = ExtractMiField(parseLine, "original-location");
                            if (origLocation is not null)
                            {
                                var colonIdx = origLocation.LastIndexOf(':');
                                if (colonIdx > 0)
                                {
                                    bpFile = origLocation[..colonIdx];
                                    if (int.TryParse(origLocation[(colonIdx + 1)..], out var origLine))
                                        bpLine = origLine;
                                }
                            }
                        }

                        _breakpoints.TryAdd(bpNum, new BreakpointInfo(bpNum, bpFile, bpLine));
                    }
                }

                // Only resolve if the response token matches the expected command token,
                // preventing late responses from timed-out commands from resolving a new command's TCS
                if (responseToken < 0 || responseToken == _expectedToken)
                    _responseWaiter?.TrySetResult(parseLine);
            }

            if (parseLine == "(gdb)")
            {
                // Prompt — response cycle complete
            }
        }
    }

    private async Task<string> SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (_netcoredbgProcess is null or { HasExited: true })
            return "^error,msg=\"netcoredbg is not running\"";

        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            var token = Interlocked.Increment(ref _tokenCounter);
            lock (_outputLock)
            {
                _expectedToken = token;
                _responseWaiter = new TaskCompletionSource<string>();
            }

            await _netcoredbgProcess.StandardInput.WriteLineAsync(
                $"{token}{command}".AsMemory(), cancellationToken);
            await _netcoredbgProcess.StandardInput.FlushAsync(cancellationToken);

            // Wait for response with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                return await _responseWaiter.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return "^error,msg=\"Command timed out\"";
            }
        }
        finally
        {
            _responseWaiter = null;
            _commandLock.Release();
        }
    }

    /// <summary>
    /// Waits for the initial breakpoint hit or process exit after attaching to a test host.
    /// With VSTEST_HOST_DEBUG + JMC, the test auto-resumes after attach. If breakpoints
    /// were set immediately, this waits for them to be hit. Returns the formatted position
    /// if a breakpoint was hit, or null if the caller should tell the user to continue.
    /// </summary>
    private async Task<string?> WaitForInitialStopAsync(bool hasBreakpoints, CancellationToken cancellationToken)
    {
        // If no breakpoints were requested, just mark as stopped (waiting for user to set breakpoints and continue)
        if (!hasBreakpoints)
        {
            _state = DebugState.Stopped;
            return null;
        }

        // Wait for the test to hit a breakpoint or exit (timeout after 10s)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                StoppedFrame? frame;
                lock (_outputLock) { frame = _currentFrame; }

                if (_state == DebugState.Stopped && frame is not null)
                    return FormatCurrentPosition(await EnrichStoppedFrameAsync(frame, cancellationToken));

                if (_state == DebugState.Exited)
                    return "⏹ Test execution completed without hitting a breakpoint.";

                if (_netcoredbgProcess is null or { HasExited: true })
                {
                    _state = DebugState.Exited;
                    return "⏹ Test execution completed without hitting a breakpoint.";
                }

                if (_testHostProcess is { HasExited: true })
                {
                    _state = DebugState.Exited;
                    return "⏹ Test execution completed without hitting a breakpoint.";
                }

                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out — the test might still be loading assemblies
        }

        // If we timed out but state is stopped with a frame, report it
        {
            StoppedFrame? frame;
            lock (_outputLock) { frame = _currentFrame; }
            if (_state == DebugState.Stopped && frame is not null)
                return FormatCurrentPosition(await EnrichStoppedFrameAsync(frame, cancellationToken));
        }

        // Otherwise, set state to stopped so the user can interact
        _state = DebugState.Stopped;
        return null;
    }

    private async Task<string> WaitForStopAsync(CancellationToken cancellationToken)
    {
        // Wait for either a *stopped event or process exit
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            while (_state == DebugState.Running && !cts.Token.IsCancellationRequested)
            {
                await Task.Delay(100, cts.Token);

                if (_netcoredbgProcess is null or { HasExited: true })
                {
                    _state = DebugState.Exited;
                    return "⏹ Program has exited.";
                }

                if (_testHostProcess is { HasExited: true })
                {
                    _state = DebugState.Exited;
                    return "⏹ Test execution completed.";
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "⏳ Timed out waiting for breakpoint. Use 'status' to check state.";
        }

        StoppedFrame? frame;
        lock (_outputLock) frame = _currentFrame;
        if (_state == DebugState.Stopped && frame is not null)
            return FormatCurrentPosition(await EnrichStoppedFrameAsync(frame, cancellationToken));

        if (_state == DebugState.Exited)
            return "⏹ Program has exited.";

        return "Debugger stopped.";
    }

    /// <summary>
    /// A stop with no source — external code, Just My Code off — resolved to the source the
    /// frame's module can yield: the PDB's own document, the reference source, or a
    /// decompilation. The stored current frame is updated too, so the editor's fallback frame
    /// and the status surface agree with what the step just printed.
    /// </summary>
    private async Task<StoppedFrame> EnrichStoppedFrameAsync(
        StoppedFrame frame, CancellationToken cancellationToken)
    {
        if (frame.FilePath.Length > 0 || frame.MethodToken == 0
            || frame.ModuleId.Length == 0 || frame.IlOffset < 0)
        {
            return frame;
        }

        try
        {
            if (ModulePathForId(frame.ModuleId) is null)
                await RefreshModuleMapAsync(cancellationToken);

            if (ModulePathForId(frame.ModuleId) is not { } modulePath)
                return frame;

            var resolved = await ExternalSource.DebugFrameSource.TryResolveAsync(
                modulePath, frame.MethodToken, frame.IlOffset, allowDecompile: true,
                cancellationToken);
            if (resolved is null)
                return frame;

            var enriched = frame with
            {
                FilePath = resolved.FilePath,
                Line = resolved.Line,
                SourceOrigin = resolved.Origin,
            };

            lock (_outputLock)
            {
                if (ReferenceEquals(_currentFrame, frame))
                    _currentFrame = enriched;
            }

            return enriched;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not resolve external source for the stop location: {ex.Message}",
                key: "debug-stop-source");
            return frame;
        }
    }

    private static string FormatCurrentPosition(StoppedFrame frame)
    {
        var sb = new StringBuilder();

        var reasonText = frame.Reason switch
        {
            "breakpoint-hit" => $"⏸ Paused at breakpoint #{frame.BreakpointNumber}",
            "end-stepping-range" => "⏸ Step completed",
            "function-finished" => "⏸ Function returned",
            "exited-normally" => "⏹ Program exited normally",
            "exited" => "⏹ Program exited",
            "signal-received" => "⏸ Signal received",
            _ => $"⏸ Stopped ({frame.Reason})"
        };

        sb.AppendLine(reasonText);
        var origin = frame.SourceOrigin.Length > 0 ? $" ({frame.SourceOrigin})" : "";
        sb.AppendLine($"📍 {frame.Function} at {Path.GetFileName(frame.FilePath)}:{frame.Line}{origin}");

        // Show code context if file exists
        if (!string.IsNullOrEmpty(frame.FilePath) && File.Exists(frame.FilePath))
        {
            try
            {
                var lines = File.ReadAllLines(frame.FilePath);
                var startLine = Math.Max(0, frame.Line - 4); // 3 lines before
                var endLine = Math.Min(lines.Length - 1, frame.Line + 2); // 2 lines after

                sb.AppendLine();
                for (var i = startLine; i <= endLine; i++)
                {
                    var lineNum = i + 1;
                    var marker = lineNum == frame.Line ? " → " : "   ";
                    sb.AppendLine($"{marker}{lineNum,4} | {lines[i]}");
                }
            }
            catch
            {
                // Ignore file read errors
            }
        }

        return sb.ToString();
    }

    internal static StoppedFrame ParseStoppedFrame(string line)
    {
        var reason = ExtractMiField(line, "reason") ?? "unknown";
        var func = ExtractMiField(line, "func") ?? "unknown";
        var file = ExtractMiField(line, "fullname") ?? ExtractMiField(line, "file") ?? "";
        var lineStr = ExtractMiField(line, "line") ?? "0";
        int.TryParse(lineStr, out var lineNum);
        var bpNoStr = ExtractMiField(line, "bkptno") ?? "0";
        int.TryParse(bpNoStr, out var bpNo);

        var (moduleId, methodToken, ilOffset) = ParseClrAddress(line);

        return new StoppedFrame(
            reason, func, file, lineNum, bpNo,
            ExceptionName: ExtractMiField(line, "exception-name"),
            ExceptionMessage: ExtractMiField(line, "exception"),
            ExceptionStage: ExtractMiField(line, "exception-stage"),
            ModuleId: moduleId,
            MethodToken: methodToken,
            IlOffset: ilOffset);
    }

    /// <summary>
    /// The <c>clr-addr={module-id=...,method-token=...,il-offset=...}</c> block netcoredbg puts
    /// on managed frames — the identity external-source resolution needs when the frame has no
    /// file.
    /// </summary>
    internal static (string ModuleId, int MethodToken, int IlOffset) ParseClrAddress(string text)
    {
        string moduleId = ExtractMiField(text, "module-id")?.Trim('{', '}') ?? "";

        int methodToken = 0;
        if (ExtractMiField(text, "method-token") is { Length: > 0 } tokenText)
        {
            string digits = tokenText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? tokenText[2..]
                : tokenText;
            int.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out methodToken);
        }

        int ilOffset = -1;
        if (ExtractMiField(text, "il-offset") is { Length: > 0 } offsetText)
            int.TryParse(offsetText, out ilOffset);

        return (moduleId, methodToken, ilOffset);
    }

    internal static string FormatLocals(string response)
    {
        if (response.Contains("^error", StringComparison.Ordinal))
            return $"Error: {ExtractError(response)}";

        // netcoredbg's -stack-list-variables answers with variables=[]; plain MI uses locals=[].
        if (!response.Contains("variables=[", StringComparison.Ordinal) &&
            !response.Contains("locals=[", StringComparison.Ordinal))
        {
            return "No local variables.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("**Local Variables:**");
        foreach (var (name, value) in ParseNameValueList(response))
            sb.AppendLine($"  {name} = {value}");

        return sb.ToString();
    }

    internal static string FormatStackTrace(string response)
    {
        if (response.Contains("^error", StringComparison.Ordinal))
            return $"Error: {ExtractError(response)}";

        return FormatStackTrace(ParseStackFrames(response));
    }

    internal static string FormatStackTrace(IReadOnlyList<Debugging.StackFrameInfo> frames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("**Call Stack:**");

        var skippedCount = 0;

        foreach (var frame in frames)
        {
            // Frames with nothing to say, and runtime transitions, collapse into a count. A frame
            // whose source was resolved externally has plenty to say and stays visible.
            if (frame.IsExternal && frame.FilePath.Length == 0)
            {
                skippedCount++;
                continue;
            }

            if (skippedCount > 0)
            {
                sb.AppendLine($"  ... ({skippedCount} framework frame{(skippedCount == 1 ? "" : "s")})");
                skippedCount = 0;
            }

            var fileDisplay = string.IsNullOrEmpty(frame.FilePath)
                ? ""
                : $" at {Path.GetFileName(frame.FilePath.Replace('\\', '/'))}:{frame.Line}";
            var origin = frame.SourceOrigin.Length > 0 ? $" ({frame.SourceOrigin})" : "";
            sb.AppendLine($"  #{frame.Id} {frame.Name}{fileDisplay}{origin}");
        }

        if (skippedCount > 0)
            sb.AppendLine($"  ... ({skippedCount} framework frame{(skippedCount == 1 ? "" : "s")})");

        return sb.ToString();
    }

    internal static string? ExtractMiField(string text, string fieldName)
    {
        var pattern = fieldName + "=\"";
        var start = text.IndexOf(pattern, StringComparison.Ordinal);
        if (start < 0) return null;
        start += pattern.Length;

        var sb = new StringBuilder();
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            if (escaped)
            {
                // Translate the escape rather than merely dropping the backslash: a Windows path
                // read as a control character, or a newline read as the letter n, is a value the
                // caller cannot tell apart from one the target really holds.
                sb.Append(text[i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    var other => other,
                });
                escaped = false;
                continue;
            }
            if (text[i] == '\\') { escaped = true; continue; }
            if (text[i] == '"') break;
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    internal static string? ExtractQuotedString(string text)
    {
        if (text.Length < 2 || text[0] != '"') return null;
        return ExtractMiField("x=" + text, "x");
    }

    internal static string ExtractError(string response)
    {
        return ExtractMiField(response, "msg") ?? "Unknown error";
    }

    internal static string EscapeMiString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    internal static string UnescapeMiString(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                switch (value[i + 1])
                {
                    case '\\': sb.Append('\\'); i++; break;
                    case '"': sb.Append('"'); i++; break;
                    case 'n': sb.Append('\n'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    default: sb.Append(value[i]); break;
                }
            }
            else
            {
                sb.Append(value[i]);
            }
        }
        return sb.ToString();
    }

    private static async Task<(int exitCode, string output)> RunProcessAsync(
        string fileName, string arguments, CancellationToken cancellationToken)
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
                CreateNoWindow = true
            }
        };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, $"'{fileName}' not found on PATH");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return (process.ExitCode, output.ToString());
    }

    private void CleanupProcesses()
    {
        try
        {
            if (_netcoredbgProcess is { HasExited: false })
            {
                try { _netcoredbgProcess.StandardInput.WriteLine("-gdb-exit"); } catch { }
                if (!_netcoredbgProcess.WaitForExit(2000))
                    try { _netcoredbgProcess.Kill(entireProcessTree: true); } catch { }
            }
            _netcoredbgProcess?.Dispose();
            _netcoredbgProcess = null;
        }
        catch { }

        try
        {
            if (_testHostProcess is { HasExited: false })
                try { _testHostProcess.Kill(entireProcessTree: true); } catch { }
            _testHostProcess?.Dispose();
            _testHostProcess = null;
        }
        catch { }
    }

    public enum DebugState
    {
        NotStarted,
        Starting,
        Running,
        Stopped,
        Exited
    }

    public sealed record BreakpointInfo(int Id, string FilePath, int Line);
    /// <param name="ModuleId">The MVID of the module the stop is in, when the engine said —
    /// what external-source resolution keys on for a frame with no <paramref name="FilePath"/>.</param>
    /// <param name="MethodToken">The stopped method's MethodDef token; 0 when unknown.</param>
    /// <param name="IlOffset">The IP within that method's IL; -1 when unknown.</param>
    /// <param name="SourceOrigin">Set when <paramref name="FilePath"/> was resolved from
    /// external source rather than reported by the engine: <c>embedded</c>, <c>source link</c>,
    /// <c>reference source</c> or <c>decompiled</c>.</param>
    public sealed record StoppedFrame(
        string Reason,
        string Function,
        string FilePath,
        int Line,
        int BreakpointNumber,
        string? ExceptionName = null,
        string? ExceptionMessage = null,
        string? ExceptionStage = null,
        string ModuleId = "",
        int MethodToken = 0,
        int IlOffset = -1,
        string SourceOrigin = "");
}
