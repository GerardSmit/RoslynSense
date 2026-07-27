using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Skips the test when IIS Express is not installed. It ships with Visual Studio, so it is absent
/// on a bare CI image and on any non-Windows host.
/// </summary>
public sealed class RequiresIisExpressFactAttribute : FactAttribute
{
    public RequiresIisExpressFactAttribute()
    {
        if (NetFxToolchain.Info.PreferredIisExpress is null)
            Skip = "IIS Express is not installed on this machine.";
    }
}
