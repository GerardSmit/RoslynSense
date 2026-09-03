using System.Diagnostics;
using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers ending a debug session without killing the debuggee mid-instruction.
/// </summary>
/// <remarks>
/// Stopping a session used to be a termination: the process died where it stood, so hosted
/// services never saw <c>StopAsync</c>, <c>finally</c> blocks never ran and nothing was flushed —
/// an app under the debugger was the one place it never got the shutdown it gets everywhere else.
/// The target here records what actually ran on its way out, which is the only way to tell a
/// clean exit from a kill after the fact.
/// </remarks>
[Collection(DebuggerCollection.Name)]
public class DebugShutdownTests
{
    [RequiresShutdownTargetFact]
    public async Task ShutdownLetsTheDebuggeeRunItsShutdownPath()
    {
        using var target = ShutdownTarget.Create();
        var session = new DebugSession(1);

        await using var _ = await target.RunUntilStoppedAsync(session);

        var (graceful, error) = await session.ShutdownAsync(TimeSpan.FromSeconds(20));

        Assert.True(graceful, error);
        Assert.Equal(["started", "shutdown requested", "hosted service stopped"], target.Journal);
    }

    /// <summary>
    /// A debuggee that ignores the request must not hold the session open forever — the fallback
    /// is the old behaviour, reported rather than silent.
    /// </summary>
    [RequiresShutdownTargetFact]
    public async Task ADebuggeeThatWillNotShutDownIsTerminated()
    {
        using var target = ShutdownTarget.Create(ignoresShutdown: true);
        var session = new DebugSession(1);

        await using var _ = await target.RunUntilStoppedAsync(session);

        var (graceful, error) = await session.ShutdownAsync(TimeSpan.FromSeconds(3));

        Assert.False(graceful);
        Assert.Contains("did not exit", error);
        Assert.DoesNotContain("hosted service stopped", target.Journal);
    }

    /// <summary>
    /// The shutdown has to survive the state a session is normally stopped in: sitting at a
    /// breakpoint, with more breakpoints armed on the way out.
    /// </summary>
    [RequiresShutdownTargetFact]
    public async Task ShutdownFromABreakpointIsNotTrappedByTheRemainingBreakpoints()
    {
        using var target = ShutdownTarget.Create();
        var session = new DebugSession(1);

        await using var _ = await target.RunUntilStoppedAsync(session, breakOnLoop: true);

        var (graceful, error) = await session.ShutdownAsync(TimeSpan.FromSeconds(20));

        Assert.True(graceful, error);
        Assert.Contains("hosted service stopped", target.Journal);
    }
}

/// <summary>Skips when no .NET Framework compiler is available to build the target.</summary>
public sealed class RequiresShutdownTargetFactAttribute : FactAttribute
{
    public RequiresShutdownTargetFactAttribute()
    {
        if (!ShutdownTarget.IsAvailable)
            Skip = "A .NET Framework target could not be compiled on this machine.";
    }
}

/// <summary>
/// A .NET Framework debuggee that writes down every stage of its own shutdown.
/// </summary>
/// <remarks>
/// It stands in for an app with hosted services: a console control handler that starts an orderly
/// stop, and work in the shutdown path that only runs when the process is allowed to finish. A
/// journal on disk rather than stdout, because a killed process's pipe races the assertion.
/// </remarks>
internal sealed class ShutdownTarget : IDisposable
{
    private static readonly Lazy<string?> s_compiled = new(() => Compile(ignoresShutdown: false));
    private static readonly Lazy<string?> s_deaf = new(() => Compile(ignoresShutdown: true));

    private readonly string _directory;

    public static bool IsAvailable => s_compiled.Value is not null;

    /// <summary>The line the loop body sits on, found in the source so it cannot drift.</summary>
    public static int LoopLine => Array.FindIndex(
        Source(false).ReplaceLineEndings("\n").Split('\n'),
        line => line.Contains(LoopStatement, StringComparison.Ordinal)) + 1;

    private const string LoopStatement = "ticks = ticks + 1;";

    public required string Executable { get; init; }
    public required string SourcePath { get; init; }
    public required string JournalPath { get; init; }

    /// <summary>What the debuggee recorded, in order.</summary>
    public string[] Journal => File.Exists(JournalPath)
        ? File.ReadAllLines(JournalPath).Where(l => l.Length > 0).ToArray()
        : [];

