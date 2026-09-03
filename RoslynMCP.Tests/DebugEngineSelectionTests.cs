using System.Diagnostics;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers automatic debug-engine selection. The caller never picks: netcoredbg cannot attach to
/// .NET Framework and ICorDebug is the only thing that can, so choosing wrong simply fails.
/// </summary>
[Collection(DebuggerCollection.Name)]
public class DebugEngineSelectionTests
{
    [Fact]
    public void WhenProjectIsLegacyThenNetFrameworkEngineIsSelected()
    {
        Assert.Equal(DebugRuntime.NetFramework, DebugRuntimeDetector.ForProject(FixturePaths.LegacyProjectFile));
        Assert.Equal(DebugRuntime.NetFramework, DebugRuntimeDetector.ForProject(FixturePaths.WebFormsSiteFile));
    }

    [Fact]
    public void WhenProjectIsSdkStyleThenCoreClrEngineIsSelected()
    {
        Assert.Equal(DebugRuntime.CoreClr, DebugRuntimeDetector.ForProject(FixturePaths.SampleProjectFile));
        Assert.Equal(DebugRuntime.CoreClr, DebugRuntimeDetector.ForProject(FixturePaths.DebugTestProjectFile));
    }

    [Fact]
    public void WhenSessionCreatedForProjectThenTheMatchingBackendIsUsed()
    {
        try
        {
            var legacy = DebugSessionManager.CreateSessionForProject(FixturePaths.LegacyProjectFile);
            Assert.IsType<IcorDebugBackend>(Unwrap(legacy));

            var modern = DebugSessionManager.CreateSessionForProject(FixturePaths.SampleProjectFile);
            Assert.IsType<DebuggerService>(Unwrap(modern));
        }
        finally
        {
            DebugSessionManager.DisposeSession();
        }
    }

    [Fact]
    public void WhenTheCoreClrEngineIsOptedIntoThenTheIcorDebugBackendIsUsed()
    {
        var restore = Config.DebugEngineOptions.CoreClr;
        try
        {
            Config.DebugEngineOptions.CoreClr = Config.CoreClrDebugEngine.IcorDebug;

            var modern = DebugSessionManager.CreateSessionForProject(FixturePaths.SampleProjectFile);
            Assert.IsType<IcorDebugBackend>(Unwrap(modern));
        }
        finally
        {
            Config.DebugEngineOptions.CoreClr = restore;
            DebugSessionManager.DisposeSession();
        }
    }

    [Fact]
    public void TheOptInDoesNotReachNetFramework()
    {
        // .NET Framework is on this engine either way. Asserted because the setting is scoped to
        // the one runtime where a choice exists, and a routing change that read it unconditionally
        // would still pass every other test here.
        var restore = Config.DebugEngineOptions.CoreClr;
        try
        {
            foreach (var choice in Enum.GetValues<Config.CoreClrDebugEngine>())
            {
                Config.DebugEngineOptions.CoreClr = choice;
                var legacy = DebugSessionManager.CreateSessionForProject(FixturePaths.LegacyProjectFile);
                Assert.IsType<IcorDebugBackend>(Unwrap(legacy));
            }
        }
        finally
        {
            Config.DebugEngineOptions.CoreClr = restore;
            DebugSessionManager.DisposeSession();
        }
    }

    [Fact]
    public void TheEngineIsReadWhenTheSessionStartsRatherThanHeldFromAnEarlierOne()
    {
        // A session already exists when the setting changes — the ordinary case, since the setting
        // is meant to be flipped while the tool is running. The next session has to see it.
        var restore = Config.DebugEngineOptions.CoreClr;
        try
        {
            Config.DebugEngineOptions.CoreClr = Config.CoreClrDebugEngine.NetCoreDbg;
            Assert.IsType<DebuggerService>(
                Unwrap(DebugSessionManager.CreateSessionForProject(FixturePaths.SampleProjectFile)));

            Config.DebugEngineOptions.CoreClr = Config.CoreClrDebugEngine.IcorDebug;
            Assert.IsType<IcorDebugBackend>(
                Unwrap(DebugSessionManager.CreateSessionForProject(FixturePaths.SampleProjectFile)));
        }
        finally
        {
            Config.DebugEngineOptions.CoreClr = restore;
            DebugSessionManager.DisposeSession();
        }
    }

