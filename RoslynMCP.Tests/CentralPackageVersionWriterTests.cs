using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Editing Directory.Packages.props in place.
/// </summary>
/// <remarks>
/// The assertion that matters is that everything except the one attribute survives byte for byte:
/// a fifty-package update must not reformat, reorder or strip the comments out of a file the team
/// maintains by hand.
/// </remarks>
public class CentralPackageVersionWriterTests : IDisposable
{
    private readonly string _directory;

    public CentralPackageVersionWriterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"cpm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void RewritesTheVersionAndLeavesTheRestOfTheFileAlone()
    {
        const string original = """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>

              <ItemGroup>
                <!-- Kept deliberately: comments are load-bearing in a hand-maintained file. -->
                <PackageVersion Include="Newtonsoft.Json" Version="13.0.1" />
                <PackageVersion Include="Serilog" Version="3.1.1" />
              </ItemGroup>
            </Project>
            """;

        string path = Write(original);

        Assert.True(CentralPackageVersionWriter.TrySetVersion(path, "Newtonsoft.Json", "13.0.3"));

        string updated = File.ReadAllText(path);
        Assert.Contains("""<PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />""", updated);
        Assert.Contains("Kept deliberately", updated);
        Assert.Equal(
            original.Replace("13.0.1", "13.0.3"),
            updated.Replace("\r\n", "\n").TrimEnd());
    }

    [Theory]
    [InlineData("""<PackageVersion Version="1.0.0" Include="Contoso.Widgets" />""")]
    [InlineData("""<PackageVersion Update="Contoso.Widgets" Version="1.0.0" />""")]
    [InlineData("""<GlobalPackageReference Include="Contoso.Widgets" Version="1.0.0" />""")]
    public void HandlesAttributeOrderAndEveryDeclarationForm(string declaration)
    {
        // Attribute order is not fixed and both Include= and Update= are legal, which is why this
        // parses the XML rather than pattern-matching the text.
        string path = Write($"""
            <Project>
              <ItemGroup>
                {declaration}
              </ItemGroup>
            </Project>
            """);

        Assert.True(CentralPackageVersionWriter.TrySetVersion(path, "Contoso.Widgets", "2.0.0"));
        Assert.Contains("""Version="2.0.0" """.TrimEnd(), File.ReadAllText(path));
    }

    [Fact]
    public void ReturnsFalseWhenThePackageIsNotDeclared()
    {
        string path = Write("""
            <Project>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="3.1.1" />
              </ItemGroup>
            </Project>
            """);

        // The caller falls through to `dotnet add package`, which knows how to add a new entry.
        Assert.False(CentralPackageVersionWriter.TrySetVersion(path, "Newtonsoft.Json", "13.0.3"));
    }

    [Fact]
    public void MissingFileIsNotAnError() =>
        Assert.False(CentralPackageVersionWriter.TrySetVersion(
            Path.Combine(_directory, "nope.props"), "Anything", "1.0.0"));

    [Fact]
    public void FindNearestWalksUpFromTheProject()
    {
        string props = Write("<Project />");
        string projectDirectory = Path.Combine(_directory, "src", "App");
        Directory.CreateDirectory(projectDirectory);
        string project = Path.Combine(projectDirectory, "App.csproj");
        File.WriteAllText(project, "<Project />");

        Assert.Equal(props, CentralPackageVersionWriter.FindNearest(project));
    }

    private string Write(string content)
    {
        string path = Path.Combine(_directory, "Directory.Packages.props");
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
