using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Services;
using RoslynMCP.Services.ExternalSource;
using RoslynMCP.Services.MetadataConfiguration;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Configuration read out of compiled assemblies: the keys a package names inside its own IL,
/// which no amount of reading the solution's source can find.
/// </summary>
/// <remarks>
/// The libraries under test are compiled here rather than taken from the package cache, so the
/// test says what it means — these exact reads, this exact shape — and says it the same on a
/// machine that has never restored a package.
/// </remarks>
public class MetadataConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "roslynsense-metadata-" + Guid.NewGuid().ToString("N"));

    public MetadataConfigurationTests()
    {
        Directory.CreateDirectory(_directory);
        MetadataConfigurationScanner.Clear();
        MetadataConfigurationIndex.Clear();
    }

    public void Dispose()
    {
        MetadataConfigurationScanner.Clear();
        MetadataConfigurationIndex.Clear();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    // ---- The modern shape ----------------------------------------------------------------------

    private const string PackageSource = """
        using Microsoft.Extensions.Configuration;

        namespace Contoso.Hosting
        {
            public static class Listener
            {
                public static string? Port(IConfiguration configuration) =>
                    configuration.GetSection("Kestrel")["Port"];

                public static int Retries(IConfiguration configuration) =>
                    configuration.GetValue<int>("Widget:Retries", 3);

                public static string? Database(IConfiguration configuration) =>
                    configuration.GetConnectionString("Main");

                // Named the same as the real thing, declared by something else entirely.
                public static string Decoy() => Impostor.GetValue("NotASetting");
            }

            public static class Impostor
            {
                public static string GetValue(string key) => key;
            }
        }
        """;

    [Fact]
    public async Task ThePackageSKeysAreFoundThoughNoSourceInTheSolutionNamesThem()
    {
        string package = Emit("Contoso.Hosting", PackageSource, ConfigurationReferences);
        var index = await IndexAsync(package, ConfigurationReferences);

        var names = index.Names(MetadataConfigurationKind.Path).ToList();

        Assert.Contains("Kestrel", names);
        Assert.Contains("Widget:Retries", names);

        // GetConnectionString names a section the runtime resolves under ConnectionStrings.
        Assert.Contains("ConnectionStrings:Main", names);
    }

    [Fact]
    public async Task AMethodNamedLikeTheApiButDeclaredElsewhereIsNotAConfigurationRead()
    {
        string package = Emit("Contoso.Hosting", PackageSource, ConfigurationReferences);
        var index = await IndexAsync(package, ConfigurationReferences);

        // The name filter offered Impostor.GetValue as a candidate; the type system rejected it.
        Assert.DoesNotContain("NotASetting", index.Names(MetadataConfigurationKind.Path));
    }

    [Fact]
    public async Task AReadIsAttributedToTheAssemblyAndTypeItWasCompiledInto()
    {
        string package = Emit("Contoso.Hosting", PackageSource, ConfigurationReferences);
        var index = await IndexAsync(package, ConfigurationReferences);

        var read = Assert.Single(index.ReadsFor(MetadataConfigurationKind.Path, "Kestrel"));

        Assert.Equal("Contoso.Hosting", read.AssemblyName);
        Assert.Equal("Contoso.Hosting.Listener", read.TypeName);
        Assert.Equal(package, read.AssemblyPath);
    }

    [Fact]
    public async Task AnAssemblyThatNamesNoConfigurationTypeIsNeverOpened()
    {
        string plain = Emit("Contoso.Plain", """
            namespace Contoso
            {
                public static class Plain
                {
                    public static string GetSection(string name) => name;
                    public static string Use() => GetSection("Kestrel");
                }
            }
            """);

        var index = await IndexAsync(plain);

        Assert.True(index.IsEmpty);
    }

    // ---- The Framework shape -------------------------------------------------------------------

    /// <summary>The two configuration managers, in the shape the Framework declares them.</summary>
    private const string ManagersSource = """
        using System.Collections.Specialized;

        namespace System.Configuration
        {
            public static class ConfigurationManager
            {
                public static NameValueCollection AppSettings { get; } = new NameValueCollection();

                public static ConnectionStringSettingsCollection ConnectionStrings { get; } = new();
            }

            public sealed class ConnectionStringSettings
            {
                public string ConnectionString { get; set; } = string.Empty;
            }

            public sealed class ConnectionStringSettingsCollection
            {
                public ConnectionStringSettings? this[string name] => null;
            }
        }

        namespace System.Web.Configuration
        {
            public static class WebConfigurationManager
            {
                public static NameValueCollection AppSettings { get; } = new NameValueCollection();
            }
        }
        """;

    private const string LegacySource = """
        using System.Collections.Specialized;
        using System.Configuration;
        using System.Web.Configuration;

        namespace Contoso.Legacy
        {
            public static class Settings
            {
                public static string? Timeout() => ConfigurationManager.AppSettings["Timeout"];

                public static string? Theme() => WebConfigurationManager.AppSettings["Theme"];

                public static string? Database() =>
                    ConfigurationManager.ConnectionStrings["Main"]?.ConnectionString;

                // A collection of its own: the same get_Item, none of the meaning.
                public static string? Decoy(NameValueCollection headers) => headers["NotASetting"];
            }
        }
        """;

    [Fact]
    public async Task BothConfigurationManagersAreReadThroughTheirOwnSections()
    {
        string managers = Emit("System.Configuration.Stub", ManagersSource);
        var managersReference = MetadataReference.CreateFromFile(managers);
        string legacy = Emit("Contoso.Legacy", LegacySource, managersReference);

        var index = await IndexAsync(legacy, managersReference);

        var appSettings = index.Names(MetadataConfigurationKind.AppSetting).ToList();

        Assert.Contains("Timeout", appSettings);                    // ConfigurationManager
        Assert.Contains("Theme", appSettings);                      // WebConfigurationManager
        Assert.Contains("Main", index.Names(MetadataConfigurationKind.ConnectionString));
    }

    [Fact]
    public async Task AnIndexerOnSomeOtherCollectionIsNotASettingRead()
    {
        string managers = Emit("System.Configuration.Stub", ManagersSource);
        var managersReference = MetadataReference.CreateFromFile(managers);
        string legacy = Emit("Contoso.Legacy", LegacySource, managersReference);

        var index = await IndexAsync(legacy, managersReference);

        Assert.DoesNotContain("NotASetting", index.Names(MetadataConfigurationKind.AppSetting));
    }

    [Fact]
    public async Task AnAssemblyBuiltByAProjectInTheSolutionIsLeftToItsSource()
    {
        string package = Emit("Contoso.Hosting", PackageSource, ConfigurationReferences);

        // Same assembly name as a project in the solution: its source is already indexed, and
        // counting the compiled copy too would report every read twice.
        var index = await IndexAsync(package, ConfigurationReferences, siblingAssemblyName: "Contoso.Hosting");

        Assert.True(index.IsEmpty);
    }

    // ---- Landing on the call ---------------------------------------------------------------------

    [Fact]
    public async Task AnExternalReadOpensOnItsOwnLineInTheDecompiledSource()
    {
        string package = Emit("Contoso.Hosting", PackageSource, ConfigurationReferences);
        var index = await IndexAsync(package, ConfigurationReferences);

        var read = Assert.Single(index.ReadsFor(MetadataConfigurationKind.Path, "Kestrel"));

        var decompiled = await DecompiledSourceService.TryDecompileTypeToFileAsync(
            read.AssemblyPath, read.TypeName, default);

        Assert.NotNull(decompiled);
        string[] lines = await File.ReadAllLinesAsync(decompiled!.Value.FilePath);

        var position = SourceMemberLocator.FindLiteral(
            string.Join(Environment.NewLine, lines), read.Literal, read.MethodName, default);

        Assert.NotNull(position);

        // The line the lens promised: the call, not the top of the type.
        Assert.Contains("\"Kestrel\"", lines[position!.Value.Line]);
        Assert.Contains("GetSection", lines[position.Value.Line]);
    }

    [Fact]
    public void TheLiteralIsFoundInTheMethodThatCompiledIt()
    {
        const string source = """
            class C
            {
                string Other() => "Key";
                string Wanted() => "Key";
            }
            """;

        var position = SourceMemberLocator.FindLiteral(source, "Key", "Wanted", default);

        Assert.NotNull(position);
        Assert.Equal(3, position!.Value.Line);
    }

    [Fact]
    public void ALiteralTheDecompilerReshapedFallsBackToWhereverItIs()
    {
        const string source = """
            class C
            {
                string Inlined() => "Key";
            }
            """;

        // The method the IL named is gone; the key is still the honest answer.
        var position = SourceMemberLocator.FindLiteral(source, "Key", "Vanished", default);

        Assert.Equal(2, position!.Value.Line);
    }

    [Fact]
    public void AKeyThatIsOnlyMentionedInProseIsNotALocation()
    {
        const string source = """
            class C
            {
                // Kestrel is configured elsewhere.
                string Other() => "Something";
            }
            """;

        Assert.Null(SourceMemberLocator.FindLiteral(source, "Kestrel", "Other", default));
    }

    // ---- Building the pieces --------------------------------------------------------------------

    /// <summary>The configuration surface a package compiles against: the abstractions, and the
    /// binder the extension methods live in.</summary>
    private static MetadataReference[] ConfigurationReferences { get; } =
    [
        MetadataReference.CreateFromFile(
            typeof(Microsoft.Extensions.Configuration.IConfiguration).Assembly.Location),
        MetadataReference.CreateFromFile(
            typeof(Microsoft.Extensions.Configuration.ConfigurationBinder).Assembly.Location),
        MetadataReference.CreateFromFile(
            typeof(Microsoft.Extensions.Configuration.ConfigurationExtensions).Assembly.Location),
    ];

    private static readonly string s_runtimeDirectory =
        Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    /// <summary>Compiles a library to disk, the way a package arrives.</summary>
    private string Emit(string assemblyName, string source, params MetadataReference[] references)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(s_runtimeDirectory, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(
                    Path.Combine(s_runtimeDirectory, "System.Collections.Specialized.dll")),
                MetadataReference.CreateFromFile(
                    Path.Combine(s_runtimeDirectory, "System.Collections.NonGeneric.dll")),
                .. references,
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        string path = Path.Combine(_directory, assemblyName + ".dll");
        var result = compilation.Emit(path);

        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        return path;
    }

    private Task<MetadataConfigurationIndex> IndexAsync(
        string assemblyPath, params MetadataReference[] references) =>
        IndexAsync(assemblyPath, references, siblingAssemblyName: null);

    /// <summary>
    /// An application referencing the compiled library, as a project the workspace holds.
    /// </summary>
    private async Task<MetadataConfigurationIndex> IndexAsync(
        string assemblyPath, MetadataReference[] references, string? siblingAssemblyName)
    {
        var workspace = new AdhocWorkspace();

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "Application", "Application",
            LanguageNames.CSharp,
            metadataReferences:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(assemblyPath),
                .. references,
            ]));

        if (siblingAssemblyName is { Length: > 0 })
        {
            solution = solution.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Default, "Sibling", siblingAssemblyName,
                LanguageNames.CSharp));
        }

        var project = solution.Projects.Single(p => p.Name == "Application");

        return await MetadataConfigurationIndex.GetAsync(project, default);
    }
}
