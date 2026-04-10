using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class ProjectStructureToolTests
{
    [Fact]
    public async Task WhenEmptyPathThenReturnsError()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure("", new MarkdownFormatter());
        Assert.StartsWith("Error: Path cannot be empty", result);
    }

    [Fact]
    public async Task WhenFileNotFoundThenReturnsError()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            @"C:\nonexistent\project.csproj", new MarkdownFormatter());
        Assert.Contains("does not exist", result);
    }

    [Fact]
    public async Task WhenCsprojProvidedThenShowsProjectStructure()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.SampleProjectFile, new MarkdownFormatter());

        Assert.Contains("# Project: SampleProject", result);
        Assert.Contains("Framework", result);
        Assert.Contains("Types", result);
    }

    [Fact]
    public async Task WhenSourceFileProvidedThenFindsProjectAndShowsStructure()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.CalculatorFile, new MarkdownFormatter());

        Assert.Contains("# Project: SampleProject", result);
        Assert.Contains("Calculator", result);
    }

    [Fact]
    public async Task WhenProjectHasTypesThenShowsNamespaceTree()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.SampleProjectFile, new MarkdownFormatter());

        Assert.Contains("Types", result);
        Assert.Contains("Calculator", result);
        Assert.Contains("StatisticsCalculator", result);
        Assert.Contains("IStringFormatter", result);
        // Should use kind badges
        Assert.Contains("[C]", result); // Class
        Assert.Contains("[I]", result); // Interface
    }

    [Fact]
    public async Task WhenProjectHasReferenceThenShowsAssemblyReferences()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.SampleProjectFile, new MarkdownFormatter());

        Assert.Contains("Assembly References", result);
    }

    [Fact]
    public async Task WhenProjectUsesNet10ThenInfersFramework()
    {
        // SampleProject targets net10.0
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.SampleProjectFile, new MarkdownFormatter());

        Assert.Contains("net10.0", result);
    }

    [Fact]
    public void InferTargetFramework_WithNet10Symbols_ReturnsNet10()
    {
        // Test the internal InferTargetFramework method
        var result = RoslynMCP.Tools.ProjectStructureTool.InferTargetFramework(
            CreateProjectWithSymbols("NET10_0", "NET9_0_OR_GREATER", "NET8_0_OR_GREATER"));
        Assert.Equal("net10.0", result);
    }

    [Fact]
    public void InferTargetFramework_WithNetStandard20_ReturnsNetstandard20()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.InferTargetFramework(
            CreateProjectWithSymbols("NETSTANDARD2_0", "NETSTANDARD1_0_OR_GREATER"));
        Assert.Equal("netstandard2.0", result);
    }

    [Fact]
    public void InferTargetFramework_WithNoSymbols_ReturnsNull()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.InferTargetFramework(
            CreateProjectWithSymbols());
        Assert.Null(result);
    }

    private static Microsoft.CodeAnalysis.Project CreateProjectWithSymbols(params string[] symbols)
    {
        var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var parseOptions = new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions()
            .WithPreprocessorSymbols(symbols);
        var projectId = Microsoft.CodeAnalysis.ProjectId.CreateNewId();
        var projectInfo = Microsoft.CodeAnalysis.ProjectInfo.Create(
            projectId, Microsoft.CodeAnalysis.VersionStamp.Default, "TestProject", "TestProject",
            Microsoft.CodeAnalysis.LanguageNames.CSharp, parseOptions: parseOptions);
        workspace.AddProject(projectInfo);
        return workspace.CurrentSolution.GetProject(projectId)!;
    }

    #region FormatOutputKind tests

    [Theory]
    [InlineData(Microsoft.CodeAnalysis.OutputKind.ConsoleApplication, "Console Application")]
    [InlineData(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, "Library (DLL)")]
    [InlineData(Microsoft.CodeAnalysis.OutputKind.WindowsApplication, "Windows Application")]
    [InlineData(Microsoft.CodeAnalysis.OutputKind.NetModule, "Module")]
    public void FormatOutputKind_ReturnsHumanReadableString(
        Microsoft.CodeAnalysis.OutputKind outputKind, string expected)
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.FormatOutputKind(outputKind);
        Assert.Equal(expected, result);
    }

    #endregion

    #region Nullable detection tests

    [Fact]
    public async Task WhenProjectHasNullableEnabledThenShowsNullableEnable()
    {
        // SampleProject has <Nullable>enable</Nullable>
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.SampleProjectFile, new MarkdownFormatter());

        Assert.Contains("**Nullable**: Enable", result);
    }

    #endregion

    #region Output kind tests

    [Fact]
    public async Task WhenProjectIsLibraryThenShowsLibraryDLL()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.SampleProjectFile, new MarkdownFormatter());

        Assert.Contains("Library (DLL)", result);
    }

    #endregion

    #region C# version tests

    [Fact]
    public async Task WhenProjectUsesNet10ThenShowsCSharpVersion()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.SampleProjectFile, new MarkdownFormatter());

        Assert.Contains("**C# Version**", result);
    }

    #endregion

    #region Test framework detection tests

    [Fact]
    public async Task WhenTestProjectThenShowsTestFramework()
    {
        // DebugTestProject references xunit
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.DebugTestProjectFile, new MarkdownFormatter());

        Assert.Contains("**Test Framework**: xUnit", result);
    }

    [Fact]
    public async Task WhenNonTestProjectThenNoTestFrameworkShown()
    {
        // SampleProject is not a test project
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.SampleProjectFile, new MarkdownFormatter());

        Assert.DoesNotContain("Test Framework", result);
    }

    #endregion

    #region InferTargetFramework legacy tests

    [Fact]
    public void InferTargetFramework_WithNet48Symbols_ReturnsNet48()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.InferTargetFramework(
            CreateProjectWithSymbols("NET48"));
        Assert.Equal("net48", result);
    }

    [Fact]
    public void InferTargetFramework_WithNet472Symbols_ReturnsNet472()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.InferTargetFramework(
            CreateProjectWithSymbols("NET472"));
        Assert.Equal("net472", result);
    }

    [Fact]
    public void InferTargetFramework_WithNet462Symbols_ReturnsNet462()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.InferTargetFramework(
            CreateProjectWithSymbols("NET462"));
        Assert.Equal("net462", result);
    }

    [Fact]
    public void InferTargetFramework_WithNetCoreApp31_ReturnsNetcoreapp31()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.InferTargetFramework(
            CreateProjectWithSymbols("NETCOREAPP3_1"));
        Assert.Equal("netcoreapp3.1", result);
    }

    [Fact]
    public void InferTargetFramework_WithNet8Symbols_ReturnsNet80()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.InferTargetFramework(
            CreateProjectWithSymbols("NET8_0", "NET7_0_OR_GREATER"));
        Assert.Equal("net8.0", result);
    }

    [Fact]
    public void InferTargetFramework_WithNet9Symbols_ReturnsNet90()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.InferTargetFramework(
            CreateProjectWithSymbols("NET9_0"));
        Assert.Equal("net9.0", result);
    }

    [Fact]
    public void InferTargetFramework_WithGenericNetFrameworkSymbol_ReturnsNetFramework()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.InferTargetFramework(
            CreateProjectWithSymbols("NETFRAMEWORK"));
        Assert.Equal(".NET Framework", result);
    }

    [Fact]
    public void InferTargetFramework_PicksMostSpecificVersion()
    {
        // When both NET10_0 and NET9_0 are present, should pick NET10_0 (newest first)
        var result = RoslynMCP.Tools.ProjectStructureTool.InferTargetFramework(
            CreateProjectWithSymbols("NET10_0", "NET9_0"));
        Assert.Equal("net10.0", result);
    }

    #endregion

    #region App type detection tests

    [Fact]
    public async Task WhenSampleProjectThenNoAppTypeShown()
    {
        // SampleProject is a plain library with no framework references
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.SampleProjectFile, new MarkdownFormatter());

        Assert.DoesNotContain("App Type", result);
    }

    [Fact]
    public async Task WhenBlazorProjectThenShowsBlazor()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.BlazorProjectFile, new MarkdownFormatter());

        Assert.Contains("**App Type**:", result);
        Assert.Contains("Blazor", result);
    }

    [Fact]
    public async Task WhenAspxProjectThenShowsWebForms()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.AspxProjectFile, new MarkdownFormatter());

        Assert.Contains("**App Type**:", result);
        Assert.Contains("ASP.NET (WebForms)", result);
    }

    [Fact]
    public void ReadProjectSdk_ForSdkStyleProject_ReturnsSdk()
    {
        // SampleProject uses Microsoft.NET.Sdk
        var types = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            CreateProjectWithSymbols(), FixturePaths.SampleProjectFile);
        // Plain SDK project with no special references → empty list
        Assert.Empty(types);
    }

    #endregion

    #region DetectAppType unit tests

    private static HashSet<string> Refs(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    // --- Desktop frameworks ---

    [Fact]
    public void DetectAppType_Avalonia()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(Refs("Avalonia"), "Microsoft.NET.Sdk");
        Assert.Contains("Avalonia", result);
    }

    [Fact]
    public void DetectAppType_AvaloniaBase()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(Refs("Avalonia.Base"), "Microsoft.NET.Sdk");
        Assert.Contains("Avalonia", result);
    }

    [Fact]
    public void DetectAppType_Maui()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(Refs("Microsoft.Maui.Controls"), "Microsoft.NET.Sdk");
        Assert.Contains(".NET MAUI", result);
    }

    [Fact]
    public void DetectAppType_WinUI()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(Refs("Microsoft.UI.Xaml"), "Microsoft.NET.Sdk");
        Assert.Contains("WinUI", result);
    }

    [Fact]
    public void DetectAppType_WPF()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(Refs("PresentationFramework"), "Microsoft.NET.Sdk");
        Assert.Contains("WPF", result);
    }

    [Fact]
    public void DetectAppType_WPF_PresentationCore()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(Refs("PresentationCore"), "Microsoft.NET.Sdk");
        Assert.Contains("WPF", result);
    }

    [Fact]
    public void DetectAppType_WinForms()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(Refs("System.Windows.Forms"), "Microsoft.NET.Sdk");
        Assert.Contains("WinForms", result);
    }

    [Fact]
    public void DetectAppType_UnoPlatform()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(Refs("Uno.UI"), "Microsoft.NET.Sdk");
        Assert.Contains("Uno Platform", result);
    }

    // --- Web frameworks ---

    [Fact]
    public void DetectAppType_BlazorWasm_ByAssembly()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Microsoft.AspNetCore.Components.WebAssembly"), "Microsoft.NET.Sdk.Web");
        Assert.Contains("Blazor WebAssembly", result);
        Assert.DoesNotContain("Blazor Server", result);
    }

    [Fact]
    public void DetectAppType_BlazorWasm_BySdk()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs(), "Microsoft.NET.Sdk.BlazorWebAssembly");
        Assert.Contains("Blazor WebAssembly", result);
    }

    [Fact]
    public void DetectAppType_BlazorServer()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Microsoft.AspNetCore.Components.Server"), "Microsoft.NET.Sdk.Web");
        Assert.Contains("Blazor Server", result);
        Assert.DoesNotContain("Blazor WebAssembly", result);
    }

    [Fact]
    public void DetectAppType_Blazor_Generic()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Microsoft.AspNetCore.Components"), "Microsoft.NET.Sdk.Web");
        Assert.Contains("Blazor", result);
        Assert.DoesNotContain("Blazor WebAssembly", result);
        Assert.DoesNotContain("Blazor Server", result);
    }

    [Fact]
    public void DetectAppType_AspNetCoreMvc()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Microsoft.AspNetCore.Mvc"), "Microsoft.NET.Sdk.Web");
        Assert.Contains("ASP.NET Core MVC", result);
    }

    [Fact]
    public void DetectAppType_AspNetCoreMvc_NotShownWhenBlazor()
    {
        // When Blazor is detected, MVC should not be added separately
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Mvc"), "Microsoft.NET.Sdk.Web");
        Assert.Contains("Blazor", result);
        Assert.DoesNotContain("ASP.NET Core MVC", result);
    }

    [Fact]
    public void DetectAppType_AspNetCore_WebSdk_WithAspNetCore()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Microsoft.AspNetCore"), "Microsoft.NET.Sdk.Web");
        Assert.Contains("ASP.NET Core", result);
    }

    [Fact]
    public void DetectAppType_AspNetCore_WebSdk_NoSpecificRef()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs(), "Microsoft.NET.Sdk.Web");
        Assert.Contains("ASP.NET Core (Web SDK)", result);
    }

    [Fact]
    public void DetectAppType_WorkerService()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs(), "Microsoft.NET.Sdk.Worker");
        Assert.Contains("Worker Service", result);
    }

    [Fact]
    public void DetectAppType_RazorClassLibrary()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs(), "Microsoft.NET.Sdk.Razor");
        Assert.Contains("Razor Class Library", result);
    }

    [Fact]
    public void DetectAppType_RazorSdk_NotShownWhenBlazor()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Microsoft.AspNetCore.Components.WebAssembly"), "Microsoft.NET.Sdk.Razor");
        Assert.Contains("Blazor WebAssembly", result);
        Assert.DoesNotContain("Razor Class Library", result);
    }

    [Fact]
    public void DetectAppType_WebForms_WithAspxFiles()
    {
        // WebForms = hasWebFormFiles flag (from .aspx/.ascx/.master scan)
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("System.Web"), null, hasWebFormFiles: true);
        Assert.Contains("ASP.NET (WebForms)", result);
    }

    [Fact]
    public void DetectAppType_SystemWeb_WithoutAspxFiles_NotWebForms()
    {
        // System.Web alone (no .aspx files) should NOT trigger WebForms
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("System.Web"), null, hasWebFormFiles: false);
        Assert.DoesNotContain("ASP.NET (WebForms)", result);
    }

    [Fact]
    public void DetectAppType_SystemWeb_WithModernSdk_NotWebForms()
    {
        // System.Web as a .NET 10 shim should NOT trigger WebForms
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("System.Web"), "Microsoft.NET.Sdk");
        Assert.DoesNotContain("ASP.NET (WebForms)", result);
    }

    // --- Cloud / serverless ---

    [Fact]
    public void DetectAppType_AzureFunctions_IsolatedWorker()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Microsoft.Azure.Functions.Worker"), "Microsoft.NET.Sdk");
        Assert.Contains("Azure Functions", result);
    }

    [Fact]
    public void DetectAppType_AzureFunctions_WebJobs()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Microsoft.Azure.WebJobs"), "Microsoft.NET.Sdk");
        Assert.Contains("Azure Functions", result);
    }

    [Fact]
    public void DetectAppType_AspireAppHost()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Aspire.Hosting"), "Microsoft.NET.Sdk");
        Assert.Contains(".NET Aspire (AppHost)", result);
    }

    [Fact]
    public void DetectAppType_AspireServiceDefaults()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Microsoft.Extensions.ServiceDiscovery"), "Microsoft.NET.Sdk");
        Assert.Contains(".NET Aspire", result);
    }

    // --- Popular libraries ---

    [Fact]
    public void DetectAppType_EfCore()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Microsoft.EntityFrameworkCore"), "Microsoft.NET.Sdk");
        Assert.Contains("EF Core", result);
    }

    [Fact]
    public void DetectAppType_Grpc_Server()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Grpc.AspNetCore"), "Microsoft.NET.Sdk.Web");
        Assert.Contains("gRPC", result);
    }

    [Fact]
    public void DetectAppType_Grpc_Client()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Grpc.Net.Client"), "Microsoft.NET.Sdk");
        Assert.Contains("gRPC", result);
    }

    [Fact]
    public void DetectAppType_SignalR()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Microsoft.AspNetCore.SignalR.Core"), "Microsoft.NET.Sdk.Web");
        Assert.Contains("SignalR", result);
    }

    [Fact]
    public void DetectAppType_MediatR()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("MediatR"), "Microsoft.NET.Sdk");
        Assert.Contains("MediatR", result);
    }

    [Fact]
    public void DetectAppType_GraphQL_HotChocolate()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("HotChocolate.AspNetCore"), "Microsoft.NET.Sdk.Web");
        Assert.Contains("GraphQL (HotChocolate)", result);
    }

    [Fact]
    public void DetectAppType_GraphQL_HotChocolateBase()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("HotChocolate"), "Microsoft.NET.Sdk");
        Assert.Contains("GraphQL (HotChocolate)", result);
    }

    [Fact]
    public void DetectAppType_GraphQL_GraphQLNet()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("GraphQL"), "Microsoft.NET.Sdk");
        Assert.Contains("GraphQL", result);
    }

    [Fact]
    public void DetectAppType_GraphQL_GraphQLNetServer()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("GraphQL.Server.Core"), "Microsoft.NET.Sdk.Web");
        Assert.Contains("GraphQL", result);
    }

    // --- Combinations ---

    [Fact]
    public void DetectAppType_Blazor_WithEfCore()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Microsoft.AspNetCore.Components", "Microsoft.EntityFrameworkCore"), "Microsoft.NET.Sdk.Web");
        Assert.Contains("Blazor", result);
        Assert.Contains("EF Core", result);
    }

    [Fact]
    public void DetectAppType_AspNetCoreMvc_WithSignalR_AndEfCore()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs("Microsoft.AspNetCore.Mvc", "Microsoft.AspNetCore.SignalR", "Microsoft.EntityFrameworkCore"),
            "Microsoft.NET.Sdk.Web");
        Assert.Contains("ASP.NET Core MVC", result);
        Assert.Contains("SignalR", result);
        Assert.Contains("EF Core", result);
    }

    // --- Negative cases ---

    [Fact]
    public void DetectAppType_PlainLibrary_Empty()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs(), "Microsoft.NET.Sdk");
        Assert.Empty(result);
    }

    [Fact]
    public void DetectAppType_NullSdk_NoRefs_Empty()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.DetectAppType(
            Refs(), null);
        Assert.Empty(result);
    }

    #endregion
}
