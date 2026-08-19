using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.AppSettings.Core;
using RoslynMCP.Languages.WebConfig.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Settings read through a method of the solution's own — <c>Config.GetSetting("Test")</c> over a
/// wrapper that hands its parameter to the framework.
/// </summary>
/// <remarks>
/// The wrapper is declared in one project and called from another, because that is where it
/// actually lives: a shared library owns the read and every application in the solution calls it.
/// A scan that answers per project, and stops at the framework's own shapes, sees neither half.
/// </remarks>
public class ConfigForwardingTests
{
    public ConfigForwardingTests()
    {
        ConfigurationUsageIndex.Clear();
        ConfigurationManagerUsageIndex.Clear();
    }

    // ---- Microsoft.Extensions.Configuration ------------------------------------------------------

    private const string ConfigurationStubs = """
        namespace Microsoft.Extensions.Configuration
        {
            public interface IConfiguration
            {
                string? this[string key] { get; set; }

                IConfigurationSection GetSection(string key);
            }

            public interface IConfigurationSection : IConfiguration
            {
            }

            public static class ConfigurationBinder
            {
                public static T? GetValue<T>(this IConfiguration configuration, string key) => default;
            }
        }
        """;

    private const string Reader = """
        using System.Collections.Generic;
        using Microsoft.Extensions.Configuration;

        namespace Shared
        {
            public static class Config
            {
                private static IConfiguration _configuration = null!;

                public static string? GetSetting(string setting) => _configuration[setting];

                public static int GetNumber(string key) => _configuration.GetValue<int>(key);

                public static string? GetWidget(string key) =>
                    _configuration.GetSection("Widget")[key];

                // Same shape, another collection: no setting is read here or by its callers.
                public static string? GetLocal(string key) => Extras[key];

                private static readonly Dictionary<string, string> Extras = new();
            }
        }
        """;

    [Fact]
    public async Task ACallToTheSolutionsOwnReaderNamesTheKeyItPasses()
    {
        var index = await ModernAsync("""
            namespace App
            {
                public class Caller
                {
                    public string? Read() => Shared.Config.GetSetting("Test");
                }
            }
            """);

        var usage = Assert.Single(index.UsagesFor("Test"));
        Assert.EndsWith("Caller.cs", usage.FilePath);
    }

    [Fact]
    public async Task AReaderRootedInASectionPutsItsCallersKeysInsideIt()
    {
        var index = await ModernAsync("""
            namespace App
            {
                public class Caller
                {
                    public string? Read() => Shared.Config.GetWidget("Retries");
                }
            }
            """);

        Assert.Single(index.UsagesFor("Widget:Retries"));
        Assert.Empty(index.UsagesFor("Retries"));
    }

    [Fact]
    public async Task AReaderOverGetValueCountsTheSameWay()
    {
        var index = await ModernAsync("""
            namespace App
            {
                public class Caller
                {
                    public int Read() => Shared.Config.GetNumber("Retries");
                }
            }
            """);

        Assert.Single(index.UsagesFor("Retries"));
    }

    [Fact]
    public async Task AReaderOfAReaderIsFollowedToo()
    {
        var index = await ModernAsync("""
            namespace App
            {
                public static class Settings
                {
                    // Nothing here names a configuration API; the wrapper below it does.
                    public static string? Get(string name) => Shared.Config.GetSetting(name);
                }

                public class Caller
                {
                    public string? Read() => Settings.Get("Deep");
                }
            }
            """);

        Assert.Single(index.UsagesFor("Deep"));
    }

    [Fact]
    public async Task AMethodThatReadsSomethingElseEntirelyIsNotAReader()
    {
        var index = await ModernAsync("""
            namespace App
            {
                public class Caller
                {
                    public string? Read() => Shared.Config.GetLocal("Test");
                }
            }
            """);

        // The parameter reaches a dictionary, not a configuration object. Nothing is read, and a
        // name-only rule — a method called Get* taking a string — would have counted it.
        Assert.Empty(index.UsagesFor("Test"));
    }

    [Fact]
    public async Task AKeyThatIsNotALiteralNamesNothing()
    {
        var index = await ModernAsync("""
            namespace App
            {
                public class Caller
                {
                    public string? Read(string chosen) => Shared.Config.GetSetting(chosen);
                }
            }
            """);

        // A key the call site does not know is nobody's reference — and the method holding it is
        // a wrapper in turn, which is the only thing worth recording about it.
        Assert.Empty(index.Usages.Where(usage => usage.FilePath.EndsWith("Caller.cs", StringComparison.Ordinal)));
    }

    // ---- ConfigurationManager --------------------------------------------------------------------

