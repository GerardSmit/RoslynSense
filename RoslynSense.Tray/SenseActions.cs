using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace RoslynSense.Tray;

/// <summary>
/// The things the menu can do: open a path, stop an app, stop a host.
/// </summary>
internal static class SenseActions
{
    /// <summary>Opens a file, folder or URL with whatever the shell associates with it.</summary>
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warn($"Could not open '{target}'.\r\n\r\n{ex.Message}");
        }
    }

    public static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                ArgumentList = { "/select,", path },
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Warn($"Could not show '{path}'.\r\n\r\n{ex.Message}");
        }
    }

    public static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Warn($"Could not copy to the clipboard.\r\n\r\n{ex.Message}");
        }
    }

    /// <summary>
    /// Stops a launched app and tells the chat that started it why it vanished.
    /// </summary>
    /// <remarks>
    /// The note matters more than the kill. An agent that launched an app and finds the process
    /// gone reads it as a crash and helpfully restarts it — so the registry's own kill path
    /// enqueues an explanation for the plugin hook to inject, and a kill from here has to do the
    /// same or the tray becomes a way to fight your own assistant.
    /// </remarks>
    public static void StopApp(AppEntry app)
    {
        if (!TryKillTree(app.Pid, out string? error))
        {
            Warn($"Could not stop '{app.ProjectName}' (pid {app.Pid}).\r\n\r\n{error}");
            return;
        }

        try { File.Delete(app.FilePath); } catch { }

        Notify(app.ProjectPath,
            $"The user stopped the running process '{app.ProjectName}' (pid {app.Pid}, " +
            $"session {app.SessionId}, project '{app.ProjectPath}') from the RoslynSense tray icon. " +
            "It was not a crash. Do not restart it unless the user asks.");
    }

    /// <summary>
    /// Stops a host daemon. Safe to do at any time: the lock and pipe are released by the OS on
    /// exit, and the next tool call spawns a fresh one.
    /// </summary>
    public static void StopHost(HostEntry host)
    {
        if (!TryKillTree(host.Pid, out string? error))
            Warn($"Could not stop the host for '{host.SolutionName}' (pid {host.Pid}).\r\n\r\n{error}");
    }

    /// <summary>
    /// Kills a process and its children, falling back to the process alone when we are one of
    /// those children.
    /// </summary>
    /// <remarks>
    /// The tray is started by a host, which makes it a descendant of that host — and
    /// <see cref="Process.Kill(bool)"/> refuses to fell a tree containing the caller. So stopping
    /// the host that happens to have spawned this tray degrades to a single-process kill, while
    /// every other host still gets the full tree. The cost of the fallback is that the daemon's
    /// own MSBuild worker processes outlive it in that one case; the alternative is a "stop" that
    /// reports failure and stops nothing.
    /// </remarks>
    private static bool TryKillTree(int pid, out string? error)
    {
        error = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                process.Kill();
            }
            return true;
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Enqueues a message for chats working in the solution that owns <paramref name="anchorPath"/>.
    /// </summary>
    /// <remarks>
    /// Reimplements <c>PendingNotificationStore.Enqueue</c> rather than referencing it — the format
    /// is one plain-text file per message in a directory keyed by the build-independent solution
    /// hash, which exists precisely so out-of-band writers (the plugin's node hook does the same
    /// derivation in JavaScript) can participate without linking against the tool.
    /// </remarks>
    private static void Notify(string anchorPath, string message)
    {
        try
        {
            string? solution = FindNearestSolution(anchorPath);
            if (solution is null)
                return;

            string dir = Path.Combine(Path.GetTempPath(), "roslyn-sense", "notifications",
                SolutionHash(Path.GetFullPath(solution)));
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, $"{DateTime.UtcNow.Ticks}-{Guid.NewGuid():N}.txt"), message);
        }
        catch
        {
            // Advisory channel; a stop that goes unannounced still stopped.
        }
    }

    /// <summary>SHA-256 of the lowercased full path, first 8 bytes as hex — the derivation the
    /// daemon and the hook script both use.</summary>
    private static string SolutionHash(string solutionPath)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(solutionPath.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    private static string? FindNearestSolution(string startPath)
    {
        try
        {
            var dir = File.Exists(startPath)
                ? new FileInfo(startPath).Directory
                : new DirectoryInfo(startPath);

            for (; dir is not null; dir = dir.Parent)
            {
                string? found = dir.EnumerateFiles("*.slnx").Concat(dir.EnumerateFiles("*.sln"))
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()?.FullName;
                if (found is not null)
                    return found;
            }
        }
        catch
        {
        }
        return null;
    }

    private static void Warn(string message) =>
        MessageBox.Show(message, "RoslynSense", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
