using System.Reflection;
using RoslynMCP.Services.MetadataConfiguration;
using Xunit;

namespace RoslynMCP.Tests;

public sealed class FrameworkImplementationAssemblyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "roslynsense-framework-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8/System.Web.dll", true)]
    [InlineData("C:\\packages\\microsoft.netframework.referenceassemblies.net48\\build\\.NETFramework\\v4.8\\System.Web.dll", true)]
    [InlineData("C:/packs/Microsoft.NETCore.App.Ref/10.0.0/ref/net10.0/System.Web.dll", false)]
    [InlineData("C:/site/bin/System.Web.dll", false)]
    public void OnlyFrameworkTargetingPackPathsAreEligible(string path, bool expected) =>
        Assert.Equal(expected, FrameworkImplementationAssembly.IsFrameworkReference(path));

    [Theory]
    [InlineData("Other", "4.0.0.0", "neutral", "b77a5c561934e089")]
    [InlineData("System.Web", "2.0.0.0", "neutral", "b77a5c561934e089")]
    [InlineData("System.Web", "4.0.0.0", "nl", "b77a5c561934e089")]
    [InlineData("System.Web", "4.0.0.0", "neutral", "31bf3856ad364e35")]
    [InlineData("System.Web", "4.0.0.0", "neutral", "null")]
    public void EveryAssemblyIdentityComponentMustMatch(string name, string version, string culture, string token)
    {
        var reference = new AssemblyName("System.Web, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
        var candidate = new AssemblyName($"{name}, Version={version}, Culture={culture}, PublicKeyToken={token}");
        Assert.False(FrameworkImplementationAssembly.SameIdentity(reference, candidate));
        Assert.True(FrameworkImplementationAssembly.SameIdentity(reference, new AssemblyName(reference.FullName)));
    }

    [Fact]
    public void UsesMatchingImplementationAndFallsBackWhenUnavailable()
    {
        string reference = CopyAssembly(typeof(object).Assembly, ".NETFramework/v4.8/System.Private.CoreLib.dll");
        Assert.Equal(reference, FrameworkImplementationAssembly.Resolve(reference, _directory));

        string implementation = CopyAssembly(typeof(object).Assembly, "Microsoft.NET/Framework/v4.0.30319/System.Private.CoreLib.dll");
        Assert.Equal(implementation, FrameworkImplementationAssembly.Resolve(reference, _directory));
    }

    [Fact]
    public void IgnoresWrongIdentityAndFindsMatchingGacEntry()
    {
        string reference = CopyAssembly(typeof(object).Assembly, ".NETFramework/v4.8/System.Private.CoreLib.dll");
        CopyAssembly(typeof(FrameworkImplementationAssemblyTests).Assembly, "Microsoft.NET/Framework/v4.0.30319/System.Private.CoreLib.dll");
        string implementation = CopyAssembly(typeof(object).Assembly, "Microsoft.NET/assembly/GAC_MSIL/System.Private.CoreLib/v4.0_test/System.Private.CoreLib.dll");
        Assert.Equal(implementation, FrameworkImplementationAssembly.Resolve(reference, _directory));
    }

    [Fact]
    public void DoesNotRedirectOrdinaryProjectReferences()
    {
        string reference = CopyAssembly(typeof(object).Assembly, "bin/System.Private.CoreLib.dll");
        CopyAssembly(typeof(object).Assembly, "Microsoft.NET/Framework/v4.0.30319/System.Private.CoreLib.dll");
        Assert.Equal(reference, FrameworkImplementationAssembly.Resolve(reference, _directory));
    }

    private string CopyAssembly(Assembly assembly, string relativePath)
    {
        string path = Path.Combine(_directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(assembly.Location, path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
