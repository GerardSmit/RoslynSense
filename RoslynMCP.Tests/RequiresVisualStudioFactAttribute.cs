using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Skips the test when Visual Studio or Build Tools MSBuild is not available.
/// Used for tests that require legacy .csproj (non-SDK-style) support.
/// </summary>
public sealed class RequiresVisualStudioFactAttribute : FactAttribute
{
    public RequiresVisualStudioFactAttribute()
    {
        if (!TestEnvironment.HasVisualStudioMSBuild)
            Skip = "Visual Studio or Build Tools MSBuild is not available on this machine.";
    }
}
