using RoslynMCP.Languages.Routes;
using RoslynMCP.Languages.Routes.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Which projects the Routes section looks at, decided from files on disk and nothing else.
/// </summary>
/// <remarks>
/// The interesting case is the last one. A solution that wrote its own routing layer references no
/// web framework anywhere — that is what made the layer in-house — so a probe reading only project
/// files finds nothing to look at and the section is empty on exactly the solution the pack was
/// written for.
/// </remarks>
public class RouteProjectProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "roslyn-sense-tests", $"route-probe-{Guid.NewGuid():N}");

    [Fact]
    public void AProjectReferencingTheFrameworkServes() =>
        Assert.True(Serves("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.AspNetCore.Mvc.Core" Version="2.2.5" />
              </ItemGroup>
            </Project>
            """));

    [Fact]
    public void AProjectWithNoWebAnythingDoesNot() =>
        Assert.False(Serves("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """));

    /// <summary>
    /// The in-house case, and the reason the probe reads source at all: nothing in the project file
    /// says HTTP, and a controller in it says <c>[Route]</c> and <c>[HttpGet]</c> anyway, because a
    /// routing layer somebody wrote themselves copies the names it is replacing.
    /// </summary>
    [Fact]
    public void AControllerIsEvidenceEvenWhenTheProjectFileSaysNothing()
    {
        string plain = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net472</TargetFramework></PropertyGroup>
            </Project>
            """;

        Assert.False(Serves(plain));

        Source("Controller/AnalyticsController.cs", """
            [Route("api/_internal/analytics")]
            public class AnalyticsController : ControllerBase
            {
                [HttpGet("")]
                public object? GetResults() => null;
            }
            """);

        Assert.True(Serves(plain));
    }

    /// <summary>
    /// A word is not a marker. Half this solution mentions routes without declaring one, and a
    /// probe that matched the word would look at every project in it.
    /// </summary>
    [Fact]
    public void MerelySayingTheWordIsNotEvidence()
    {
        Source("Middleware.cs", """
            public class Middleware
            {
                // Nothing here declares a route: it reads the RouteData the layer above put there.
                public string? Route(RouteData routeData) => routeData.Route;
            }
            """);

        Assert.False(Serves("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net472</TargetFramework></PropertyGroup>
            </Project>
            """));
    }

    /// <summary>Compiler output is not somebody's source, however much of it says <c>[Route(</c>.</summary>
    [Fact]
    public void GeneratedOutputIsNotEvidence()
    {
        Source("obj/Debug/Generated.cs", """
            [Route("api/generated")]
            public class GeneratedController { }
            """);

        Assert.False(Serves("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net472</TargetFramework></PropertyGroup>
            </Project>
            """));
    }

    /// <summary>
    /// A minimal API is a call rather than an attribute, and the marker for one carries the dot and
    /// the parenthesis so that a method named <c>MapGet</c> being declared is not a project serving.
    /// </summary>
    [Fact]
    public void ARegistrationCallIsEvidenceToo()
    {
        Source("Program.cs", """
            var app = Build();
            app.MapGet("/health", () => "ok");
            """);

        Assert.True(Serves("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- The harness ---------------------------------------------------------------------------

    /// <summary>Writes the project file and asks, against a fresh directory each time.</summary>
    /// <remarks>
    /// The project file is rewritten on every call so that its timestamp moves, which is what the
    /// manifest half of the cache is keyed on. The source half keys on the files themselves, so
    /// adding one between two calls is seen without anything having to be reset.
    /// </remarks>
    private bool Serves(string project)
    {
        Directory.CreateDirectory(_root);

        string path = Path.Combine(_root, "Application.csproj");
        File.WriteAllText(path, project);

        return RouteProjectProbe.Serves(path, RoutesSettings.Default.SourceMarkers);
    }

    private void Source(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
