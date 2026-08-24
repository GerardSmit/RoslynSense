using System.Diagnostics;
using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers the <c>$exception</c> pseudo-local: stopping on an unhandled exception in a 32-bit
/// .NET Framework target must put the thrown exception itself in the frame's variable list,
/// expandable down to its message — VS's Locals window behaviour.
/// </summary>
[Collection(DebuggerCollection.Name)]
public class NetFxExceptionStopTests
{
    private const string BreakpointStatement = "Boom(marker);";

    private const string Source =
        """
        using System;

        namespace X86ThrowTarget
        {
            public static class Program
            {
                public static void Main()
                {
                    Console.WriteLine("ready");
                    Console.Out.Flush();
                    System.Threading.Thread.Sleep(500);
                    int marker = 1;
                    Boom(marker);
                }

                private static void Boom(int marker)
                {
                    throw new InvalidOperationException("boom-unhandled");
                }
            }
        }
        """;

    [RequiresX86WorkerFact]
    public async Task WhenAnExceptionGoesUnhandledThenItIsListedAsTheExceptionPseudoLocal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "roslynsense-x86-throw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "Program.cs");
        var exe = Path.Combine(directory, "X86ThrowTarget.exe");
        File.WriteAllText(sourcePath, Source);

        var csc = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        var compile = Process.Start(new ProcessStartInfo
        {
            FileName = csc,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
            ArgumentList = { "-nologo", "-debug:full", "-platform:x86", "-out:" + exe, sourcePath },
        })!;
        compile.WaitForExit(120_000);
        Assert.Equal(0, compile.ExitCode);

        var breakpointLine = Array.FindIndex(
            Source.ReplaceLineEndings("\n").Split('\n'),
            line => line.Contains(BreakpointStatement, StringComparison.Ordinal)) + 1;

        using var target = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        try
        {
            target.StandardOutput.ReadLine();

            using var engine = new WorkerDebugEngine(X86Target.WorkerPath!, sessionId: 3);
            var breakpointHit = new TaskCompletionSource<DebugEvent>();
            var exceptionStop = new TaskCompletionSource<DebugEvent>();
            _ = Task.Run(async () =>
            {
                await foreach (var e in engine.Events.ReadAllAsync())
                {
                    if (e.Kind == DebugEventKind.Breakpoint)
                        breakpointHit.TrySetResult(e);
                    if (e.Kind == DebugEventKind.Exception)
                        exceptionStop.TrySetResult(e);
                }
            });

            engine.Attach(
                target.Id,
                [new BreakpointSpec { FilePath = sourcePath, Line = (uint)breakpointLine }],
                DebugRuntime.NetFramework);
            await breakpointHit.Task.WaitAsync(TimeSpan.FromSeconds(45));

            // Run on into the throw; nothing catches it, so the next stop is the unhandled one.
            engine.Continue();
            await exceptionStop.Task.WaitAsync(TimeSpan.FromSeconds(45));

            var locals = await engine.VariablesAsync(0);
            var exception = Assert.Single(locals, v => v.Name == "$exception");
            Assert.False(string.IsNullOrEmpty(exception.VariablesReference), "the exception is not expandable");

            var members = await engine.ExpandAsync(0, exception.VariablesReference);
            Assert.Equal("\"boom-unhandled\"", Assert.Single(members, m => m.Name == "_message").Value);

            engine.Terminate();
        }
        finally
        {
            try { if (!target.HasExited) target.Kill(entireProcessTree: true); } catch { }
        }
    }
}
