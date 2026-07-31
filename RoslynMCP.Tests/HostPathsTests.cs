using RoslynMCP.Daemon;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// How a daemon is addressed.
/// </summary>
/// <remarks>
/// The name was derived from the solution alone, so whichever build reached a solution first owned
/// it and every later client silently got that one. A development build would connect to an
/// installed release and be answered with the release's capabilities — surfacing as "no method by
/// the name ..." for anything new, or, worse, as old behaviour that reads as the new code simply
/// not working. Hours went into the second kind before this was found.
/// </remarks>
public class HostPathsTests
{
    private const string Solution = @"D:\Sources\roslyn-sandbox\Sandbox.sln";

    [Fact]
    public void TheSameSolutionFromTheSameBuildIsTheSameDaemon()
    {
        Assert.Equal(HostPaths.PipeName(Solution), HostPaths.PipeName(Solution));
    }

    [Fact]
    public void DifferentSolutionsAreDifferentDaemons()
    {
        Assert.NotEqual(
            HostPaths.PipeName(Solution),
            HostPaths.PipeName(@"D:\Sources\other\Other.sln"));
    }

    [Fact]
    public void ThePathIsMatchedWithoutRegardForCase()
    {
        // Windows hands the same solution back with whatever casing the caller used; two daemons
        // for one solution would each hold their own copy of the workspace.
        Assert.Equal(
            HostPaths.PipeName(Solution),
            HostPaths.PipeName(Solution.ToUpperInvariant()));
    }

    [Fact]
    public void TheBuildIsPartOfTheName()
    {
        // The regression this file exists for. The key must not be a function of the solution
        // alone, or two builds share a daemon and the newer one inherits the older one's answers.
        string expectedIfSolutionOnly = ExpectedHashOf(Solution.ToLowerInvariant());

        Assert.DoesNotContain(expectedIfSolutionOnly, HostPaths.PipeName(Solution));
    }

    [Fact]
    public void EveryNameForOneSolutionAgreesWithTheOthers()
    {
        // Pipe, lock and spawn mutex must all address the same daemon, or a client can hold the
        // lock for one and talk to another.
        string hash = HostPaths.Hash(Solution);

        Assert.Contains(hash, HostPaths.PipeName(Solution));
        Assert.Contains(hash, HostPaths.LockFilePath(Solution));
        Assert.Contains(hash, HostPaths.SpawnMutexName(Solution));
    }

    private static string ExpectedHashOf(string value)
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }
}
