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

    /// <summary>
    /// Identifies the build, so daemons of different builds never share a solution.
    /// </summary>
    /// <remarks>
    /// A daemon is reached by a name derived from the solution alone, which meant whichever build
    /// started first owned that solution and every later client silently got it — a development
    /// build connecting to an installed one, answering with the capabilities of whatever happened
    /// to run first. It surfaces as "no method by the name ..." for anything new, or worse, as old
    /// behaviour that looks like the new code not working. Keying by build as well means each one
    /// gets its own daemon and the question never arises.
    /// </remarks>
    /// The module id rather than the version: two builds of the same version are the normal case
    /// during development, and they are exactly the ones that must not share.
    private static readonly string s_buildTag =
        typeof(HostPaths).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    public static string Hash(string solutionKey)
    {
        byte[] bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{solutionKey.ToLowerInvariant()}|{s_buildTag}"));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant(); // 16 hex chars
    }

    /// <summary>
    /// The build-independent solution key, for state shared across builds and with the hook
    /// script.
    /// </summary>
    /// <remarks>
    /// The build salt in <see cref="Hash"/> exists so daemons never cross builds — but the
    /// on-disk stores (breakpoints, editor debug state, notifications) are the opposite case:
    /// the editor extension, the hook script, and whichever build of this tool is running must
    /// all find one another's files. The hook computes this derivation in JavaScript and cannot
    /// know an MVID, so anything it reads must be keyed by the solution alone.
    /// </remarks>
    public static string SolutionHash(string solutionKey)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(solutionKey.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant(); // 16 hex chars
    }

    public static string PipeName(string solutionKey) => $"roslyn-mcp-host-{Hash(solutionKey)}";

    /// <summary>The directory every daemon's lock directory lives under — what an enumeration
    /// of running daemons scans.</summary>
    public static string DaemonRoot => Path.Combine(Path.GetTempPath(), "roslyn-mcp-daemon");

    public static string LockDirectory(string solutionKey) =>
        Path.Combine(DaemonRoot, Hash(solutionKey));

    public static string LockFilePath(string solutionKey) =>
        Path.Combine(LockDirectory(solutionKey), ".lock");

    /// <summary>Global mutex name guarding the connect-or-spawn race for one solution.</summary>
    public static string SpawnMutexName(string solutionKey) => $@"Global\RoslynMcpDaemon_{Hash(solutionKey)}";
}
