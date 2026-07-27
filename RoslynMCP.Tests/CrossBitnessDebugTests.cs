using System.Diagnostics;
using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers debugging a target whose bitness differs from this process.
/// </summary>
/// <remarks>
/// ICorDebug cannot attach across x86/x64, so a 32-bit target — a 32-bit IIS Express app pool
/// being the realistic case — must be driven from a 32-bit worker running the same engine.
/// </remarks>
[Collection(DebuggerCollection.Name)]
public class CrossBitnessDebugTests
{
    [RequiresX86WorkerFact]
    public void WhenTargetIsThirtyTwoBitThenItIsDetectedAsX86()
    {
        using var target = X86Target.Launch();

        Assert.Equal(DebugArch.X86, ProcessArch.OfProcess(target.Process.Id));

        // The host test process is x64, so this really is a cross-architecture pair.
        Assert.Equal(DebugArch.X64, ProcessArch.Host);
    }

    [RequiresX86WorkerFact]
    public async Task WhenDebuggingThirtyTwoBitTargetThenTheWorkerBindsAndHitsTheBreakpoint()
    {
        using var target = X86Target.Launch();
        using var engine = new WorkerDebugEngine(X86Target.WorkerPath!, sessionId: 1);

        var hit = new TaskCompletionSource<DebugEvent>();
        _ = Task.Run(async () =>
        {
            await foreach (var e in engine.Events.ReadAllAsync())
            {
                if (e.Kind == DebugEventKind.Breakpoint)
                    hit.TrySetResult(e);
            }
        });

        engine.Attach(
            target.Process.Id,
            [new BreakpointSpec { FilePath = X86Target.SourcePath, Line = (uint)X86Target.BreakpointLine }],
            DebugRuntime.NetFramework);

        var stop = await hit.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.Equal(X86Target.BreakpointLine, (int)stop.Line);

        var frames = await engine.StackTraceAsync();
        Assert.Contains(frames, f => f.Method.Contains("Compute", StringComparison.Ordinal));

        var variables = await engine.VariablesAsync(0);
        Assert.Contains(variables, v => v.Name == "input");

        // Function evaluation has to work across the worker boundary too.
        var (ok, value, error) = await engine.EvaluateAsync(0, "input");
        Assert.True(ok, error);
        Assert.True(int.TryParse(value, out _), $"expected an integer, got '{value}'");

        engine.Terminate();
    }

    [Fact]
    public void WhenBitnessMatchesThenNoWorkerIsUsed()
    {
        // The test host is x64 and so is this process, so an x64 target debugs in-process.
        using var engine = DebugEngineFactory.ForProcess(Environment.ProcessId);

        Assert.IsType<InProcessDebugEngine>(engine);
    }
}

/// <summary>
/// A deliberately 32-bit .NET Framework target, plus the x86 worker needed to debug it.
/// </summary>
internal static class X86Target
{
    private const string BreakpointStatement = "int result = input * 2;";

    public static int BreakpointLine => Array.FindIndex(
        Source.ReplaceLineEndings("\n").Split('\n'),
        line => line.Contains(BreakpointStatement, StringComparison.Ordinal)) + 1;

    private static readonly Lazy<string?> s_compiled = new(Compile);
    private static readonly Lazy<string?> s_worker = new(FindWorker);

    public static string? WorkerPath => s_worker.Value;

    public static string SourcePath => Path.Combine(Path.GetDirectoryName(s_compiled.Value!)!, "Program.cs");

    /// <summary>Both the 32-bit target and the matching worker must exist for these tests to mean anything.</summary>
    public static bool IsAvailable => s_compiled.Value is not null && s_worker.Value is not null;

    private const string Source =
        """
        using System;

        namespace X86Target
        {
            public static class Program
            {
                public static void Main()
                {
                    Console.WriteLine("ready");
                    Console.Out.Flush();
                    for (int i = 0; i < 100000; i++)
                    {
                        Compute(i);
                        System.Threading.Thread.Sleep(20);
                    }
                }

                private static int Compute(int input)
                {
                    int result = input * 2;
                    return result;
                }
            }
        }
        """;

    public static X86Process Launch()
    {
        var exe = s_compiled.Value ?? throw new InvalidOperationException("no 32-bit target");

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;

        process.StandardOutput.ReadLine();
        return new X86Process(process);
    }

    private static string? Compile()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var csc = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");

        if (!File.Exists(csc))
            return null;

        var directory = Path.Combine(Path.GetTempPath(), "roslynsense-x86-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var source = Path.Combine(directory, "Program.cs");
        var exe = Path.Combine(directory, "X86Target.exe");
        File.WriteAllText(source, Source);

        // /platform:x86 sets the 32BitRequired corflag, so the image runs under WOW64 even on x64.
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = csc,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
            ArgumentList = { "-nologo", "-debug:full", "-platform:x86", "-out:" + exe, source },
        })!;

        process.WaitForExit(120_000);
        return process.ExitCode == 0 && File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// Locates the published x86 worker in the main project's output, which is where it is built.
    /// The test project does not publish its own copy.
    /// </summary>
    private static string? FindWorker()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RoslynMCP.sln")))
            directory = directory.Parent;

        if (directory is null)
            return null;

        var candidate = Path.Combine(
            directory.FullName, "RoslynMCP", "bin", "Debug", "net10.0",
            "workers", "x86", "RoslynMCP.DebugWorker.exe");

        return File.Exists(candidate) ? candidate : null;
    }

    internal sealed class X86Process(Process process) : IDisposable
    {
        public Process Process { get; } = process;

        public void Dispose()
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
            try { Process.Dispose(); } catch { }
        }
    }
}