    public static ShutdownTarget Create(bool ignoresShutdown = false)
    {
        var exe = (ignoresShutdown ? s_deaf : s_compiled).Value
            ?? throw new InvalidOperationException("The .NET Framework target could not be compiled.");

        var directory = Path.GetDirectoryName(exe)!;
        return new ShutdownTarget(directory)
        {
            Executable = exe,
            SourcePath = Path.Combine(directory, "Program.cs"),
            JournalPath = Path.Combine(
                Path.GetTempPath(), "roslynsense-shutdown-" + Guid.NewGuid().ToString("N") + ".log"),
        };
    }

    private ShutdownTarget(string directory) => _directory = directory;

    /// <summary>
    /// Launches the target under the session and returns once it is running (or stopped at the
    /// loop, when asked), so a shutdown is exercised from the state a real session is in.
    /// </summary>
    public async Task<IAsyncDisposable> RunUntilStoppedAsync(DebugSession session, bool breakOnLoop = false)
    {
        var ready = new TaskCompletionSource();
        var stopped = new TaskCompletionSource();

        _ = Task.Run(async () =>
        {
            await foreach (var e in session.Events.ReadAllAsync())
            {
                if (e.Kind == DebugEventKind.Output && e.Message.Contains("ready"))
                    ready.TrySetResult();
                if (e.Kind == DebugEventKind.Breakpoint)
                    stopped.TrySetResult();
            }
        });

        session.Launch(
            Executable,
            [],
            breakOnLoop
                ? [new BreakpointSpec { Id = "1", FilePath = SourcePath, Line = (uint)LoopLine, Enabled = true }]
                : [],
            new Dictionary<string, string> { ["SHUTDOWN_JOURNAL"] = JournalPath },
            _directory,
            DebugRuntime.NetFramework);

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (breakOnLoop)
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30));

        return new Teardown(session);
    }

    public void Dispose()
    {
        try { File.Delete(JournalPath); } catch { /* best effort */ }
    }

    private sealed class Teardown(DebugSession session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            try { session.Terminate(); } catch { /* already gone */ }
            return ValueTask.CompletedTask;
        }
    }

    private static string? Compile(bool ignoresShutdown)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var csc = new[] { "Framework64", "Framework" }
            .Select(d => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", d, "v4.0.30319", "csc.exe"))
            .FirstOrDefault(File.Exists);

        if (csc is null)
            return null;

        var directory = Path.Combine(
            Path.GetTempPath(), "roslynsense-shutdowntarget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var source = Path.Combine(directory, "Program.cs");
        var exe = Path.Combine(directory, "ShutdownTarget.exe");
        File.WriteAllText(source, Source(ignoresShutdown));

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = csc,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
            ArgumentList = { "-nologo", "-debug:full", "-out:" + exe, source },
        })!;

        process.WaitForExit(120_000);
        return process.ExitCode == 0 && File.Exists(exe) ? exe : null;
    }

    private static string Source(bool ignoresShutdown) =>
        $$"""
        using System;
        using System.IO;
        using System.Threading;

        namespace ShutdownTarget
        {
            public static class Program
            {
                private static string _journal;
                private static volatile bool _stopping;

                private static void Record(string stage)
                {
                    lock (typeof(Program))
                    {
                        File.AppendAllText(_journal, stage + Environment.NewLine);
                    }
                }

                public static void Main()
                {
                    _journal = Environment.GetEnvironmentVariable("SHUTDOWN_JOURNAL");
                    Record("started");

                    Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
                    {
                        // Cancel the default kill and start an orderly stop, exactly as the
                        // generic host's ConsoleLifetime does.
                        e.Cancel = true;
                        Record("shutdown requested");
                        {{(ignoresShutdown ? "// This target refuses to stop." : "_stopping = true;")}}
                    };

                    Console.WriteLine("ready");
                    Console.Out.Flush();

                    int ticks = 0;
                    while (!_stopping)
                    {
                        ticks = ticks + 1;
                        Thread.Sleep(20);
                    }

                    // Stands in for a hosted service's StopAsync: only a process that is allowed
                    // to finish ever gets here.
                    Thread.Sleep(100);
                    Record("hosted service stopped");
                }
            }
        }
        """;
}
