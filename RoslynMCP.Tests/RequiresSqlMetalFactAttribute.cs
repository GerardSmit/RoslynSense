using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Skips the test when SqlMetal.exe is not available. SqlMetal ships with the Windows SDK, so this
/// is Windows-only and absent on a bare CI image.
/// </summary>
public sealed class RequiresSqlMetalFactAttribute : FactAttribute
{
    public RequiresSqlMetalFactAttribute()
    {
        if (NetFxToolchain.Info.SqlMetal.Length == 0)
            Skip = "SqlMetal.exe is not available on this machine (install the Windows SDK).";
    }
}
