using System.Diagnostics;
using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers what a frame inside an async method looks like: the compiler moved the body into a
/// state machine and every local that crosses an await into its fields, and the debugger must
/// move them all back — <c>total</c> as <c>total</c>, no <c>&lt;&gt;t__builder</c>, no state
/// machine posing as <c>this</c>.
/// </summary>
[Collection(DebuggerCollection.Name)]
public class NetFxAsyncFrameTests
{
    private const string BreakpointStatement = "Look(total, factor);";

    private const string Source =
        """
        using System;
        using System.Threading.Tasks;

        namespace X86AsyncTarget
        {
            public static class Program
            {
                public static void Main()
                {
                    Console.WriteLine("ready");
                    Console.Out.Flush();
                    RunAsync().Wait();
                }

                private static async Task RunAsync()
                {
                    for (int i = 0; i < 100000; i++)
                    {
                        await LookAsync(21);
                    }
                }

                private static async Task LookAsync(int factor)
                {
                    int total = factor * 2;
                    await Task.Delay(10);
                    Look(total, factor);
                }

                private static void Look(int total, int factor)
                {
                }
            }
        }
        """;

    [RequiresX86WorkerFact]
    public async Task WhenStoppedInAnAsyncMethodThenHoistedLocalsShowUnderTheirOwnNames()
    {
        var directory = Path.Combine(Path.GetTempPath(), "roslynsense-x86-async-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "Program.cs");
        var exe = Path.Combine(directory, "X86AsyncTarget.exe");
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

            using var engine = new WorkerDebugEngine(X86Target.WorkerPath!, sessionId: 4);
            var breakpointHit = new TaskCompletionSource<DebugEvent>();
            _ = Task.Run(async () =>
            {
                await foreach (var e in engine.Events.ReadAllAsync())
                {
                    if (e.Kind == DebugEventKind.Breakpoint)
                        breakpointHit.TrySetResult(e);
                }
            });

            engine.Attach(
                target.Id,
                [new BreakpointSpec { FilePath = sourcePath, Line = (uint)breakpointLine }],
                DebugRuntime.NetFramework);
            await breakpointHit.Task.WaitAsync(TimeSpan.FromSeconds(45));

            var locals = await engine.VariablesAsync(0);

            Assert.Equal("42", Assert.Single(locals, v => v.Name == "total").Value);
            Assert.Equal("21", Assert.Single(locals, v => v.Name == "factor").Value);
            Assert.DoesNotContain(locals, v => v.Name.StartsWith("<>", StringComparison.Ordinal));
            Assert.DoesNotContain(locals, v => v.Name == "this" && v.Type.Contains("d__", StringComparison.Ordinal));

            engine.Terminate();
        }
        finally
        {
            try { if (!target.HasExited) target.Kill(entireProcessTree: true); } catch { }
        }
    }
}
