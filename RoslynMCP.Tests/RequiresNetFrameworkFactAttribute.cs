using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Skips the test when a .NET Framework debug target cannot be produced — off Windows, or where
/// the framework's csc.exe is absent.
/// </summary>
public sealed class RequiresNetFrameworkFactAttribute : FactAttribute
{
    public RequiresNetFrameworkFactAttribute()
    {
        if (!FxTargetProcess.IsAvailable)
            Skip = "A .NET Framework target could not be compiled on this machine.";
    }
}
