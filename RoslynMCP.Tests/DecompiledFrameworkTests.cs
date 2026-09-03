using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Which framework a decompiled assembly is compiled against.
/// </summary>
/// <remarks>
/// Getting this wrong is not subtle but it is silent: the host runs on CoreCLR, so its
/// TRUSTED_PLATFORM_ASSEMBLIES were being added to every decompiled project, including .NET
/// Framework ones. Two core libraries in one compilation, and a decompiled <c>System.Web.UI.Page</c>
/// reports that <c>System.Web.Caching</c> does not exist in <c>System.Web</c> — an error about the
/// assembly it was decompiled from.
/// </remarks>
public class DecompiledFrameworkTests
{
    [Fact]
    public void ACoreAssemblyIsNotMistakenForFramework()
    {
        // This test assembly runs on CoreCLR and references System.Runtime.
        Assert.False(DecompiledSourceService.IsFrameworkAssembly(
            typeof(DecompiledFrameworkTests).Assembly.Location));
    }

    [RequiresFrameworkReferenceAssembliesFact]
    public void AFrameworkAssemblyIsRecognisedByWhatItReferences()
    {
        // System.Web only exists on the desktop runtime, and is exactly the case that was broken.
        Assert.True(DecompiledSourceService.IsFrameworkAssembly(
            Path.Combine(FrameworkReferenceDirectory()!, "System.Web.dll")));
    }

    [RequiresFrameworkReferenceAssembliesFact]
    public void MscorlibIsRecognisedByItsOwnNameRatherThanItsReferences()
    {
        // It references nothing, so the assembly-reference walk would never reach a verdict.
        Assert.True(DecompiledSourceService.IsFrameworkAssembly(
            Path.Combine(FrameworkReferenceDirectory()!, "mscorlib.dll")));
    }

    [Fact]
    public void SomethingThatIsNotAnAssemblyFallsBackRatherThanThrowing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"not-an-assembly-{Guid.NewGuid():N}.dll");
        File.WriteAllText(path, "definitely not a PE file");

        try
        {
            Assert.False(DecompiledSourceService.IsFrameworkAssembly(path));
        }
        finally
        {
            File.Delete(path);
        }
    }


    [Fact]
    public void ADecompiledPathIsRecognisedSoItDoesNotHijackTheSolution()
    {
        // Opening decompiled source used to empty the Solution Explorer: its ad-hoc workspace was
        // cached, became the most recently used one, and had no solution file to list.
        string decompiled = Path.Combine(
            Path.GetTempPath(), "RoslynMCP", "Decompiled", "mscorlib_ABC", "System_Decimal", "Decompiled.cs");

        Assert.True(DecompiledSourceService.IsDecompiledPath(decompiled));
        Assert.False(DecompiledSourceService.IsDecompiledPath(
            Path.Combine("D:", "Sources", "roslyn-sandbox", "Program.cs")));
        Assert.False(DecompiledSourceService.IsDecompiledPath(null));
    }

    internal static string? FrameworkReferenceDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Reference Assemblies", "Microsoft", "Framework", ".NETFramework");

        if (!Directory.Exists(root))
            return null;

        return Directory.EnumerateDirectories(root, "v4.*")
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "System.Web.dll")));
    }
}

/// <summary>Skips when the .NET Framework reference assemblies are not installed.</summary>
public sealed class RequiresFrameworkReferenceAssembliesFactAttribute : Xunit.FactAttribute
{
    public RequiresFrameworkReferenceAssembliesFactAttribute()
    {
        if (DecompiledFrameworkTests.FrameworkReferenceDirectory() is null)
            Skip = "The .NET Framework reference assemblies are not installed.";
    }
}
