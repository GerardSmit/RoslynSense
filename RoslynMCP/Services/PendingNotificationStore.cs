using RoslynMCP.Daemon;

namespace RoslynMCP.Services;

/// <summary>
/// Per-solution pending message queue for LLM clients. Events that happen outside a chat's
/// process (e.g. the user kills a launched app from the editor) are enqueued here; the
/// Claude Code plugin's PreToolUse hook drains the queue and injects the messages as
/// additional context on the next tool call. One plain-text file per message — the hook is
/// a small node script, so the format stays trivially parseable and needs no locking.
/// The queue directory is keyed by the build-independent solution hash
/// (<see cref="HostPaths.SolutionHash"/>); the hook script mirrors the derivation.
/// </summary>
public static class PendingNotificationStore
{
    /// <summary>Enqueues a message for chats working in the solution that owns
    /// <paramref name="anchorPath"/>. No-op when no solution is found.</summary>
    public static void Enqueue(string anchorPath, string message)
    {
        try
        {
            string? solution = PathHelper.FindNearestSolution(anchorPath);
            if (solution is null)
                return;

            string dir = DirectoryFor(solution);
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, $"{DateTime.UtcNow.Ticks}-{Guid.NewGuid():N}.txt"),
                message);
        }
        catch
        {
            // Advisory channel — never fail the caller over it.
        }
    }

    /// <summary>Reads and removes all pending messages for the solution owning
    /// <paramref name="anchorPath"/>, oldest first. (The plugin hook does this in node;
    /// this method exists for tests and in-process consumers.)</summary>
    public static IReadOnlyList<string> Drain(string anchorPath)
    {
        var messages = new List<string>();
        try
        {
            string? solution = PathHelper.FindNearestSolution(anchorPath);
            if (solution is null)
                return messages;

            string dir = DirectoryFor(solution);
            if (!Directory.Exists(dir))
                return messages;

            foreach (var file in Directory.EnumerateFiles(dir, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    messages.Add(File.ReadAllText(file).Trim());
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Mid-write or already drained by another reader — skip.
                }
            }
        }
        catch
        {
        }
        return messages;
    }

    private static string DirectoryFor(string solutionPath) =>
        Path.Combine(Path.GetTempPath(), "roslyn-sense", "notifications",
            HostPaths.SolutionHash(Path.GetFullPath(solutionPath)));
}
