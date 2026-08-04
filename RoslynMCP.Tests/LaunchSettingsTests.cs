using System.Text.Json;
using RoslynMCP.Services.Run;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers launchSettings.json: which profile a launch picks, which of its fields survive into the
/// run spec, and what writing one back leaves behind.
/// </summary>
public class LaunchSettingsTests : IDisposable
{
    private readonly string _projectDir =
        Path.Combine(Path.GetTempPath(), "roslyn-sense-launch-" + Guid.NewGuid().ToString("N"));

    public LaunchSettingsTests() => Directory.CreateDirectory(_projectDir);

    public void Dispose()
    {
        try { Directory.Delete(_projectDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void WriteSettings(string json)
    {
        var path = LaunchSettings.FilePath(_projectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void WhenProfilesAreReadThenEveryFieldALaunchNeedsSurvives()
    {
        WriteSettings("""
        {
          "iisSettings": { "iisExpress": { "applicationUrl": "http://localhost:41234/" } },
          "profiles": {
            "IIS Express": { "commandName": "IISExpress", "launchBrowser": true },
            "Api": {
              "commandName": "Project",
              "applicationUrl": "https://localhost:7001;http://localhost:5001",
              "commandLineArgs": "--seed \"two words\"",
              "launchBrowser": true,
              "launchUrl": "swagger",
              "hotReloadEnabled": false,
              "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
            },
            "Docs": { "commandName": "Executable", "executablePath": "dotnet", "commandLineArgs": "--info" }
          }
        }
        """);

        var settings = LaunchSettings.Load(_projectDir);

        Assert.NotNull(settings);
        Assert.Equal("http://localhost:41234/", settings.IisExpressApplicationUrl);
        Assert.Equal(3, settings.Profiles.Count);

        var api = settings.Profiles.Single(p => p.Name == "Api");
        Assert.Equal("https://localhost:7001;http://localhost:5001", api.ApplicationUrl);
        Assert.Equal("--seed \"two words\"", api.CommandLineArgs);
        Assert.True(api.LaunchBrowser);
        Assert.Equal("swagger", api.LaunchUrl);
        Assert.False(api.HotReloadEnabled);
        Assert.Equal("Development", api.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"]);
    }

    [Fact]
    public void WhenNoProfileIsNamedThenTheFirstProjectProfileWins()
    {
        WriteSettings("""
        {
          "profiles": {
            "IIS Express": { "commandName": "IISExpress" },
            "Api": { "commandName": "Project" },
            "Other": { "commandName": "Project" }
          }
        }
        """);

        var settings = LaunchSettings.Load(_projectDir)!;

        Assert.Equal("Api", settings.Select(null)?.Name);
        Assert.Equal("Other", settings.Select("other")?.Name); // named, case-insensitively
        Assert.Null(settings.Select("missing"));
    }

    [Fact]
    public void WhenCommandNameIsAbsentThenItIsAProjectProfile()
    {
        WriteSettings("""{ "profiles": { "Api": { "applicationUrl": "http://localhost:5123" } } }""");

        var profile = LaunchSettings.Load(_projectDir)!.Select(null);

        Assert.Equal("Project", profile?.CommandName);
        Assert.True(profile?.IsLaunchable);
    }

    [Fact]
    public void WhenTheFileIsMalformedThenLoadingYieldsNothingRatherThanThrowing()
    {
        WriteSettings("{ not json");

        Assert.Null(LaunchSettings.Load(_projectDir));
    }

    [Fact]
    public void WhenAnApplicationUrlIsWrittenThenOtherProfilesAndFieldsAreLeftAlone()
    {
        WriteSettings("""
        {
          "profiles": {
            "Api": {
              "commandName": "Project",
              "applicationUrl": "http://localhost:5000",
              "commandLineArgs": "--verbose",
              "somethingWeDoNotModel": true
            },
            "Worker": { "commandName": "Project" }
          }
        }
        """);

        var error = LaunchSettings.SetApplicationUrl(_projectDir, "api", "http://localhost:5321");

        Assert.Null(error);

        using var document = JsonDocument.Parse(File.ReadAllText(LaunchSettings.FilePath(_projectDir)));
        var api = document.RootElement.GetProperty("profiles").GetProperty("Api");
        Assert.Equal("http://localhost:5321", api.GetProperty("applicationUrl").GetString());
        Assert.Equal("--verbose", api.GetProperty("commandLineArgs").GetString());
        Assert.True(api.GetProperty("somethingWeDoNotModel").GetBoolean());
        Assert.True(document.RootElement.GetProperty("profiles").TryGetProperty("Worker", out _));
    }

    [Fact]
    public void WhenThereIsNoFileThenWritingCreatesOneWithARunnableProfile()
    {
        var error = LaunchSettings.SetApplicationUrl(_projectDir, "MySite", "http://localhost:5432");

        Assert.Null(error);

        var profile = LaunchSettings.Load(_projectDir)!.Select(null);
        Assert.Equal("MySite", profile?.Name);
        Assert.Equal("Project", profile?.CommandName);
        Assert.Equal("http://localhost:5432", profile?.ApplicationUrl);
    }

    [Fact]
    public void WhenTheUrlIsNotAbsoluteThenNothingIsWritten()
    {
        var error = LaunchSettings.SetApplicationUrl(_projectDir, "MySite", "5000");

        Assert.NotNull(error);
        Assert.False(File.Exists(LaunchSettings.FilePath(_projectDir)));
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("--verbose", new[] { "--verbose" })]
    [InlineData("  --a   --b  ", new[] { "--a", "--b" })]
    [InlineData("--path \"C:\\Program Files\\app\"", new[] { "--path", "C:\\Program Files\\app" })]
    [InlineData("--name \"two words\" --flag", new[] { "--name", "two words", "--flag" })]
    [InlineData("--json \"{\\\"a\\\":1}\"", new[] { "--json", "{\"a\":1}" })]
    [InlineData("--empty \"\"", new[] { "--empty", "" })]
    public void WhenCommandLineArgsAreSplitThenQuotingIsHonoured(string? commandLine, string[] expected) =>
        Assert.Equal(expected, RunConfigResolver.SplitArguments(commandLine));

    [Theory]
    [InlineData(new[] { "--urls", "http://localhost:5999" }, "http://localhost:5999")]
    [InlineData(new[] { "--urls=http://localhost:5998" }, "http://localhost:5998")]
    [InlineData(new[] { "--other", "x" }, null)]
    [InlineData(new[] { "--urls" }, null)]
    public void WhenArgumentsCarryUrlsThenTheyAreTheAddress(string[] arguments, string? expected) =>
        Assert.Equal(expected, RunConfigResolver.UrlsFromArguments(arguments));

    [Theory]
    [InlineData("http://localhost:5000", null, "http://localhost:5000")]
    [InlineData("http://localhost:5000/", "swagger", "http://localhost:5000/swagger")]
    [InlineData("http://localhost:5000", "/swagger", "http://localhost:5000/swagger")]
    [InlineData("https://localhost:7001;http://localhost:5001", "health", "https://localhost:7001/health")]
    [InlineData("http://localhost:5000", "https://example.test/x", "https://example.test/x")]
    [InlineData(null, "swagger", null)]
    public void WhenALaunchUrlIsCombinedThenItIsRelativeToTheFirstBinding(
        string? applicationUrl, string? launchUrl, string? expected) =>
        Assert.Equal(expected, RunConfigResolver.CombineLaunchUrl(applicationUrl, launchUrl));

    [Fact]
    public void WhenAPortIsDerivedFromTheProjectThenItIsStableAndInTheTemplateRange()
    {
        var port = RunConfigResolver.StablePort(@"C:\src\App\App.csproj");

        Assert.Equal(port, RunConfigResolver.StablePort(@"C:\src\App\App.csproj"));
        Assert.Equal(port, RunConfigResolver.StablePort(@"c:/src/app/App.csproj"));
        Assert.InRange(port, 5000, 5999);
        Assert.NotEqual(port, RunConfigResolver.StablePort(@"C:\src\Other\Other.csproj"));
    }
}
