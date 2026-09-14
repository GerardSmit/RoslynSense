using System.Diagnostics;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.HotReload;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Hot reload on CoreCLR, end to end and through the product's own path.
/// </summary>
/// <remarks>
/// <para>
/// Everything else about this feature is tested in pieces — the launch environment, the module
/// identity, the agent's wire protocol against a fake on the other end. None of that answers the
/// only question that matters: does a running application actually change what it does. This does,
/// by building a real project, launching it, editing its source, and watching the number it prints
/// change without it restarting.
/// </para>
/// <para>
/// Deliberately through <see cref="HotReloadService"/> rather than a hand-built delta. Roslyn's
/// EnC engine constructs the baseline from the project's own PDB; a harness that stubs that out
/// proves nothing about the code that ships. It also means a failure here is a failure of the
/// product, which is the point of the test.
/// </para>
/// </remarks>
[Collection(DebuggerCollection.Name)]
public class CoreClrHotReloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hotreload-e2e-{Guid.NewGuid():N}");
    private readonly System.Text.StringBuilder _targetOutput = new();

    private static readonly string Project = $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net{Environment.Version.Major}.0</TargetFramework>
            <Nullable>disable</Nullable>
            <AssemblyName>HotReloadTarget</AssemblyName>
            <RootNamespace>HotReloadTarget</RootNamespace>
            <!-- Optimised code is not updatable; Debug is what hot reload is for anyway. -->
            <Optimize>false</Optimize>
            <DebugType>portable</DebugType>
          </PropertyGroup>
        </Project>
        """;

    private const string BaselineSource = """
        using System;
        using System.IO;
        using System.Threading;

        namespace HotReloadTarget
        {
            public static class Program
            {
                public static int Compute(int input)
                {
                    return input * 2;
                }

                public static void Main(string[] args)
                {
                    for (int i = 0; i < 100000; i++)
                    {
                        File.AppendAllText(args[0], Compute(3) + Environment.NewLine);
                        Thread.Sleep(50);
                    }
                }
            }
        }
        """;

    private static readonly string GatedSource = BaselineSource.Replace(
        "for (int i = 0;",
        "while (!File.Exists(args[0] + \".go\")) Thread.Sleep(10);\n            for (int i = 0;");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AnEditReachesTheRunningProcessWithoutRestartingIt(bool useDotnetHost) =>
        VerifyRuntimeUpdatesAsync(useDotnetHost, attachDebugger: false);

    [Fact]
    public Task RuntimeUpdaterReportsTheDebuggerRestriction() =>
        VerifyRuntimeUpdatesAsync(useDotnetHost: true, attachDebugger: true);

    /// <summary>
    /// The F5 case: the debugger started the process, so the runtime turns its own updater away
    /// and the edit reaches the process through the debugger's <c>ApplyChanges</c> instead, the
    /// way VS hot reloads a .NET app under the debugger.
    /// </summary>
    [Fact]
    public async Task AnEditAppliesThroughTheDebuggerThatLaunchedTheProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (csproj, sourcePath, exe, log) = await BuildTargetAsync();

        var previousEngine = Config.DebugEngineOptions.CoreClr;
        Config.DebugEngineOptions.CoreClr = Config.CoreClrDebugEngine.IcorDebug;
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(Services.DebugRuntime.CoreClr);
        Process? target = null;
        try
        {
            // The same environment an F5 launch gets, and the bare "dotnet" a project without
            // an apphost is started with.
            var environment = HotReloadHandler.Environment();
            Assert.True(environment.Available, environment.Message);
            string launched = await backend.LaunchAsync(
                "dotnet", [Path.ChangeExtension(exe, ".dll"), log], environment.Variables,
                Path.GetDirectoryName(exe));
            Assert.False(launched.Contains("Error:"), launched);
            // The pid arrives with the engine's process-created event, a moment after the launch
            // call returns.
            Assert.True(await WaitAsync(() => backend.DebuggeePid is not null), launched);
            target = Process.GetProcessById(backend.DebuggeePid!.Value);

            Assert.True(await WaitAsync(() => HotReloadAgentServer.Instance.Targets
                    .Any(t => t.ProcessId == target.Id)),
                "The hot reload agent never connected." + Diagnose(target));
            await ResumeUntilValueAsync(backend, log, "6");

            await EditTwiceThroughTheDebuggerAsync(backend, csproj, sourcePath, log, target);
        }
        finally
        {
            await TearDownAsync(csproj, backend, previousEngine, target);
        }
    }

    /// <summary>
    /// The attach case: the app was started for hot reload (with
    /// <c>DOTNET_MODIFIABLE_ASSEMBLIES=debug</c>, as Run with Hot Reload does) and the debugger
    /// arrived later. The runtime decided the modules were updatable when they loaded, so the
    /// debugger's <c>ApplyChanges</c> works on them even though the EnC JIT flag can no longer be
    /// set. VS attaches to such a process and hot reloads it the same way. Both halves matter: a
    /// method the runtime compiled before the attach has no debugger record until the apply
    /// creates one, which once made the engine remap every call back into the old code, so the
    /// edited method is exercised both already compiled and not yet compiled at attach time.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnEditAppliesThroughADebuggerThatAttachedLater(bool jitBeforeAttach)
    {
        if (!OperatingSystem.IsWindows()) return;
        var (csproj, sourcePath, exe, log) = await BuildTargetAsync();
        string source = BaselineSource;
        if (!jitBeforeAttach)
        {
            source = GatedSource;
            await File.WriteAllTextAsync(sourcePath, source);
            Assert.True(await BuildAsync(csproj));
        }

        var previousEngine = Config.DebugEngineOptions.CoreClr;
        Config.DebugEngineOptions.CoreClr = Config.CoreClrDebugEngine.IcorDebug;
        var target = Launch(exe, log, useDotnetHost: true);
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(Services.DebugRuntime.CoreClr);
        try
        {
            Assert.True(await WaitAsync(() => HotReloadAgentServer.Instance.Targets
                    .Any(t => t.ProcessId == target.Id)),
                "The hot reload agent never connected." + Diagnose(target));
            if (jitBeforeAttach)
            {
                Assert.True(await WaitForLastLineAsync(log, "6"),
                    "The target never produced its baseline value." + Diagnose(target));
            }

            string attached = await backend.AttachToProcessAsync(target.Id);
            Assert.False(attached.Contains("Error:"), attached);
            if (!jitBeforeAttach)
            {
                File.WriteAllText(log, "");
                File.WriteAllText(log + ".go", "");
            }
            await ResumeUntilValueAsync(backend, log, "6");

            await EditTwiceThroughTheDebuggerAsync(backend, csproj, sourcePath, log, target, source);
        }
        finally
        {
            await TearDownAsync(csproj, backend, previousEngine, target);
        }
    }

    private async Task EditTwiceThroughTheDebuggerAsync(
        RoslynMCP.Services.Debugging.PublishingDebugBackend backend,
        string csproj, string sourcePath, string log, Process target, string? source = null)
    {
        source ??= BaselineSource;
        var (session, message) = await HotReloadService.StartAsync(csproj);
        Assert.True(session is not null, message);

        await File.WriteAllTextAsync(sourcePath, source.Replace("input * 2", "input * 10"));
        var outcome = await session!.ApplyAsync();
        Assert.True(outcome.Ok,
            $"{outcome.Summary}\n" +
            string.Join("\n", outcome.Diagnostics.Select(d => $"{d.Severity} {d.Id}: {d.Message}")) +
            string.Join("\n", outcome.Errors) + "\n--- engine ---\n" + backend.GetStatus());
        Assert.Contains(outcome.AppliedTo, a => a.Contains("debuggee", StringComparison.Ordinal));
        // The updater's refusal is the expected shape of a debugged process, not an error.
        Assert.Empty(outcome.Errors);
        Assert.True(await WaitForLastLineAsync(log, "30"),
            "The delta was reported as applied but the process kept returning the old value." +
            Diagnose(target) +
            "\n--- log tail ---\n" + string.Join("|", File.ReadAllLines(log).TakeLast(8)) +
            "\n--- outcome ---\n" + outcome.Summary + "\n" + string.Join("\n", outcome.AppliedTo) +
            "\n--- engine ---\n" + backend.GetStatus());
        Assert.False(target.HasExited, Diagnose(target));

        // A second edit diffs against the first, not against the build.
        await File.WriteAllTextAsync(sourcePath, source.Replace("input * 2", "input * 20"));
        var second = await session.ApplyAsync();
        Assert.True(second.Ok, second.Summary + "\n" + string.Join("\n", second.Errors));
        Assert.True(await WaitForLastLineAsync(log, "60"),
            "The second delta did not reach the process." + Diagnose(target) +
            "\n--- engine ---\n" + backend.GetStatus());
        Assert.False(target.HasExited, Diagnose(target));
    }

    private static async Task TearDownAsync(
        string csproj, RoslynMCP.Services.Debugging.PublishingDebugBackend backend,
        Config.CoreClrDebugEngine previousEngine, Process? target)
    {
        try { HotReloadService.Get(csproj)?.Stop(); } catch { }
        try { backend.Stop(); } catch { }
        DebugSessionManager.DisposeSession();
        Config.DebugEngineOptions.CoreClr = previousEngine;
        if (target is not null)
        {
            using (target)
            {
                try
                {
                    if (!target.HasExited)
                        target.Kill(entireProcessTree: true);
                    await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static async Task ResumeUntilValueAsync(
        RoslynMCP.Services.Debugging.PublishingDebugBackend backend, string log, string value)
    {
        // Continue waits for a future stop; once the output proves the app is running, cancel
        // that wait so a later pause has its own waiter for the backend's single stop signal.
        using var cancellation = new CancellationTokenSource();
        var continuing = backend.ContinueAsync(cancellation.Token);
        try
        {
            long previousLength = new FileInfo(log).Length;
            Assert.True(await WaitForLastLineAsync(log, value, previousLength),
                $"The target did not produce {value} after resuming.\n" + backend.GetStatus());
        }
        finally
        {
            cancellation.Cancel();
            try { await continuing.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsavedEditorChangesReachTheRunningProcessThroughTheLspHandler(bool useDifferentPathCasing)
    {
        var (csproj, sourcePath, exe, log) = await BuildTargetAsync(useDifferentPathCasing);
        using var editor = new LspServer(new EmptyServices());
        using var target = Launch(exe, log, useDotnetHost: true, editor.HotReloadEnvironment());
        var request = new HotReloadParams(csproj);
        // VS Code builds with a lowercase drive letter, while its document URI and the
        // workspace can use uppercase. The PDB keeps the compiler's exact spelling.
        string editorPath = useDifferentPathCasing && OperatingSystem.IsWindows()
            ? sourcePath.ToUpperInvariant()
            : sourcePath;
        string uri = LspConverters.PathToUri(editorPath);
        try
        {
            Assert.True(await WaitAsync(() => editor.HotReloadStatus().Targets.Any(t => t.ProcessId == target.Id)),
                "The editor could not discover the hot reload target." + Diagnose(target));
            Assert.True(await WaitForLastLineAsync(log, "6"), Diagnose(target));

            editor.DidOpen(new DidOpenTextDocumentParams(new TextDocumentItem(uri, "csharp", 1, BaselineSource)));
            var started = await editor.HotReloadStart(request, CancellationToken.None);
            Assert.True(started.Ok, started.Summary);

            string firstText = BaselineSource.Replace("input * 2", "input * 10");
            editor.DidChange(new DidChangeTextDocumentParams(
                new VersionedTextDocumentIdentifier(uri, 2),
                [new TextDocumentContentChangeEvent(null, firstText)]));

            await AssertEditorUpdateAsync(editor, request, target, log, "30");

            var unchanged = await editor.HotReloadApply(request, CancellationToken.None);
            Assert.True(unchanged.Ok, unchanged.Summary);
            Assert.Equal("No changes to apply.", unchanged.Summary);
            Assert.Empty(unchanged.AppliedTo);
            Assert.Empty(unchanged.Errors);

            // The next notification is a ranged edit against the existing editor buffer.
            Assert.True(OpenDocumentStore.TryGet(sourcePath, out var buffer));
            var start = buffer.Lines.GetLinePosition(firstText.IndexOf("input * 10", StringComparison.Ordinal));
            editor.DidChange(new DidChangeTextDocumentParams(
                new VersionedTextDocumentIdentifier(uri, 3),
                [new TextDocumentContentChangeEvent(
                    new RoslynMCP.Lsp.Protocol.Range(
                        new Position(start.Line, start.Character),
                        new Position(start.Line, start.Character + "input * 10".Length)),
                    "input * 20")]));

            await AssertEditorUpdateAsync(editor, request, target, log, "60");

            // No save occurred: successful deltas must have come from the notification text.
            Assert.Equal(BaselineSource, await File.ReadAllTextAsync(sourcePath));
            Assert.True(OpenDocumentStore.TryGet(sourcePath, out buffer));
            Assert.Equal(BaselineSource.Replace("input * 2", "input * 20"), buffer.ToString());

            var stopped = await editor.HotReloadStop(request);
            Assert.True(stopped.Ok, stopped.Summary);
            Assert.False(HotReloadService.IsRunning(csproj));
        }
        finally
        {
            editor.DidClose(new DidCloseTextDocumentParams(new TextDocumentIdentifier(uri)));
            HotReloadService.Get(csproj)?.Stop();
            if (!target.HasExited)
                target.Kill(entireProcessTree: true);
            await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await ImportCompletionWarmer.DrainForTestsAsync();
        }
    }

    private async Task AssertEditorUpdateAsync(
        LspServer editor, HotReloadParams request, Process target, string log, string expectedValue)
    {
        var result = await editor.HotReloadApply(request, CancellationToken.None);
        Assert.True(result.Ok,
            $"{result.Summary}\n" +
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Severity} {d.Id}: {d.Message}")) +
            string.Join("\n", result.Errors));
        Assert.Contains(result.AppliedTo, applied => applied.Contains(target.Id.ToString()));
        Assert.Empty(result.Errors);
        Assert.True(await WaitForLastLineAsync(log, expectedValue),
            "The editor's delta did not reach the running process." + Diagnose(target));
        Assert.False(target.HasExited, Diagnose(target));
    }

    private async Task VerifyRuntimeUpdatesAsync(bool useDotnetHost, bool attachDebugger)
    {
        var (csproj, sourcePath, exe, log) = await BuildTargetAsync();

        using var target = Launch(exe, log, useDotnetHost);
        using var debugger = attachDebugger ? new DebuggerService() : null;
        using var continueCancellation = new CancellationTokenSource();
        Task<string>? continuing = null;
        try
        {
            Assert.True(await WaitAsync(() => HotReloadAgentServer.Instance.Targets
                    .Any(t => t.ProcessId == target.Id)),
                "The hot reload agent never connected; nothing could have been applied." + Diagnose(target));

            Assert.True(await WaitForLastLineAsync(log, "6"),
                "The target never produced its baseline value." + Diagnose(target));

            if (debugger is not null)
            {
                Assert.DoesNotContain("Error:", await debugger.AttachToProcessAsync(target.Id));
                continuing = debugger.ContinueAsync(continueCancellation.Token);
            }

            // --- the edit ---

            var (session, message) = await HotReloadService.StartAsync(csproj);
            Assert.True(session is not null, message);

            await AssertNoChangesAsync(session!, target, log, "6");

            await File.WriteAllTextAsync(sourcePath, BaselineSource.Replace("input * 2", "input * 10"));

            var outcome = await session!.ApplyAsync();

            if (attachDebugger)
            {
                Assert.False(outcome.Ok);
                Assert.Empty(outcome.AppliedTo);
                Assert.Contains(outcome.Errors, error => error.Contains("while a debugger is attached", StringComparison.OrdinalIgnoreCase));
                Assert.True(await WaitForLastLineAsync(log, "6"));
                Assert.False(target.HasExited);

                // A rejected update must remain pending. Committing it would make this retry
                // incorrectly report "No changes" even though the target still runs the old code.
                var retry = await session.ApplyAsync();
                Assert.False(retry.Ok);
                Assert.Empty(retry.AppliedTo);
                Assert.Contains(retry.Errors, error => error.Contains("while a debugger is attached", StringComparison.OrdinalIgnoreCase));
                return;
            }

            Assert.True(outcome.Ok,
                $"{outcome.Summary}\n" +
                string.Join("\n", outcome.Diagnostics.Select(d => $"{d.Severity} {d.Id}: {d.Message}")) +
                string.Join("\n", outcome.Errors));

            Assert.Contains(outcome.AppliedTo, a => a.Contains(target.Id.ToString()));
            Assert.DoesNotContain("No changes", outcome.Summary);

            // The claim under test: the process that is already running now returns something else.
            Assert.True(await WaitForLastLineAsync(log, "30"),
                "The delta was reported as applied but the process kept returning the old value." + Diagnose(target));

            Assert.False(target.HasExited, "The process restarted; that would not be hot reload." + Diagnose(target));

            // Reusing the unchanged workspace after a committed edit must not emit a revert.
            await AssertNoChangesAsync(session, target, log, "30");

            // A compiler error must preserve both the live code and the committed generation,
            // so fixing it below can continue the same session without rebuilding or restarting.
            await File.WriteAllTextAsync(sourcePath, BaselineSource.Replace("input * 2", "missingValue"));
            var rejected = await session.ApplyAsync();
            Assert.False(rejected.Ok);
            Assert.Empty(rejected.AppliedTo);
            Assert.Contains(rejected.Diagnostics, diagnostic =>
                diagnostic.Severity == "error" && diagnostic.Id == "CS0103" &&
                string.Equals(diagnostic.FilePath, sourcePath, StringComparison.OrdinalIgnoreCase) &&
                diagnostic.Line > 0);
            long rejectedLogLength = new FileInfo(log).Length;
            Assert.True(await WaitForLastLineAsync(log, "30", rejectedLogLength),
                "The target stopped producing the last successfully applied value after a compiler error." + Diagnose(target));
            Assert.False(target.HasExited, Diagnose(target));

            // --- a second edit, against the same session ---

            // The second apply is the regression that bit here: the workspace snapshot never
            // learns about the first edit, so a diff that forgets it would emit a delta
            // REVERTING it. The value must move forward to 60, not back to 6.
            await File.WriteAllTextAsync(sourcePath, BaselineSource.Replace("input * 2", "input * 20"));

            var second = await session!.ApplyAsync();
            Assert.True(second.Ok,
                $"{second.Summary}\n" +
                string.Join("\n", second.Diagnostics.Select(d => $"{d.Severity} {d.Id}: {d.Message}")) +
                string.Join("\n", second.Errors));
            Assert.Contains(second.AppliedTo, a => a.Contains(target.Id.ToString()));

            Assert.True(await WaitForLastLineAsync(log, "60"),
                "The second delta was reported as applied but the process kept the first edit's value." + Diagnose(target));
            Assert.False(target.HasExited, "The process restarted on the second apply." + Diagnose(target));

            await AssertNoChangesAsync(session, target, log, "60");
        }
        finally
        {
            continueCancellation.Cancel();
            debugger?.Stop();
            HotReloadService.Get(csproj)?.Stop();
            try
            {
                if (!target.HasExited)
                    target.Kill(entireProcessTree: true);
                await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (InvalidOperationException) { }
            if (continuing is not null)
            {
                try { await continuing.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (OperationCanceledException) when (continueCancellation.IsCancellationRequested) { }
            }
        }
    }

    private async Task AssertNoChangesAsync(HotReloadService session, Process target, string log, string value)
    {
        var unchanged = await session.ApplyAsync();
        Assert.True(unchanged.Ok, unchanged.Summary + "\n" + string.Join("\n", unchanged.Errors));
        Assert.Equal("No changes to apply.", unchanged.Summary);
        Assert.Empty(unchanged.AppliedTo);
        Assert.Empty(unchanged.Errors);
        Assert.DoesNotContain(unchanged.Diagnostics, diagnostic => diagnostic.Severity == "error");

        long previousLength = new FileInfo(log).Length;
        Assert.True(await WaitForLastLineAsync(log, value, previousLength),
            "A no-op apply changed the runtime value or stopped the target." + Diagnose(target));
        Assert.False(target.HasExited, Diagnose(target));
    }

    private Process Launch(string exe, string log, bool useDotnetHost, HotReloadEnvironmentDto? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = useDotnetHost ? "dotnet" : exe,
            WorkingDirectory = Path.GetDirectoryName(exe),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (useDotnetHost)
            startInfo.ArgumentList.Add(Path.ChangeExtension(exe, ".dll"));
        startInfo.ArgumentList.Add(log);

        // The product's own preparation, not a test-local imitation: this is the thing that has to
        // work for hot reload to be reachable at all.
        if (environment is null)
        {
            Assert.True(HotReloadLauncher.Inject(startInfo), "The hot reload agent was not found.");
        }
        else
        {
            Assert.True(environment.Available, environment.Message);
            foreach (var (name, value) in environment.Variables)
                startInfo.Environment[name] = value;
        }

        var process = Process.Start(startInfo)!;

        // Both streams are redirected, so both must be read. A redirected pipe nobody drains fills
        // at about four kilobytes and then blocks the writer inside its next Console call — the
        // target stops looping, the log stops growing, and every wait below times out on a process
        // that is alive and stuck rather than slow. The agent writes to stderr on any apply it
        // cannot complete, which is why this only ever bit under load.
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private void Capture(string? line)
    {
        if (line is null)
            return;

        lock (_targetOutput)
            _targetOutput.AppendLine(line);
    }

    /// <summary>What the target had to say, so a wait that expires says why rather than just that
    /// it did.</summary>
    private string Diagnose(Process target)
    {
        string captured;
        lock (_targetOutput)
            captured = _targetOutput.ToString();

        string state = target.HasExited
            ? $"The target exited with code {target.ExitCode}."
            : "The target was still running.";

        return captured.Length == 0
            ? $"\n{state} It wrote nothing."
            : $"\n{state} It wrote:\n{captured}";
    }

    private async Task<(string Project, string Source, string Executable, string Log)> BuildTargetAsync(
        bool useDifferentPathCasing = false)
    {
        Directory.CreateDirectory(_root);
        string csproj = Path.Combine(_root, "HotReloadTarget.csproj");
        string sourcePath = Path.Combine(_root, "Program.cs");
        string log = Path.Combine(_root, "values.txt");
        if (useDifferentPathCasing && OperatingSystem.IsWindows())
        {
            csproj = char.ToUpperInvariant(csproj[0]) + csproj[1..];
            sourcePath = char.ToUpperInvariant(sourcePath[0]) + sourcePath[1..];
        }

        await File.WriteAllTextAsync(csproj, Project);
        await File.WriteAllTextAsync(sourcePath, BaselineSource);
        string buildPath = useDifferentPathCasing && OperatingSystem.IsWindows()
            ? char.ToLowerInvariant(csproj[0]) + csproj[1..]
            : csproj;
        Assert.True(await BuildAsync(buildPath), "The target project did not build.");

        string exe = Path.Combine(_root, "bin", "Debug", $"net{Environment.Version.Major}.0",
            OperatingSystem.IsWindows() ? "HotReloadTarget.exe" : "HotReloadTarget");
        Assert.True(File.Exists(exe), $"'{exe}' was not produced by the build.");
        if (useDifferentPathCasing && OperatingSystem.IsWindows())
        {
            using var pdb = File.OpenRead(Path.ChangeExtension(exe, ".pdb"));
            using var provider = System.Reflection.Metadata.MetadataReaderProvider.FromPortablePdbStream(pdb);
            var reader = provider.GetMetadataReader();
            Assert.Contains(reader.Documents, handle => reader.GetString(reader.GetDocument(handle).Name) ==
                char.ToLowerInvariant(sourcePath[0]) + sourcePath[1..]);
        }
        return (csproj, sourcePath, exe, log);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static async Task<bool> BuildAsync(string csproj)
    {
        using var build = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(csproj),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "build", csproj, "-c", "Debug", "--nologo" },
        })!;

        Task<string> output = build.StandardOutput.ReadToEndAsync();
        Task<string> errors = build.StandardError.ReadToEndAsync();
        try
        {
            await build.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            if (!build.HasExited)
                build.Kill(entireProcessTree: true);
            await build.WaitForExitAsync();
            Assert.Fail($"The target build timed out.\n{await output}\n{await errors}");
        }

        string[] captured = await Task.WhenAll(output, errors);
        if (build.ExitCode != 0)
            Assert.Fail(string.Join("\n", captured));

        return build.ExitCode == 0;
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int attempts = 200)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (condition())
                return true;
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>Waits for the target's most recent write to be a given value. The last line, not
    /// any line: the file still holds every earlier value, so "contains" would pass before the
    /// edit landed.</summary>
    private static Task<bool> WaitForLastLineAsync(string log, string value, long previousLength = -1) => WaitAsync(() =>
    {
        try
        {
            if (!File.Exists(log))
                return false;

            // Shared for writing and deletion, because the target is appending to this file the
            // whole time. File.ReadLines opens with FileShare.Read, which locks the writer out —
            // its File.AppendAllText then throws an unhandled IOException and the process dies,
            // which this test reports as "the process restarted on the second apply". The catch
            // below only ever covered the reader's side of that collision, and the reader is not
            // the side that was failing.
            using var stream = new FileStream(
                log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= previousLength)
                return false;
            using var reader = new StreamReader(stream);

            string? last = null;
            while (reader.ReadLine() is { } line)
            {
                if (line.Trim().Length > 0)
                    last = line;
            }

            return last?.Trim() == value;
        }
        catch (IOException)
        {
            return false; // the target appends while this reads
        }
    });
}