    [Fact]
    public void WhenInspectingThisProcessThenCoreClrIsDetected()
    {
        // The test host itself runs on CoreCLR, so module-based detection must say so.
        Assert.Equal(
            DebugRuntime.CoreClr,
            DebugRuntimeDetector.ForProcess(Environment.ProcessId));
    }

    [Fact]
    public void WhenProcessIsGoneThenDetectionFallsBackInsteadOfThrowing()
    {
        // A PID that cannot be opened must degrade, never throw, or attach would crash the tool.
        var runtime = DebugRuntimeDetector.ForProcess(int.MaxValue - 1);
        Assert.Equal(DebugRuntime.CoreClr, runtime);
    }

    [RequiresNetFrameworkFact]
    public void WhenAttachedToNetFrameworkProcessThenNetFrameworkEngineIsSelected()
    {
        using var target = FxTargetProcess.Launch();

        var runtime = DebugRuntimeDetector.ForProcess(target.Process.Id);

        Assert.Equal(DebugRuntime.NetFramework, runtime);
        Assert.IsType<IcorDebugBackend>(Unwrap(DebugSessionManager.CreateSession(runtime)));
        DebugSessionManager.DisposeSession();
    }

    [RequiresNetFrameworkFact]
    public async Task WhenDebuggingNetFrameworkProcessThenBreakpointIsHitThroughTheToolPath()
    {
        using var target = FxTargetProcess.Launch();
        var session = DebugSessionManager.CreateSessionForProcess(target.Process.Id);

        try
        {
            Assert.IsType<IcorDebugBackend>(Unwrap(session));

            var attached = await session.AttachToProcessAsync(
                target.Process.Id, [(FxTargetProcess.SourcePath, FxTargetProcess.BreakpointLine)]);
            Assert.DoesNotContain("Error:", attached);

            // The target loops, so the breakpoint is reached without needing to resume first.
            var stopped = await WaitForStopAsync(session);
            Assert.True(stopped, "The breakpoint was never hit.");

            var stack = await session.GetStackTraceAsync();
            Assert.Contains("Compute", stack);

            var locals = await session.GetLocalsAsync();
            Assert.Contains("input", locals);
        }
        finally
        {
            DebugSessionManager.DisposeSession();
        }
    }

    /// <summary>Sessions come wrapped in the state-publishing decorator; engine assertions
    /// care about the engine inside.</summary>
    private static IDebugBackend Unwrap(IDebugBackend session) =>
        session is RoslynMCP.Services.Debugging.PublishingDebugBackend publishing
            ? publishing.Inner
            : session;

    private static async Task<bool> WaitForStopAsync(IDebugBackend session)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (session.CurrentFrame is not null)
                return true;
            await Task.Delay(100);
        }

        return false;
    }
}

/// <summary>
/// Compiles and runs a small .NET Framework console app to debug. Built at test time with the
/// framework's own csc so it carries a Windows PDB — the format that exercises the diasymreader
/// path, which is what .NET Framework debugging actually depends on.
/// </summary>
internal sealed class FxTargetProcess : IDisposable
{
    /// <summary>The statement to break on. Located in the source rather than hardcoded, so it
    /// cannot silently drift when the target program changes.</summary>
    private const string BreakpointStatement = "int result = input * 2;";

    /// <summary>The call whose Step Into reaches a <c>[DebuggerStepThrough]</c> method.</summary>
    private const string GuardedCallStatement = "int guarded = Guarded.Twice(result);";

    public static int BreakpointLine => LineOf(BreakpointStatement);

    public static int GuardedCallLine => LineOf(GuardedCallStatement);

    private static int LineOf(string statement) => Array.FindIndex(
        TargetSource.ReplaceLineEndings("\n").Split('\n'),
        line => line.Contains(statement, StringComparison.Ordinal)) + 1;

    private static readonly Lazy<string?> s_compiled = new(Compile);

    public required Process Process { get; init; }

    public static string SourcePath => Path.Combine(
        Path.GetDirectoryName(s_compiled.Value!)!, "Program.cs");

    public static bool IsAvailable => s_compiled.Value is not null;

    public static FxTargetProcess Launch()
    {
        var exe = s_compiled.Value
            ?? throw new InvalidOperationException("The .NET Framework target could not be compiled.");

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;

        // Wait until the target says it is up. Attaching microseconds after spawn would race the
        // CLR's own load, which no real attach target (w3wp, iisexpress) does.
        var ready = process.StandardOutput.ReadLine();
        if (ready is null)
            throw new InvalidOperationException("The .NET Framework target exited before signalling readiness.");

        return new FxTargetProcess { Process = process };
    }

