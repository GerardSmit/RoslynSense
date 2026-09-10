using System.Diagnostics;
using System.Reflection.Metadata;
using System.Text;
using RoslynMCP.Debugger;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Services;
using RoslynMCP.Services.Debugging;
using RoslynMCP.Services.HotReload;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Edit-and-Continue against a live .NET Framework process, through the product's own path.
/// </summary>
/// <remarks>
/// <para>
/// The desktop runtime has no in-process metadata updater, so the only route onto it is
/// <c>ICorDebugModule2::ApplyChanges</c> — the app has to be under the debugger for an edit to
/// land. That call does not validate what it is given: an earlier version of this test built its
/// own delta from a stubbed baseline and the CLR faulted on it, taking the whole test host down
/// with an access violation rather than returning an error.
/// </para>
/// <para>
/// Two things came out of that and both shape this test. The delta is now computed by
/// <see cref="HotReloadService"/>, so Roslyn's EnC engine builds the baseline from the project's
/// real PDB instead of a stub. The targets cover both <c>x86</c> and <c>x64</c> and require
/// <see cref="DebugEngineFactory"/> to pick a bitness-matched worker, which is the only engine
/// allowed to call <c>ApplyChanges</c> — a fault there costs a disposable process rather than the
/// language server.
/// </para>
/// </remarks>
[Collection(DebuggerCollection.Name)]
public class FrameworkHotReloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fx-hotreload-{Guid.NewGuid():N}");

    /// <summary>
    /// SDK-style but <c>net48</c>: the desktop runtime with a project format the workspace can
    /// load without Visual Studio's MSBuild. x86 so the debugger runs out of process.
    /// </summary>
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net48</TargetFramework>
            <PlatformTarget>x86</PlatformTarget>
            <AssemblyName>FxHotReloadTarget</AssemblyName>
            <RootNamespace>FxHotReloadTarget</RootNamespace>
            <Optimize>false</Optimize>
            <DebugType>portable</DebugType>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>
    /// The same target with a Windows (full) PDB — what a real legacy .NET Framework project
    /// emits. The EnC baseline has to be readable from this format or hot reload only works on
    /// the SDK-style fixtures.
    /// </summary>
    private const string FullPdbProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net48</TargetFramework>
            <PlatformTarget>x86</PlatformTarget>
            <AssemblyName>FxHotReloadTarget</AssemblyName>
            <RootNamespace>FxHotReloadTarget</RootNamespace>
            <Optimize>false</Optimize>
            <DebugType>full</DebugType>
          </PropertyGroup>
        </Project>
        """;

    // A classic project must pass through Visual Studio MSBuild and the legacy workspace
    // loader. Merely targeting net48 from an SDK project does not exercise either path.
    private const string LegacyProject = """
        <Project ToolsVersion="15.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props" Condition="Exists('$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props')" />
          <PropertyGroup>
            <Configuration Condition="'$(Configuration)' == ''">Debug</Configuration>
            <Platform Condition="'$(Platform)' == ''">AnyCPU</Platform>
            <ProjectGuid>{D2934D15-6DBA-4FDB-9728-89CD6837DE23}</ProjectGuid>
            <OutputType>Exe</OutputType>
            <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
            <PlatformTarget>x86</PlatformTarget>
            <AssemblyName>FxHotReloadTarget</AssemblyName>
            <RootNamespace>FxHotReloadTarget</RootNamespace>
            <OutputPath>bin\Debug\</OutputPath>
            <DebugSymbols>true</DebugSymbols>
            <DebugType>full</DebugType>
            <Optimize>false</Optimize>
            <Deterministic>true</Deterministic>
          </PropertyGroup>
          <ItemGroup>
            <Reference Include="System" />
            <Compile Include="Program.cs" />
          </ItemGroup>
          <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
        </Project>
        """;

    private const string BaselineSource = """
        using System;
        using System.IO;
        using System.Threading;

        namespace FxHotReloadTarget
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

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [FrameworkHotReloadFact]
    public Task ARoslynDeltaIsAcceptedByTheDesktopClrAndChangesALiveProcess() => RunAsync();

    /// <summary>
    /// Break All, then apply — the case that settles whether the desktop CLR needs a stop that
    /// arrived through a debug event, or merely a stop with a real context behind it.
    /// </summary>
    /// <remarks>
    /// Earlier attempts applied after <c>ICorDebugProcess::Stop</c> alone and after adopting a
    /// thread ad hoc, and both faulted. This one goes through the engine's own Break All, which
    /// suspends, adopts a user-code thread as the stop context, and emits a stop the session
    /// state machine sees — the same shape a breakpoint produces.
    /// </remarks>
    [FrameworkHotReloadFact]
    public async Task AnEditAppliesAfterBreakingIntoARunningTarget()
    {
        Directory.CreateDirectory(_root);
        string csproj = Path.Combine(_root, "FxHotReloadTarget.csproj");
        string sourcePath = Path.Combine(_root, "Program.cs");
        string log = Path.Combine(_root, "values.txt");

        await File.WriteAllTextAsync(csproj, Project);
        await File.WriteAllTextAsync(sourcePath, BaselineSource);
        Assert.True(await BuildAsync(csproj), "The .NET Framework target did not build.");

        string exe = Path.Combine(_root, "bin", "Debug", "net48", "FxHotReloadTarget.exe");
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(Services.DebugRuntime.NetFramework);

        try
        {
            Assert.DoesNotContain("Error:", await backend.LaunchAsync(
                exe, [log], null, Path.GetDirectoryName(exe)));

            await ResumeUntilValueAsync(backend, log, "6");

            string paused = await backend.InterruptAsync();
            Assert.DoesNotContain("cannot suspend", paused);
            Assert.NotNull(backend.CurrentFrame);

            var (session, message) = await HotReloadService.StartAsync(csproj);
            Assert.True(session is not null, message);
            await File.WriteAllTextAsync(sourcePath, BaselineSource.Replace("input * 2", "input * 10"));

            var outcome = await session!.ApplyAsync();

            Assert.True(outcome.Ok,
                $"{outcome.Summary}\n" + string.Join("\n", outcome.Errors) +
                "\n--- engine ---\n" + backend.GetStatus());

            Assert.NotNull(backend.CurrentFrame);
            AssertApplied(outcome, backend);
            await ResumeUntilValueAsync(backend, log, "30");
        }
        finally
        {
            await StopTargetAsync(csproj, backend);
        }
    }

    /// <summary>
    /// A running target is broken into, edited, and resumed, without the caller pausing first.
    /// </summary>
    /// <remarks>
    /// This is the case the whole investigation was about. It faulted for as long as the apply
    /// followed a bare <c>ICorDebugProcess::Stop</c>; it works now that the engine performs a real
    /// Break All — suspend, adopt a user-code thread, report the stop — before applying. The
    /// engine still refuses an apply to a running target, as a backstop for anything reaching
    /// past the backend.
    /// </remarks>
    [FrameworkHotReloadTheory]
    [InlineData("x86", "portable")]
    [InlineData("x64", "portable")]
    [InlineData("x86", "full")]
    [InlineData("x64", "full")]
    public Task AnEditAppliesToARunningTargetWithoutTheCallerPausingIt(
        string architecture, string debugType) => RunUpdatesAsync(architecture, debugType);

    [FrameworkHotReloadFact]
    public Task RepeatedEditsApplyToALegacyProjectBuiltWithVisualStudioMsBuild() =>
        RunUpdatesAsync("x86", "full", legacy: true);

    [FrameworkHotReloadTheory]
    [InlineData("portable")]
    [InlineData("full")]
    public Task RepeatedEditsApplyWhenBuildAndWorkspaceUseDifferentDriveLetterCasing(string debugType) =>
        RunUpdatesAsync("x86", debugType, lowercaseBuildPath: true);

    private async Task RunUpdatesAsync(
        string architecture, string debugType, bool legacy = false, bool lowercaseBuildPath = false)
    {
        string projectRoot = lowercaseBuildPath ? WithDriveLetterCase(_root, upper: true) : _root;
        Directory.CreateDirectory(projectRoot);
        string csproj = Path.Combine(projectRoot, "FxHotReloadTarget.csproj");
        string sourcePath = Path.Combine(projectRoot, "Program.cs");
        string log = Path.Combine(projectRoot, "values.txt");

        await File.WriteAllTextAsync(csproj, legacy ? LegacyProject : ProjectFor(architecture, debugType));
        await File.WriteAllTextAsync(sourcePath, BaselineSource);
        string buildPath = lowercaseBuildPath ? WithDriveLetterCase(csproj, upper: false) : csproj;
        if (lowercaseBuildPath)
        {
            Assert.NotEqual(csproj, buildPath);
            Assert.Equal(csproj, buildPath, ignoreCase: true);
        }
        // Pass the differently spelled path to the actual build process; the EnC workspace is
        // opened below using csproj's uppercase drive, as it is when VS Code builds a URI path.
        Assert.True(await BuildAsync(buildPath, legacy), "The .NET Framework target did not build.");

        string output = Path.Combine(projectRoot, "bin", "Debug");
        string exe = Path.Combine(legacy ? output : Path.Combine(output, "net48"), "FxHotReloadTarget.exe");
        AssertBuildFormat(exe, architecture, debugType);
        if (lowercaseBuildPath && debugType == "portable")
        {
            using var pdb = File.OpenRead(Path.ChangeExtension(exe, ".pdb"));
            using var provider = MetadataReaderProvider.FromPortablePdbStream(pdb);
            var reader = provider.GetMetadataReader();
            var documents = reader.Documents.Select(handle => reader.GetString(reader.GetDocument(handle).Name));
            Assert.Contains(WithDriveLetterCase(sourcePath, upper: false), documents);
        }
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(Services.DebugRuntime.NetFramework);

        Process? target = null;
        using var continueCancellation = new CancellationTokenSource();
        Task<string>? continuing = null;
        try
        {
            Assert.DoesNotContain("Error:", await backend.LaunchAsync(
                exe, [log], null, Path.GetDirectoryName(exe)));

            // The user's Continue request can still be waiting when Apply initiates Break All.
            // Both operations must observe the stop instead of competing for one semaphore slot.
            continuing = backend.ContinueAsync(continueCancellation.Token);
            Assert.True(await WaitForLastLineAsync(log, "6"), "The target never started.");
            Assert.NotNull(backend.DebuggeePid);
            target = Process.GetProcessById(backend.DebuggeePid.Value);
            Assert.Equal(architecture == "x86" ? DebugArch.X86 : DebugArch.X64,
                ProcessArch.OfProcess(target.Id));

            var (session, message) = await HotReloadService.StartAsync(csproj);
            Assert.True(session is not null, message);

            // Multiple generations must update the same process, then a compile error must leave
            // the last accepted generation running and allow a corrected edit to apply.
            foreach (int multiplier in new[] { 10, 20 })
            {
                await File.WriteAllTextAsync(sourcePath,
                    BaselineSource.Replace("input * 2", $"input * {multiplier}"));
                AssertApplied(await ApplyPromptlyAsync(session!), backend);
                Assert.True(await WaitForLastLineAsync(log, (3 * multiplier).ToString()),
                    "The edit did not resume the target with the new code.\n" + backend.GetStatus());
                AssertSameProcess(backend, target);
            }

            await File.WriteAllTextAsync(sourcePath,
                BaselineSource.Replace("input * 2", "input * missingMultiplier"));
            var rejected = await ApplyPromptlyAsync(session!);
            Assert.False(rejected.Ok);
            Assert.Empty(rejected.AppliedTo);
            Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Severity == "error");
            Assert.True(await WaitForLastLineAsync(log, "60"));
            AssertSameProcess(backend, target);

            await File.WriteAllTextAsync(sourcePath,
                BaselineSource.Replace("input * 2", "input * 30"));
            AssertApplied(await ApplyPromptlyAsync(session), backend);
            Assert.True(await WaitForLastLineAsync(log, "90"),
                "Correcting the compile error did not update the running target.\n" + backend.GetStatus());
            AssertSameProcess(backend, target);

            var unchanged = await ApplyPromptlyAsync(session);
            Assert.True(unchanged.Ok, unchanged.Summary);
            Assert.Empty(unchanged.AppliedTo);
            Assert.Empty(unchanged.Errors);
            Assert.Equal("No changes to apply.", unchanged.Summary);
            AssertSameProcess(backend, target);
        }
        finally
        {
            continueCancellation.Cancel();
            try
            {
                if (continuing is not null)
                {
                    try { await continuing.WaitAsync(TimeSpan.FromSeconds(15)); }
                    catch (OperationCanceledException) when (continueCancellation.IsCancellationRequested) { }
                }
            }
            finally { await StopTargetAsync(csproj, backend, target); }
        }
    }

    /// <summary>
    /// The delta pipeline against a Windows (full) PDB, which is what every legacy .NET
    /// Framework project — WebForms under IIS Express included — actually produces.
    /// </summary>
    /// <remarks>
    /// Roslyn's EnC baseline is read from the built module's PDB. Portable PDBs are read
    /// managed; a full PDB needs a native DiaSymReader. If that reader is not available to the
    /// server, the session opens fine and the failure only surfaces at the first apply — as a
    /// diagnostic or an exception, never as working hot reload.
    /// </remarks>
    [FrameworkHotReloadFact]
    public async Task AnEditAppliesToAProjectBuiltWithAFullWindowsPdb()
    {
        Directory.CreateDirectory(_root);
        string csproj = Path.Combine(_root, "FxHotReloadTarget.csproj");
        string sourcePath = Path.Combine(_root, "Program.cs");
        string log = Path.Combine(_root, "values.txt");

        await File.WriteAllTextAsync(csproj, FullPdbProject);
        await File.WriteAllTextAsync(sourcePath, BaselineSource);
        Assert.True(await BuildAsync(csproj), "The .NET Framework target did not build.");

        string exe = Path.Combine(_root, "bin", "Debug", "net48", "FxHotReloadTarget.exe");
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(Services.DebugRuntime.NetFramework);

        try
        {
            Assert.DoesNotContain("Error:", await backend.LaunchAsync(
                exe, [log], null, Path.GetDirectoryName(exe)));

            await ResumeUntilValueAsync(backend, log, "6");

            var (session, message) = await HotReloadService.StartAsync(csproj);
            Assert.True(session is not null, message);
            await File.WriteAllTextAsync(sourcePath, BaselineSource.Replace("input * 2", "input * 10"));

            var outcome = await session!.ApplyAsync();

            Assert.True(outcome.Ok,
                $"{outcome.Summary}\n" +
                string.Join("\n", outcome.Diagnostics.Select(d => $"{d.Severity} {d.Id}: {d.Message}")) +
                string.Join("\n", outcome.Errors) +
                "\n--- engine ---\n" + backend.GetStatus());

            Assert.True(await WaitForLastLineAsync(log, "30"),
                "The edit was applied but the process did not carry on with the new code.");
            AssertApplied(outcome, backend);
        }
        finally
        {
            await StopTargetAsync(csproj, backend);
        }
    }

    /// <summary>
    /// The user's loop: stopped at a breakpoint in the method being edited, apply, continue,
    /// hit the rebound breakpoint in the new version, and step through it.
    /// </summary>
    /// <remarks>
    /// The other tests end at "the process runs the new code". This one covers what the debugger
    /// itself has to survive after an apply: the breakpoint was invalidated and rebound to the
    /// new method version, and a step in that version needs sequence points that match the IL
    /// actually executing. A stepper built against the wrong version never completes, which the
    /// backend reports as "still running" — the editor experience is a debugger that is stuck.
    /// </remarks>
    [FrameworkHotReloadTheory]
    [InlineData("x86", "portable")]
    [InlineData("x64", "portable")]
    [InlineData("x86", "full")]
    [InlineData("x64", "full")]
    public async Task ABreakpointHitAfterAnAppliedEditCanBeSteppedThrough(
        string architecture, string debugType)
    {
        Directory.CreateDirectory(_root);
        string csproj = Path.Combine(_root, "FxHotReloadTarget.csproj");
        string sourcePath = Path.Combine(_root, "Program.cs");
        string log = Path.Combine(_root, "values.txt");

        await File.WriteAllTextAsync(csproj, ProjectFor(architecture, debugType));
        await File.WriteAllTextAsync(sourcePath, BaselineSource);
        Assert.True(await BuildAsync(csproj), "The .NET Framework target did not build.");

        string exe = Path.Combine(_root, "bin", "Debug", "net48", "FxHotReloadTarget.exe");
        AssertBuildFormat(exe, architecture, debugType);
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(Services.DebugRuntime.NetFramework);

        try
        {
            Assert.DoesNotContain("Error:", await backend.LaunchAsync(
                exe, [log], null, Path.GetDirectoryName(exe),
                [(sourcePath, LineOf("return input * 2"))]));

            // First hit: the old version of Compute.
            string stopped = await backend.ContinueAsync();
            Assert.DoesNotContain("still running", stopped);
            Assert.NotNull(backend.CurrentFrame);
            Assert.Equal("breakpoint", backend.CurrentFrame.Reason);
            Assert.Equal(LineOf("return input * 2"), backend.CurrentFrame.Line);

            var (session, message) = await HotReloadService.StartAsync(csproj);
            Assert.True(session is not null, message);
            await File.WriteAllTextAsync(sourcePath, BaselineSource.Replace("input * 2", "input * 10"));

            var outcome = await session!.ApplyAsync();
            Assert.True(outcome.Ok,
                $"{outcome.Summary}\n" + string.Join("\n", outcome.Errors) +
                "\n--- engine ---\n" + backend.GetStatus());
            AssertApplied(outcome, backend);
            Assert.NotNull(backend.CurrentFrame);

            // Second hit: the breakpoint was rebound to the new version of the method.
            stopped = await backend.ContinueAsync();
            Assert.False(stopped.Contains("still running"),
                "After the apply, the rebound breakpoint was never hit again:\n" + stopped +
                "\n--- engine ---\n" + backend.GetStatus());
            Assert.NotNull(backend.CurrentFrame);
            Assert.Equal("breakpoint", backend.CurrentFrame.Reason);
            Assert.Equal(LineOf("return input * 2"), backend.CurrentFrame.Line);

            // The user's "go through it": step over inside the edited method, then step again to
            // leave it. A stepper that never completes reports "still running".
            foreach (int step in new[] { 1, 2 })
            {
                string stepResult = await backend.StepOverAsync();
                Assert.False(
                    stepResult.Contains("still running") || stepResult.StartsWith("Error"),
                    $"Step {step} after the apply did not complete:\n" + stepResult +
                    "\n--- engine ---\n" + backend.GetStatus());
                Assert.NotNull(backend.CurrentFrame);
            }

            // And the process still runs the new code once released. The breakpoint stops every
            // iteration, so ride a few hits rather than waiting for a free run.
            bool sawNewValue = false;
            for (int hit = 0; hit < 10 && !sawNewValue; hit++)
            {
                await backend.ContinueAsync();
                sawNewValue = await WaitForLastLineAsync(log, "30", attempts: 10);
            }

            Assert.True(sawNewValue,
                "Stepping succeeded but the resumed process kept returning the old value.");
        }
        finally
        {
            await StopTargetAsync(csproj, backend);
        }
    }

    private async Task RunAsync()
    {
        Directory.CreateDirectory(_root);
        string csproj = Path.Combine(_root, "FxHotReloadTarget.csproj");
        string sourcePath = Path.Combine(_root, "Program.cs");
        string log = Path.Combine(_root, "values.txt");

        await File.WriteAllTextAsync(csproj, Project);
        await File.WriteAllTextAsync(sourcePath, BaselineSource);
        Assert.True(await BuildAsync(csproj), "The .NET Framework target did not build.");

        string exe = Path.Combine(_root, "bin", "Debug", "net48", "FxHotReloadTarget.exe");
        Assert.True(File.Exists(exe), $"'{exe}' was not produced by the build.");

        // The guard under test: an x86 target on this x64 host has to resolve to a worker.
        Assert.NotNull(DebugEngineFactory.FindWorker(DebugArch.X86));

        // Through the session manager, not a bare engine: an apply finds its target the way the
        // product does — the registered session, or one published by another process.
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(Services.DebugRuntime.NetFramework);
        try
        {
            string launched = await backend.LaunchAsync(
                exe, [log], null, Path.GetDirectoryName(exe),
                [(sourcePath, LineOf("File.AppendAllText"))]);
            Assert.DoesNotContain("Error:", launched);

            // The apply happens from a real break state, which is the only kind the desktop CLR
            string stopped = await backend.ContinueAsync();
            Assert.DoesNotContain("still running", stopped);
            Assert.NotNull(backend.CurrentFrame);

            var (session, _) = await HotReloadService.StartAsync(csproj);
            Assert.NotNull(session);

            await File.WriteAllTextAsync(sourcePath, BaselineSource.Replace("input * 2", "input * 10"));

            var outcome = await session!.ApplyAsync();

            Assert.True(outcome.Ok,
                $"{outcome.Summary}\n" +
                string.Join("\n", outcome.Diagnostics.Select(d => $"{d.Severity} {d.Id}: {d.Message}")) +
                string.Join("\n", outcome.Errors) +
                // The engine's captured output carries the worker's stderr, which is where an
                // ApplyChanges fault says what it actually was.
                "\n--- engine ---\n" + backend.GetStatus());

            // Resumed after the apply: the new IL only runs when the process does.
            AssertApplied(outcome, backend);
            Assert.NotNull(backend.CurrentFrame);
            await ResumeUntilValueAsync(backend, log, "30");
        }
        finally
        {
            await StopTargetAsync(csproj, backend);
        }
    }

    /// <summary>Locates a line in the target by its text, so the breakpoint cannot drift when the
    /// program is edited.</summary>
    private static int LineOf(string text) => Array.FindIndex(
        BaselineSource.ReplaceLineEndings("\n").Split('\n'),
        line => line.Contains(text, StringComparison.Ordinal)) + 1;

    private static string ProjectFor(string architecture, string debugType) => Project
        .Replace("<PlatformTarget>x86</PlatformTarget>", $"<PlatformTarget>{architecture}</PlatformTarget>")
        .Replace("<DebugType>portable</DebugType>", $"<DebugType>{debugType}</DebugType>");

    private static string WithDriveLetterCase(string path, bool upper)
    {
        Assert.True(path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':',
            "The drive-casing regression requires a local Windows drive for the temporary target.");
        return (upper ? char.ToUpperInvariant(path[0]) : char.ToLowerInvariant(path[0])) + path[1..];
    }

    private static void AssertBuildFormat(string exe, string architecture, string debugType)
    {
        var arch = architecture == "x86" ? DebugArch.X86 : DebugArch.X64;
        Assert.Equal(arch, ProcessArch.OfExecutable(exe));
        Assert.True(DebugEngineFactory.FindWorker(arch) is not null,
            $"The {architecture} debug worker is missing. Build tests with -p:BuildDebugWorkers=true.");
        string signature = debugType == "portable" ? "BSJB" : "Microsoft C/C++ MSF 7.00";
        Assert.True(File.ReadAllBytes(Path.ChangeExtension(exe, ".pdb")).AsSpan()
            .StartsWith(Encoding.ASCII.GetBytes(signature)), $"The target did not produce a {debugType} PDB.");
    }

    private static void AssertApplied(HotReloadOutcome outcome, PublishingDebugBackend backend)
    {
        Assert.True(outcome.Ok, $"{outcome.Summary}\n" +
            string.Join("\n", outcome.Diagnostics.Select(d => $"{d.Severity} {d.Id}: {d.Message}")) +
            string.Join("\n", outcome.Errors) + "\n--- engine ---\n" + backend.GetStatus());
        Assert.Empty(outcome.Errors);
        Assert.NotEmpty(outcome.AppliedTo);
        Assert.DoesNotContain(outcome.AppliedTo, target => target.Contains("queued", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<HotReloadOutcome> ApplyPromptlyAsync(HotReloadService session)
    {
        var timeout = TimeSpan.FromSeconds(15);
        using var cancellation = new CancellationTokenSource(timeout);
        var elapsed = Stopwatch.StartNew();
        var outcome = await session.ApplyAsync(cancellation.Token);
        Assert.True(elapsed.Elapsed < timeout,
            $"Applying an edit took {elapsed.Elapsed}; a pending Continue must not consume the pause signal.");
        return outcome;
    }

    private static void AssertSameProcess(PublishingDebugBackend backend, Process target)
    {
        Assert.False(target.HasExited, "Hot reload must preserve the original process.");
        Assert.Equal(target.Id, backend.DebuggeePid);
    }

    private static async Task ResumeUntilValueAsync(PublishingDebugBackend backend, string log, string value)
    {
        // Continue waits for a future stop. Once the output proves the app is running, cancel
        // that wait so a later pause has its own waiter for the backend's single stop signal.
        using var cancellation = new CancellationTokenSource();
        var continuing = backend.ContinueAsync(cancellation.Token);
        try
        {
            Assert.True(await WaitForLastLineAsync(log, value),
                $"The target did not produce {value} after resuming.\n" + backend.GetStatus());
        }
        finally
        {
            cancellation.Cancel();
            try { await continuing.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private static async Task StopTargetAsync(
        string csproj, PublishingDebugBackend backend, Process? target = null)
    {
        if (target is null && backend.DebuggeePid is { } pid)
        {
            try { target = Process.GetProcessById(pid); }
            catch (ArgumentException) { }
        }

        try
        {
            if (HotReloadService.Get(csproj) is { } session)
                await session.StopAsync();
        }
        finally
        {
            try { backend.Stop(); }
            finally
            {
                DebugSessionManager.DisposeSession();
                if (target is not null)
                {
                    using (target)
                    {
                        if (!target.HasExited)
                            target.Kill(entireProcessTree: true);
                        await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
            }
        }
    }

    private static async Task<bool> BuildAsync(string csproj, bool legacy = false)
    {
        if (legacy)
        {
            Assert.True(MsBuildLocator.FindMsBuild() is { } msbuild && File.Exists(msbuild),
                "The legacy hot reload E2E test requires Visual Studio or Build Tools MSBuild " +
                "and the .NET Framework 4.8 developer pack. Install those prerequisites before enabling it.");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var result = await LaunchHandler.BuildAsync(csproj, "Debug", cancellation.Token);
            Assert.True(result.Success, result.Summary + "\n" +
                string.Join("\n", result.Errors.Select(error => error.Message)));
            return true;
        }

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

        var output = build.StandardOutput.ReadToEndAsync();
        var error = build.StandardError.ReadToEndAsync();
        try
        {
            await build.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch
        {
            if (!build.HasExited)
                build.Kill(entireProcessTree: true);
            await build.WaitForExitAsync();
            throw;
        }

        if (build.ExitCode != 0)
            Assert.Fail(await output + "\n" + await error);

        return true;
    }

    internal static string? FrameworkDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        return new[] { "Framework64", "Framework" }
            .Select(flavour => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", flavour, "v4.0.30319"))
            .FirstOrDefault(directory => File.Exists(Path.Combine(directory, "mscorlib.dll")));
    }

    /// <summary>Waits for the target's most recent write to be a given value. The last line, not
    /// any line: every earlier value is still in the file.</summary>
    private static async Task<bool> WaitForLastLineAsync(string log, string value, int attempts = 200)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                if (File.Exists(log))
                {
                    // File.ReadLines denies concurrent writers while it is open, which can make
                    // the debuggee throw in AppendAllText instead of merely delaying this poll.
                    using var stream = new FileStream(log, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    string text = await reader.ReadToEndAsync();
                    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (text.EndsWith('\n') && lines.Length > 0 && lines[^1].Trim() == value)
                        return true;
                }
            }
            catch (IOException)
            {
                // The target appends while this reads.
            }

            await Task.Delay(100);
        }

        return false;
    }
}
