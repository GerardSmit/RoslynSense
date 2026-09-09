using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Services.Testing;
using Xunit;

namespace RoslynMCP.Tests;

public sealed class FrameworkBuildProcessTests : IClassFixture<FrameworkBuildProcessFixture>
{
    private readonly FrameworkBuildProcessFixture _fixture;

    public FrameworkBuildProcessTests(FrameworkBuildProcessFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task LargeStandardErrorCannotBlockStandardOutputOrLoseTheBuildFailure()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var result = await TestRunService.RunBuildProcessAsync(_fixture.StartInfo("flood"), timeout.Token);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(new string('O', 1024 * 1024), result.Output);
        Assert.Equal(new string('E', 1024 * 1024) + "error BUILD123: fixture failure", result.Error);
    }

    [Fact]
    public async Task BuildStandardInputIsClosedInsteadOfSharingTheHostsInput()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var result = await TestRunService.RunBuildProcessAsync(_fixture.StartInfo("stdin"), timeout.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("closed", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndDeadlinesStopTheBuildAndItsChildBeforeReturning(bool deadline)
    {
        string parentFile = _fixture.NewPath("parent.pid");
        string childFile = _fixture.NewPath("child.pid");
        using var cancellation = new CancellationTokenSource();
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = TestRunService.RunBuildProcessAsync(
            _fixture.StartInfo("tree", parentFile, childFile), cancellation.Token);
        int? parent = null;
        int? child = null;
        try
        {
            parent = await ReadPidAsync(parentFile, testTimeout.Token);
            child = await ReadPidAsync(childFile, testTimeout.Token);
            Assert.False(HasExited(parent.Value));
            Assert.False(HasExited(child.Value));

            if (deadline)
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            else
                cancellation.Cancel();

            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(testTimeout.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.True(HasExited(parent.Value));
            // Process.Kill(true) kills children before their parent, though another process's
            // termination can take a moment to become observable on all operating systems.
            while (!HasExited(child.Value))
                await Task.Delay(10, testTimeout.Token);
        }
        finally
        {
            cancellation.Cancel();
            Kill(child);
            Kill(parent);
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task PrecancelledBuildDoesNotStartAProcess()
    {
        string marker = _fixture.NewPath("not-started.pid");
        var startInfo = _fixture.StartInfo("wait", marker);
        // Starting this command would throw a process-start error, so cancellation must be
        // observed before asking the OS to launch anything, independent of process timing.
        startInfo.FileName = _fixture.NewPath("missing-executable");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestRunService.RunBuildProcessAsync(
            startInfo, new CancellationToken(canceled: true)));

        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task TargetPathEvaluationDrainsDiagnosticsAndOnlyAcceptsSuccessfulOutput(int exitCode)
    {
        string path = _fixture.NewPath("built assembly.dll");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        string? result = await TestRunService.ReadFrameworkTargetPathAsync(
            _fixture.StartInfo("target", path, exitCode.ToString()), timeout.Token);

        Assert.Equal(exitCode == 0 ? path : null, result);
    }

    private static async Task<int> ReadPidAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path, ct), out int pid))
                    return pid;
            }
            catch (IOException) { }
            await Task.Delay(10, ct);
        }
    }

    private static bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
    }

    private static void Kill(int? pid)
    {
        if (pid is null) return;
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }
}

public sealed class FrameworkBuildProcessFixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"framework-build-process-{Guid.NewGuid():N}");
    private string AssemblyPath => Path.Combine(_directory, "BuildProcessFixture.dll");

    public FrameworkBuildProcessFixture()
    {
        Directory.CreateDirectory(_directory);
        const string source = """
            using System;
            using System.Diagnostics;
            using System.IO;
            using System.Reflection;
            using System.Threading;

            internal static class Program
            {
                private static int Main(string[] args)
                {
                    if (args[0] == "flood")
                    {
                        Console.Error.Write(new string('E', 1024 * 1024));
                        Console.Out.Write(new string('O', 1024 * 1024));
                        Console.Error.Write("error BUILD123: fixture failure");
                        return 7;
                    }
                    if (args[0] == "target")
                    {
                        Console.Error.Write(new string('E', 1024 * 1024));
                        Console.WriteLine("warning MSB123: an evaluation warning");
                        Console.WriteLine("  " + args[1] + "  ");
                        return int.Parse(args[2]);
                    }
                    if (args[0] == "stdin")
                    {
                        Console.Write(Console.In.ReadToEnd() + "closed");
                        return 0;
                    }
                    if (args[0] == "tree")
                    {
                        var child = new ProcessStartInfo(Environment.ProcessPath)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };
                        child.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                        child.ArgumentList.Add("wait");
                        child.ArgumentList.Add(args[2]);
                        Process.Start(child);
                    }
                    File.WriteAllText(args[1], Environment.ProcessId.ToString());
                    Thread.Sleep(Timeout.Infinite);
                    return 0;
                }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("BuildProcessFixture", [CSharpSyntaxTree.ParseText(source)],
            references, new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        var emitted = compilation.Emit(AssemblyPath);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        File.WriteAllText(Path.ChangeExtension(AssemblyPath, ".runtimeconfig.json"), JsonSerializer.Serialize(new
        {
            runtimeOptions = new
            {
                tfm = $"net{Environment.Version.Major}.0",
                framework = new { name = "Microsoft.NETCore.App", version = $"{Environment.Version.Major}.{Environment.Version.Minor}.0" },
            },
        }));
    }

    public string NewPath(string name) => Path.Combine(_directory, $"{Guid.NewGuid():N}-{name}");

    public ProcessStartInfo StartInfo(params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _directory,
        };
        info.ArgumentList.Add(AssemblyPath);
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
