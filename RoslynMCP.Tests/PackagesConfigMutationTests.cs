using System.Xml.Linq;
using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public sealed class PackagesConfigMutationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"package-mutation-{Guid.NewGuid():N}");
    private string ProjectPath => Path.Combine(_directory, "Legacy.csproj");
    private string ConfigPath => Path.Combine(_directory, "packages.config");

    public PackagesConfigMutationTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("/")]
    [InlineData("\\")]
    public async Task UninstallOnlyRemovesReferencesInsideTheExactPackageDirectory(string separator)
    {
        var hints = new Dictionary<string, string>
        {
            ["Target"] = "packages/Contoso.Core.1.0.0/lib/net48/Target.dll",
            ["TargetExtra"] = "packages/contoso.core.1.0.0/lib/net48/Extra.dll",
            ["LongerVersion"] = "packages/Contoso.Core.1.0.0.1/lib/net48/Other.dll",
            ["PrefixedPackage"] = "packages/Prefix.Contoso.Core.1.0.0/lib/net48/Other.dll",
            ["SuffixedPackage"] = "packages/Contoso.Core.1.0.0.Extensions/lib/net48/Other.dll",
            ["AssemblyNameOnly"] = "lib/Contoso.Core.1.0.0.dll",
        };
        string project = new XElement("Project",
            new XComment("Keep unrelated references and package files"),
            new XElement("ItemGroup", hints.Select(pair => new XElement("Reference",
                new XAttribute("Include", pair.Key),
                new XElement("HintPath", pair.Value.Replace("/", separator)))))).ToString();
        await File.WriteAllTextAsync(ProjectPath, project);
        await File.WriteAllTextAsync(ConfigPath,
            "<packages><package id=\"Contoso.Core\" version=\"1.0.0\" targetFramework=\"net48\" />" +
            "<package id=\"Prefix.Contoso.Core\" version=\"1.0.0\" targetFramework=\"net48\" /></packages>");
        string installedAssembly = Path.Combine(_directory, "packages", "Contoso.Core.1.0.0", "lib", "net48", "Target.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(installedAssembly)!);
        await File.WriteAllTextAsync(installedAssembly, "shared package payload");
        await using var scope = new PackageMutationScope();

        var result = await PackagesConfigService.UninstallAsync(ProjectPath, "CONTOSO.CORE", default, scope);

        Assert.True(result.Success, result.Message);
        var remaining = XDocument.Load(ProjectPath).Descendants("Reference")
            .Select(element => (string)element.Attribute("Include")!).Order().ToArray();
        Assert.Equal(hints.Keys.Except(["Target", "TargetExtra"]).Order(), remaining);
        Assert.Equal("Prefix.Contoso.Core", Assert.Single(PackagesConfigService.Read(ProjectPath)).Id);
        Assert.Contains("Keep unrelated references", await File.ReadAllTextAsync(ProjectPath));
        Assert.Equal("shared package payload", await File.ReadAllTextAsync(installedAssembly));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrecancelledPackageMutationPreservesProjectAndConfig(bool install)
    {
        const string project = "<Project><PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion>" +
            "</PropertyGroup><ItemGroup><Reference Include=\"Target\"><HintPath>" +
            "packages/Contoso.Core.1.0.0/lib/net48/Target.dll</HintPath></Reference></ItemGroup></Project>";
        const string config = "<packages><package id=\"Contoso.Core\" version=\"1.0.0\" targetFramework=\"net48\" /></packages>";
        await File.WriteAllTextAsync(ProjectPath, project);
        await File.WriteAllTextAsync(ConfigPath, config);
        // A cached payload makes installation fully local and exposes cancellation before edits.
        string assembly = Path.Combine(PackagesConfigService.PackagesRootFor(ProjectPath),
            "Contoso.Core.2.0.0", "lib", "net48", "Target.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
        await File.WriteAllTextAsync(assembly, "cached payload");
        var canceled = new CancellationToken(canceled: true);
        await using var scope = new PackageMutationScope();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install
            ? PackagesConfigService.InstallAsync(ProjectPath, "Contoso.Core", "2.0.0", canceled, scope)
            : PackagesConfigService.UninstallAsync(ProjectPath, "Contoso.Core", canceled, scope));

        Assert.Equal(project, await File.ReadAllTextAsync(ProjectPath));
        Assert.Equal(config, await File.ReadAllTextAsync(ConfigPath));
        Assert.Equal("cached payload", await File.ReadAllTextAsync(assembly));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
