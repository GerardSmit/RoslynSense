using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Skips the test unless a 32-bit .NET Framework target can be built and the x86 debug worker has
/// been published (build with <c>-p:BuildDebugWorkers=true</c>, which Release does by default).
/// </summary>
public sealed class RequiresX86WorkerFactAttribute : FactAttribute
{
    public RequiresX86WorkerFactAttribute()
    {
        if (!X86Target.IsAvailable)
            Skip = "The 32-bit target or the x86 debug worker is unavailable on this machine.";
    }
}
