using System.Security.Cryptography;
using System.Text;
using RoslynMCP.Services;

namespace RoslynMCP.Daemon;

/// <summary>
/// Derives the shared-host identity (solution key) and the OS resource names that key it:
/// the named pipe, the per-host lock directory, and the cross-process spawn mutex. All are a
/// stable hash of the normalized solution path so every client of the same solution agrees.
/// </summary>
internal static class HostPaths
{
    /// <summary>
    /// Returns the normalized solution path that owns <paramref name="startPath"/> (a file or
    /// directory), or <c>null</c> when no solution is found (loose project → no shared host).
    /// </summary>
    public static string? ResolveSolutionKey(string startPath)
    {
        try
        {
            string? sln = PathHelper.FindNearestSolution(startPath);
            return string.IsNullOrEmpty(sln) ? null : Path.GetFullPath(sln);
        }
        catch
        {
            return null;
        }
    }

    public static string Hash(string solutionKey)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(solutionKey.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant(); // 16 hex chars
    }

    public static string PipeName(string solutionKey) => $"roslyn-mcp-host-{Hash(solutionKey)}";

    public static string LockDirectory(string solutionKey) =>
        Path.Combine(Path.GetTempPath(), "roslyn-mcp-daemon", Hash(solutionKey));

    public static string LockFilePath(string solutionKey) =>
        Path.Combine(LockDirectory(solutionKey), ".lock");

    /// <summary>Global mutex name guarding the connect-or-spawn race for one solution.</summary>
    public static string SpawnMutexName(string solutionKey) => $@"Global\RoslynMcpDaemon_{Hash(solutionKey)}";
}
