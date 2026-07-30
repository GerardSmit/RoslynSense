using System.Diagnostics;
using System.Net.Sockets;

namespace RoslynMCP.Services.Run;

public sealed record RunOutcome(AppSession? Session, string? Error, RunSpec Spec)
{
    public bool Succeeded => Session is not null && Error is null;
}

/// <summary>
/// Launches and stops applications, and waits for a web app to actually start serving.
/// </summary>
public sealed class AppRunService(AppSessionStore store)
{
    /// <summary>How long to wait for a web app's port to start accepting connections.</summary>
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(60);

    public async Task<RunOutcome> StartAsync(
        string projectPath,
        string configuration,
        string? launchProfile,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        var spec = RunConfigResolver.Resolve(projectPath, configuration, launchProfile, environment);
        if (!spec.CanRun)
            return new RunOutcome(null, spec.Error, spec);

        // Two instances of the same web project would fight over the port, and silently moving to
        // a different one hides that an app is already serving.
        if (spec.Port is { } port && store.LiveFor(spec.ProjectPath).FirstOrDefault() is { } existing)
        {
            return new RunOutcome(null,
                $"'{Path.GetFileNameWithoutExtension(spec.ProjectPath)}' is already running as " +
                $"'{existing.Id}' ({existing.Url ?? "no URL"}). Stop it first, or use that instance.",
                spec);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = spec.Executable,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in spec.Arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var pair in spec.Environment)
            startInfo.Environment[pair.Key] = pair.Value;

        var session = new AppSession
        {
            Id = store.NextId(spec.Kind),
            ProjectPath = spec.ProjectPath,
            Kind = spec.Kind,
            Process = new Process { StartInfo = startInfo, EnableRaisingEvents = true },
            StartedAtUtc = DateTime.UtcNow,
            Url = spec.Url,
            Port = spec.Port,
            DebugRuntime = spec.DebugRuntime,
        };

        session.Process.OutputDataReceived += (_, e) => { if (e.Data is not null) session.Append(e.Data); };
        session.Process.ErrorDataReceived += (_, e) => { if (e.Data is not null) session.Append(e.Data); };
        session.Process.Exited += (_, _) =>
        {
            try { session.MarkExited(session.Process.ExitCode); }
            catch { session.MarkExited(null); }
            RunningProcessRegistry.Unregister(session);
        };

        try
        {
            session.Process.Start();
            session.Process.BeginOutputReadLine();
            session.Process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            session.Dispose();
            return new RunOutcome(null, $"Could not start '{spec.Executable}': {ex.Message}", spec);
        }

        store.Add(session);
        RunningProcessRegistry.Register(session);

        if (spec.Port is { } listenPort)
        {
            var ready = await WaitForPortAsync(session, listenPort, cancellationToken);
            if (!ready && AppSessionStore.IsLive(session))
                session.Append("[roslyn-sense] The port never started accepting connections.");
        }

        session.MarkRunning();
        return new RunOutcome(session, null, spec);
    }

    /// <summary>
    /// Polls until the app accepts a connection, the process dies, or the timeout expires.
    /// </summary>
    private static async Task<bool> WaitForPortAsync(
        AppSession session, int port, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ReadinessTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (session.Process.HasExited)
                return false;

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port, cancellationToken);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(200, cancellationToken);
            }
        }

        return false;
    }

    /// <summary>Kills a session's process tree. Returns false when it was already finished.</summary>
    public static async Task<bool> StopAsync(AppSession session)
    {
        if (!AppSessionStore.IsLive(session))
        {
            session.MarkStopped();
            return false;
        }

        await BuildProcessHelper.KillAndDrainAsync(session.Process);
        session.MarkStopped();
        RunningProcessRegistry.Unregister(session);
        return true;
    }
}
