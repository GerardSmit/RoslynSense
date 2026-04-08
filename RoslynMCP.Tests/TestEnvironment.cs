using RoslynMCP.Services;

namespace RoslynMCP.Tests;

/// <summary>
/// Provides information about the test execution environment.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>
    /// Returns <c>true</c> when Visual Studio or Build Tools MSBuild was registered
    /// by <see cref="WorkspaceService"/>, enabling legacy .csproj support.
    /// </summary>
    public static bool HasVisualStudioMSBuild => WorkspaceService.IsLegacyProjectSupported;
}
