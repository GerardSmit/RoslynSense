using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Skips unless <c>ROSLYNMCP_TESTS_NETWORK=1</c>. The suite has to pass on a machine with no
/// route to the internet, so anything that reaches a symbol server or GitHub is opt-in.
/// </summary>
public class RequiresNetworkFactAttribute : FactAttribute
{
    public RequiresNetworkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ROSLYNMCP_TESTS_NETWORK") is not ("1" or "true"))
            Skip = "Set ROSLYNMCP_TESTS_NETWORK=1 to run tests that download symbols or source.";
    }
}
