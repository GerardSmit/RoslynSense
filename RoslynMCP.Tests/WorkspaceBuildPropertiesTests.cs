using System.Diagnostics;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class WorkspaceBuildPropertiesTests
{
    [Theory]
    [InlineData(false, ".sln")]
    [InlineData(true, ".slnx")]
    public void IndividualProjectLoadsReceiveTheOwningSolutionsProperties(bool isLegacy, string extension)
    {
        string solution = Path.Combine(Path.GetTempPath(), "Nested Solution", "Example" + extension);
        var properties = WorkspaceBuildProperties.Create(isLegacy, solution);

        Assert.Equal("true", properties["DesignTimeBuild"]);
        Assert.Equal(!isLegacy, properties.ContainsKey("AlwaysUseNETSdkDefaults"));
        Assert.Equal(Path.GetDirectoryName(solution) + Path.DirectorySeparatorChar, properties["SolutionDir"]);
        Assert.Equal(solution, properties["SolutionPath"]);
        Assert.Equal("Example", properties["SolutionName"]);
        Assert.Equal("Example" + extension, properties["SolutionFileName"]);
        Assert.Equal(extension, properties["SolutionExt"]);
        Assert.False(properties.ContainsKey("VisualStudioVersion"));
    }

    [Fact]
    public void StandaloneProjectDoesNotInventASolution()
    {
        var properties = WorkspaceBuildProperties.Create(isLegacy: false, solutionPath: null);
        Assert.DoesNotContain(properties.Keys, key => key.StartsWith("Solution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SdkProjectCanEvaluateItsLegacyWebProjectReference()
    {
        if (!OperatingSystem.IsWindows() || MsBuildLocator.VsEvaluationProperties.Count == 0)
            return; // The web targets are an optional Visual Studio component.

        string directory = Path.Combine(Path.GetTempPath(), $"mixed-web-reference-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string parent = Path.Combine(directory, "Consumer.csproj");
        string reference = Path.Combine(directory, "Web.csproj");
        string marker = Path.Combine(directory, "reference-evaluated.txt");

        try
        {
            await File.WriteAllTextAsync(parent, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net48</TargetFramework>
                    <EnableDefaultItems>false</EnableDefaultItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="Web.csproj" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(reference, """
                <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <PropertyGroup>
                    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
                    <OutputType>Library</OutputType>
                    <OutputPath>bin\</OutputPath>
                    <VisualStudioVersion Condition="'$(VisualStudioVersion)' == ''">10.0</VisualStudioVersion>
                    <VSToolsPath Condition="'$(VSToolsPath)' == ''">$(MSBuildExtensionsPath32)\Microsoft\VisualStudio\v$(VisualStudioVersion)</VSToolsPath>
                  </PropertyGroup>
                  <Import Project="$(MSBuildBinPath)\Microsoft.CSharp.targets" />
                  <Import Project="$(VSToolsPath)\WebApplications\Microsoft.WebApplication.targets" />
                  <Target Name="RecordReferenceEvaluation" BeforeTargets="GetTargetFrameworks">
                    <WriteLinesToFile File="$(MSBuildProjectDirectory)\reference-evaluated.txt"
                        Lines="$(VSToolsPath)" Overwrite="true" />
                  </Target>
                </Project>
                """);

            // Exercise the SDK's actual nested MSBuild request, where the pooled Roslyn
            // provider cannot intercept the reference. This target requires neither restore
            // nor compilation and does not need a .NET Framework targeting pack.
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("msbuild");
            startInfo.ArgumentList.Add(parent);
            startInfo.ArgumentList.Add("-target:PrepareProjectReferences");
            startInfo.ArgumentList.Add("-nologo");
            startInfo.ArgumentList.Add("-verbosity:minimal");
            startInfo.ArgumentList.Add("-nodeReuse:false");
            var properties = WorkspaceBuildProperties.Create(isLegacy: false, solutionPath: null);
            foreach (var (key, value) in properties)
                startInfo.ArgumentList.Add($"-property:{key}={value}");
            BuildProcessHelper.ConfigureMsBuildEnvironment(startInfo);

            using var process = new Process { StartInfo = startInfo };
            BuildProcessHelper.StartWithClosedInput(process);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                throw;
            }
            string output = await stdout + await stderr;

            Assert.True(process.ExitCode == 0, output);
            Assert.True(File.Exists(marker), output);
            Assert.Equal(properties["VSToolsPath"], (await File.ReadAllTextAsync(marker)).Trim());
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