    /// <summary>
    /// A long-running target that signals readiness first, then repeatedly calls a method with a
    /// local worth inspecting.
    /// </summary>
    private const string TargetSource =
        """
        using System;
        using System.Diagnostics;

        namespace FxTarget
        {
            // Renders through its display string; Id has no backing field, so producing one runs
            // the getter in the target.
            [DebuggerDisplay("Order {Id}: {Name,nq}")]
            public class Order
            {
                private int _id;

                [DebuggerBrowsable(DebuggerBrowsableState.Never)]
                private string _secret;

                public string Name;

                public Order(int id, string name)
                {
                    _id = id;
                    Name = name;
                    _secret = "hidden";
                }

                public int Id { get { return _id; } }
            }

            // The shape a proxy exists for: the fields are storage, not content.
            [DebuggerTypeProxy(typeof(BagView))]
            public class Bag
            {
                internal int[] _slots = new int[] { 7, 8, 9, 0 };
                internal int _count = 3;
            }

            public class BagView
            {
                private Bag _bag;

                public BagView(Bag bag) { _bag = bag; }

                [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
                public int[] Items
                {
                    get
                    {
                        int[] used = new int[_bag._count];
                        Array.Copy(_bag._slots, used, _bag._count);
                        return used;
                    }
                }

                public int Count { get { return _bag._count; } }
            }

            public static class Guarded
            {
                // A step into this should come straight back out under Just My Code.
                [DebuggerStepThrough]
                public static int Twice(int value)
                {
                    int doubled = value * 2;
                    return doubled;
                }
            }

            public class Counter
            {
                private int _count;

                // Deliberately not an auto-property: there is no <Count>k__BackingField, so
                // reading this requires calling the getter.
                public int Count { get { return _count; } }

                // Computed: no backing field exists at all.
                public int Doubled { get { return _count * 2; } }

                public void Bump() { _count++; }

                public string Describe() { return "count=" + _count; }
            }

            public static class Program
            {
                public static void Main()
                {
                    Console.WriteLine("ready");
                    Console.Out.Flush();
                    Counter counter = new Counter();
                    for (int i = 0; i < 100000; i++)
                    {
                        counter.Bump();
                        Compute(i, counter);
                        System.Threading.Thread.Sleep(20);
                    }
                }

                private static int Compute(int input, Counter counter)
                {
                    Order order = new Order(input, "sample");
                    Bag bag = new Bag();
                    int result = input * 2;
                    int guarded = Guarded.Twice(result);
                    return result + guarded - guarded;
                }
            }
        }
        """;

    private static string? Compile()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var frameworkCompilers = new[] { "Framework64", "Framework" }
            .Select(d => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", d, "v4.0.30319", "csc.exe"));
        var csc = new[]
            {
                WorkspaceService.LegacyMsBuildDirectory is { } msbuild
                    ? Path.Combine(msbuild, "Roslyn", "csc.exe")
                    : null,
            }
            .Concat(frameworkCompilers)
            .FirstOrDefault(File.Exists);

        if (csc is null)
        {
            Console.Error.WriteLine(
                "[FxTargetProcess] No Visual Studio or .NET Framework C# compiler was found.");
            return null;
        }

        var directory = Path.Combine(Path.GetTempPath(), "roslynsense-fxtarget-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);

        var source = Path.Combine(directory, "Program.cs");
        var exe = Path.Combine(directory, "FxTarget.exe");

        File.WriteAllText(source, TargetSource);

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = csc,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
            ArgumentList = { "-nologo", "-debug:full", "-out:" + exe, source },
        })!;

        // Drain both pipes while the compiler runs. Waiting before reading redirected output can
        // deadlock when a compiler or its host writes enough diagnostics to fill either pipe.
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Console.Error.WriteLine($"[FxTargetProcess] Compiler '{csc}' timed out after 120 seconds.");
            return null;
        }

        Task.WaitAll([output, errors], 5_000);
        if (process.ExitCode == 0 && File.Exists(exe))
            return exe;

        Console.Error.WriteLine(
            $"[FxTargetProcess] Compiler '{csc}' exited with code {process.ExitCode}. " +
            $"stdout: {output.Result} stderr: {errors.Result}");
        return null;
    }

    public void Dispose()
    {
        try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
        try { Process.Dispose(); } catch { }
    }
}
