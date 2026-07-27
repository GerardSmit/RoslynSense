using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers PE-header architecture detection, which decides whether a debug target can be attached
/// in-process or needs a bitness-matched worker — ICorDebug cannot attach across x86/x64.
/// </summary>
public class ProcessArchTests
{
    [Fact]
    public void WhenHostArchQueriedThenItMatchesTheCurrentProcess() =>
        Assert.Equal(
            Environment.Is64BitProcess ? DebugArch.X64 : DebugArch.X86,
            ProcessArch.Host);

    [Fact]
    public void WhenAnyCpuAssemblyThenItMapsToTheHostArch()
    {
        // The test assembly itself is AnyCPU, which runs at whatever bitness the OS gives it.
        var assembly = typeof(ProcessArchTests).Assembly.Location;

        Assert.Equal(ProcessArch.Host, ProcessArch.OfExecutable(assembly));
    }

    [Fact]
    public void WhenNativeX64ImageThenX64IsDetected()
    {
        // The desktop CLR ships per-bitness, so Framework64 is unambiguously an x64 image.
        var clr = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework64", "v4.0.30319", "clr.dll");

        if (!File.Exists(clr))
            return; // .NET Framework not installed on this machine.

        Assert.Equal(DebugArch.X64, ProcessArch.OfExecutable(clr));
    }

    [Fact]
    public void WhenNativeX86ImageThenX86IsDetected()
    {
        var clr = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework", "v4.0.30319", "clr.dll");

        if (!File.Exists(clr))
            return;

        Assert.Equal(DebugArch.X86, ProcessArch.OfExecutable(clr));
    }

    [Fact]
    public void WhenFileCannotBeReadThenItFallsBackToTheHostArch()
    {
        // Detection must degrade rather than throw: it runs on the attach path.
        Assert.Equal(
            ProcessArch.Host,
            ProcessArch.OfExecutable(Path.Combine(Path.GetTempPath(), "definitely-not-here.exe")));
    }

    [Fact]
    public void WhenFileIsNotAPeImageThenItFallsBackToTheHostArch()
    {
        var textFile = Path.Combine(Path.GetTempPath(), $"roslynsense-notpe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(textFile, "this is not a PE image");

        try
        {
            Assert.Equal(ProcessArch.Host, ProcessArch.OfExecutable(textFile));
        }
        finally
        {
            try { File.Delete(textFile); } catch { }
        }
    }
}
