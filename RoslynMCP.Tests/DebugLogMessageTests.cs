using System.Diagnostics;
using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers <c>Debug.WriteLine</c> and <c>Trace.WriteLine</c> reaching the session as output.
/// </summary>
/// <remarks>
/// These never touch stdout: <c>DefaultTraceListener</c> hands them to the debugger through
/// <c>OutputDebugString</c> and nowhere else, so anything reading the debuggee's streams — the
/// output log a chat reads with GetProjectOutput included — saw nothing at all. The runtime only
/// raises them as LogMessage callbacks once a debugger asks, which is what makes attaching the
/// thing that turns them on.
/// </remarks>
[Collection(DebuggerCollection.Name)]
public class DebugLogMessageTests
{
    [RequiresLogMessageTargetFact]
    public async Task DebugWriteLineReachesTheSessionAsOutput()
    {
        using var target = LogMessageTarget.Create();
        var session = new DebugSession(1);

        await using var _ = await target.LaunchAsync(session);

        var plain = await target.WaitForOutputAsync(LogMessageTarget.PlainMessage);
        Assert.Contains(LogMessageTarget.PlainMessage, plain);
    }

    /// <summary>
    /// The two-argument overload keeps its category.
    /// </summary>
    /// <remarks>
    /// The category is not carried beside the message: the framework folds it into the text as
    /// <c>category: message</c> before the debugger ever sees it, and the LogMessage callback
    /// reports an empty switch name. Asserted here because it is the difference between telling
    /// a caller to grep for <c>[Category]</c> and telling them to grep for <c>Category:</c>.
    /// </remarks>
    [RequiresLogMessageTargetFact]
    public async Task ACategorisedMessageKeepsItsCategory()
    {
        using var target = LogMessageTarget.Create();
        var session = new DebugSession(1);

        await using var _ = await target.LaunchAsync(session);

        var categorised = await target.WaitForOutputAsync(LogMessageTarget.CategorisedMessage);
        Assert.Contains($"{LogMessageTarget.Category}: ", categorised);
    }
}

/// <summary>Skips when no .NET Framework compiler is available to build the target.</summary>
public sealed class RequiresLogMessageTargetFactAttribute : FactAttribute
{
    public RequiresLogMessageTargetFactAttribute()
    {
        if (!LogMessageTarget.IsAvailable)
            Skip = "A .NET Framework target could not be compiled on this machine.";
    }
}

/// <summary>
/// A .NET Framework debuggee whose only job is to write to <c>Debug</c> and <c>Trace</c>.
/// </summary>
/// <remarks>
/// Compiled with <c>-d:DEBUG;TRACE</c> on purpose: both APIs are <c>[Conditional]</c>, so without
/// those symbols the calls are removed by the compiler and the test would pass or fail on whether
/// the target was built right rather than on whether the callback works.
/// </remarks>
internal sealed class LogMessageTarget : IDisposable
{
    public const string PlainMessage = "roslynsense-debug-plain";
    public const string CategorisedMessage = "roslynsense-debug-categorised";
    public const string Category = "RoslynSense";

    private static readonly Lazy<string?> s_compiled = new(Compile);

    private readonly string _directory;
    private readonly List<string> _output = [];
    private readonly Lock _gate = new();

    public static bool IsAvailable => s_compiled.Value is not null;

    public required string Executable { get; init; }

    public static LogMessageTarget Create()
    {
        var exe = s_compiled.Value
            ?? throw new InvalidOperationException("The .NET Framework target could not be compiled.");

        return new LogMessageTarget(Path.GetDirectoryName(exe)!) { Executable = exe };
    }

    private LogMessageTarget(string directory) => _directory = directory;

    /// <summary>Launches the target and returns once it is running.</summary>
    public async Task<IAsyncDisposable> LaunchAsync(DebugSession session)
    {
        // Asynchronous continuations for the reason WaitForOutputAsync polls: completing this
        // from the reading loop would otherwise resume the test on that loop's thread, and it
        // would stop reading events at the moment the test starts wanting them.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            await foreach (var e in session.Events.ReadAllAsync())
            {
                if (e.Kind != DebugEventKind.Output)
                    continue;

                // "ready" goes to stdout, so it arrives whether or not the callback works — which
                // is what makes a missing log message a failed assertion rather than a hang.
                if (e.Message.Contains("ready", StringComparison.Ordinal))
                    ready.TrySetResult();

                lock (_gate)
                    _output.Add(e.Message);
            }
        });

        session.Launch(Executable, [], [], null, _directory, DebugRuntime.NetFramework);

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return new Teardown(session);
    }

    /// <summary>
    /// The first output line containing <paramref name="needle"/>, waiting for it.
    /// </summary>
    /// <remarks>
    /// Polled rather than signalled. Completing a TaskCompletionSource runs its continuations
    /// synchronously by default, so signalling from the event-reading loop hands that thread to
    /// the waiter and it never returns to reading — the test then hangs the whole test host
    /// rather than failing, which is a far worse way to be wrong.
    /// </remarks>
    public async Task<string> WaitForOutputAsync(string needle)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_output.FirstOrDefault(l => l.Contains(needle, StringComparison.Ordinal)) is { } hit)
                    return hit;
            }

            await Task.Delay(100);
        }

        lock (_gate)
            throw new TimeoutException(
                $"No output containing '{needle}'. Received: {string.Join(" | ", _output)}");
    }

    public void Dispose() { }

    private sealed class Teardown(DebugSession session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            try { session.Terminate(); } catch { /* already gone */ }
            return ValueTask.CompletedTask;
        }
    }

    private static string? Compile()
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
            Path.GetTempPath(), "roslynsense-logmessagetarget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var source = Path.Combine(directory, "Program.cs");
        var exe = Path.Combine(directory, "LogMessageTarget.exe");
        File.WriteAllText(source, Source());

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = csc,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
            ArgumentList = { "-nologo", "-debug:full", "-d:DEBUG;TRACE", "-out:" + exe, source },
        })!;

        process.WaitForExit(120_000);
        return process.ExitCode == 0 && File.Exists(exe) ? exe : null;
    }

    private static string Source() =>
        $$"""
        using System;
        using System.Diagnostics;
        using System.Threading;

        namespace LogMessageTarget
        {
            public static class Program
            {
                public static void Main()
                {
                    Console.WriteLine("ready");
                    Console.Out.Flush();

                    // Repeated because the debugger asks for log messages while handling
                    // CreateProcess, and a single write racing that would make the test flaky
                    // rather than wrong.
                    for (int i = 0; i < 40; i++)
                    {
                        Debug.WriteLine("{{PlainMessage}}");
                        Debug.WriteLine("{{CategorisedMessage}}", "{{Category}}");
                        Thread.Sleep(250);
                    }
                }
            }
        }
        """;
}