    private const string ConfigurationManagerStubs = """
        using System.Collections.Specialized;

        namespace System.Configuration
        {
            public class ConnectionStringSettings
            {
                public string ConnectionString { get; set; } = "";
            }

            public class ConnectionStringSettingsCollection
            {
                public ConnectionStringSettings? this[string name] => null;
            }

            public static class ConfigurationManager
            {
                public static NameValueCollection AppSettings { get; } = new();

                public static ConnectionStringSettingsCollection ConnectionStrings { get; } = new();
            }
        }
        """;

    private const string FrameworkReader = """
        using System.Collections.Specialized;
        using System.Configuration;

        namespace Shared
        {
            public static class Config
            {
                public static string? GetSetting(string setting) =>
                    ConfigurationManager.AppSettings[setting];

                public static string? GetConnection(string name) =>
                    ConfigurationManager.ConnectionStrings[name]?.ConnectionString;

                // Same shape, another collection: no setting is read here or by its callers.
                public static string? GetLocal(string key) => Extras[key];

                private static readonly NameValueCollection Extras = new();
            }
        }
        """;

    [Fact]
    public async Task ACallToTheSolutionsOwnSettingReaderNamesTheSettingItPasses()
    {
        var index = await FrameworkAsync("""
            namespace App
            {
                public class Caller
                {
                    public string? Read() => Shared.Config.GetSetting("Timeout");
                }
            }
            """);

        var usage = Assert.Single(index.UsagesFor(WebConfigSection.AppSettings, "Timeout"));
        Assert.EndsWith("Caller.cs", usage.FilePath);
    }

    [Fact]
    public async Task AConnectionStringReaderKeepsItsCallersInTheRightSection()
    {
        var index = await FrameworkAsync("""
            namespace App
            {
                public class Caller
                {
                    public string? Read() => Shared.Config.GetConnection("Main");
                }
            }
            """);

        Assert.Single(index.UsagesFor(WebConfigSection.ConnectionStrings, "Main"));
        Assert.Empty(index.UsagesFor(WebConfigSection.AppSettings, "Main"));
    }

    [Fact]
    public async Task AFrameworkMethodThatReadsSomethingElseIsNotAReader()
    {
        var index = await FrameworkAsync("""
            namespace App
            {
                public class Caller
                {
                    public string? Read() => Shared.Config.GetLocal("Timeout");
                }
            }
            """);

        Assert.True(index.IsEmpty);
    }

    // ---- Building the pieces ----------------------------------------------------------------------

    private static Task<ConfigurationUsageIndex> ModernAsync(string caller) =>
        ConfigurationUsageIndex.GetAsync(Application(ConfigurationStubs, Reader, caller), default);

    private static Task<ConfigurationManagerUsageIndex> FrameworkAsync(string caller) =>
        ConfigurationManagerUsageIndex.GetAsync(
            Application(ConfigurationManagerStubs, FrameworkReader, caller), default);

    /// <summary>
    /// Two projects: a library owning the read, and the application calling it. Referenced the way
    /// most legacy solutions actually do it would be by assembly name, but a project reference is
    /// the harder case here — the wrapper's symbol comes from another compilation either way.
    /// </summary>
    private static Project Application(string stubs, string reader, string caller)
    {
        var workspace = new AdhocWorkspace();

        var libraryId = ProjectId.CreateNewId();
        var applicationId = ProjectId.CreateNewId();

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                libraryId, VersionStamp.Default, "Shared", "Shared", LanguageNames.CSharp,
                metadataReferences: Runtime))
            .AddProject(ProjectInfo.Create(
                applicationId, VersionStamp.Default, "Application", "Application",
                LanguageNames.CSharp,
                metadataReferences: Runtime,
                projectReferences: [new ProjectReference(libraryId)]));

        solution = solution
            .AddDocument(DocumentId.CreateNewId(libraryId), "Stubs.cs", stubs, filePath: @"C:\src\Stubs.cs")
            .AddDocument(DocumentId.CreateNewId(libraryId), "Config.cs", reader, filePath: @"C:\src\Config.cs")
            .AddDocument(
                DocumentId.CreateNewId(applicationId), "Caller.cs", caller,
                filePath: @"C:\src\App\Caller.cs");

        return solution.GetProject(applicationId)!;
    }

    private static readonly string s_runtimeDirectory =
        Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    private static readonly MetadataReference[] Runtime =
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(s_runtimeDirectory, "System.Runtime.dll")),
        MetadataReference.CreateFromFile(Path.Combine(s_runtimeDirectory, "System.Collections.dll")),
        MetadataReference.CreateFromFile(
            Path.Combine(s_runtimeDirectory, "System.Collections.Specialized.dll")),
        MetadataReference.CreateFromFile(
            Path.Combine(s_runtimeDirectory, "System.Collections.NonGeneric.dll")),
    ];
}
