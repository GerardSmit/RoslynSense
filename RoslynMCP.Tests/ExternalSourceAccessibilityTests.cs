using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ExternalSource;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public class ExternalSourceAccessibilityTests
{
    [Theory]
    [InlineData("ReferenceSource")]
    [InlineData("Decompiled")]
    [InlineData("SourceLink")]
    [InlineData("EmbeddedSource")]
    public async Task ExternalDiagnosticsAreClearedWhileSemanticFeaturesRemainAvailable(string kind)
    {
        string directory = Path.Combine(Path.GetTempPath(), "RoslynMCP", kind, "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string source = "class Reader { MissingType broken; int Value() => 42; }";
        string file = WriteSource(directory, typeof(object).Assembly.Location, source, "Reader");
        var document = (await WorkspaceService.FindDocumentAsync(file, default))!;
        Assert.Contains((await document.GetSemanticModelAsync())!.GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);
        var identifier = new TextDocumentIdentifier(LspConverters.PathToUri(file));
        var report = Assert.IsType<FullDocumentDiagnosticReport>(await DiagnosticsHandler.PullAsync(
            new DocumentDiagnosticParams(identifier, "old-report-with-errors"), default));
        Assert.Empty(report.Items);
        Assert.IsType<UnchangedDocumentDiagnosticReport>(await DiagnosticsHandler.PullAsync(
            new DocumentDiagnosticParams(identifier, report.ResultId), default));
        Assert.Empty(await DiagnosticsHandler.ComputeAsync(file, default));
        Assert.Empty(await DiagnosticsHandler.ComputeWithAnalyzersAsync(file, default));
        var position = new TextDocumentPositionParams(identifier, new Position(0, source.IndexOf("Value", StringComparison.Ordinal)));
        Assert.NotNull(await HoverHandler.HoverAsync(position, default));
        Assert.NotEmpty(await NavigationHandlers.DefinitionAsync(position, false, default));
    }

    [Fact]
    public async Task OrdinaryProjectsKeepFrameworkReferences()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
        var compilation = (await project.GetCompilationAsync())!;
        Assert.True(compilation.GetTypeByMetadataName("System.String") is not null,
            string.Join("\n", compilation.GetDiagnostics().Take(10)) + "\nReferences:\n" +
            string.Join("\n", compilation.References.Select(r => r.Display)));
    }

    [Theory]
    [InlineData("Library", "1.0.0.0", true)]
    [InlineData("Other", "1.0.0.0", false)]
    [InlineData("Library", "2.0.0.0", false)]
    public void BrowsingRedirectRequiresMatchingAssemblyIdentity(string implementationName, string implementationVersion, bool matching)
    {
        string directory = Path.Combine(ExternalSourceCache.ReferenceSourceDirectory, "tests", Guid.NewGuid().ToString("N"));
        string reference = Path.Combine(directory, "ref", "net10.0", "Library.dll");
        string implementation = Path.Combine(directory, "lib", "net10.0", "Library.dll");
        foreach (var (path, name, version) in new[]
                 { (reference, "Library", "1.0.0.0"), (implementation, implementationName, implementationVersion) })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var compilation = CSharpCompilation.Create(name,
                [CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{version}\")] public class Library {{ }}")],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.True(compilation.Emit(path).Success);
        }
        Assert.Equal(matching ? implementation : reference, ReferenceAssemblyRedirector.RedirectForBrowsing(reference));
    }

    [Fact]
    public async Task PackageImplementationMembersBindAndKeepReferenceAssemblyProvenance()
    {
        using var offline = ExternalSourceScope.Offline();
        string directory = Path.Combine(ExternalSourceCache.ReferenceSourceDirectory, "tests", Guid.NewGuid().ToString("N"));
        string reference = Path.Combine(directory, "ref", "net10.0", "BrowsingPackage.dll");
        string implementation = Path.Combine(directory, "lib", "net10.0", "BrowsingPackage.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(reference)!);
        Directory.CreateDirectory(Path.GetDirectoryName(implementation)!);
        var library = CSharpCompilation.Create("BrowsingPackage",
            [CSharpSyntaxTree.ParseText("public class Library { private int PrivateField = 42; internal int InternalProperty => PrivateField; }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var implementationResult = library.Emit(implementation);
        Assert.True(implementationResult.Success, string.Join("\n", implementationResult.Diagnostics));
        using (var referenceStream = File.Create(reference))
        {
            var referenceResult = library.Emit(referenceStream, options: new EmitOptions(metadataOnly: true, includePrivateMembers: false));
            Assert.True(referenceResult.Success, string.Join("\n", referenceResult.Diagnostics));
        }

        // Even importing all metadata cannot recover members omitted from the reference image.
        var referenceConsumer = CSharpCompilation.Create("Consumer",
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), MetadataReference.CreateFromFile(reference)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithMetadataImportOptions(MetadataImportOptions.All));
        var referenceType = referenceConsumer.GetTypeByMetadataName("Library")!;
        Assert.Empty(referenceType.GetMembers("PrivateField"));
        Assert.Empty(referenceType.GetMembers("InternalProperty"));

        const string source = "class Reader { int Read(Library value) => value.PrivateField + value.InternalProperty; }";
        string file = WriteSource(directory, reference, source, "Reader");
        var document = (await WorkspaceService.FindDocumentAsync(file, default))!;
        var model = (await document.GetSemanticModelAsync())!;
        Assert.DoesNotContain(model.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);

        foreach (string name in new[] { "PrivateField", "InternalProperty" })
        {
            int offset = source.IndexOf(name, StringComparison.Ordinal);
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, default);
            Assert.NotNull(symbol);
            Assert.Equal(name, symbol!.Name);
            Assert.All(symbol.Locations, location => Assert.True(location.IsInMetadata));
            Assert.Equal(reference, await SourceMemberLocator.AssemblyPathAsync(symbol, document.Project, default), ignoreCase: true);

            var position = new TextDocumentPositionParams(new TextDocumentIdentifier(LspConverters.PathToUri(file)), new Position(0, offset));
            var hover = await HoverHandler.HoverAsync(position, default);
            Assert.NotNull(hover);
            Assert.Contains(name, hover!.Contents.Value);
            var locations = await NavigationHandlers.DefinitionAsync(position, false, default);
            Assert.Contains(locations, location => File.ReadAllLines(LspConverters.UriToPath(location.Uri))[location.Range.Start.Line]
                .Contains(name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ExternalBrowsingBindsPrivateAndInternalMetadataMembers()
    {
        using var offline = ExternalSourceScope.Offline();
        string directory = Path.Combine(ExternalSourceCache.ReferenceSourceDirectory, "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string assembly = Path.Combine(directory, "ExternalNavigationFixture.dll");
        var library = CSharpCompilation.Create("ExternalNavigationFixture",
            [CSharpSyntaxTree.ParseText("public class Library { internal int InternalField; private int PrivateField; internal int InternalProperty => 1; }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.True(library.Emit(assembly).Success);
        string source = "class Reader { int Read(Library x) => x.InternalField + x.PrivateField + x.InternalProperty; }";
        string file = WriteSource(directory, assembly, source, "Reader");
        var document = (await WorkspaceService.FindDocumentAsync(file, default))!;
        var model = (await document.GetSemanticModelAsync())!;
        Assert.DoesNotContain(model.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);

        foreach (string name in new[] { "InternalField", "PrivateField", "InternalProperty" })
        {
            int offset = source.IndexOf(name, StringComparison.Ordinal);
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, default);
            Assert.Equal(name, symbol?.Name);
            Assert.All(symbol!.Locations, l => Assert.True(l.IsInMetadata));
            var position = new TextDocumentPositionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(file)), new Position(0, offset));
            Assert.NotNull(await HoverHandler.HoverAsync(position, default));
            var locations = await NavigationHandlers.DefinitionAsync(position, false, default);
            Assert.NotEmpty(locations);
            Assert.Contains(locations, l => File.ReadAllLines(LspConverters.UriToPath(l.Uri))[l.Range.Start.Line].Contains(name, StringComparison.Ordinal));
        }

        // The permissive binding is confined to the browsing project.
        var normal = CSharpCompilation.Create("Consumer", [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), MetadataReference.CreateFromFile(assembly)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Contains(normal.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [RequiresFrameworkReferenceAssembliesFact]
    public async Task FrameworkPackageResolvesDependenciesOutsideItsOwnDirectory()
    {
        using var offline = ExternalSourceScope.Offline();
        string framework = DecompiledFrameworkTests.FrameworkReferenceDirectory()!;
        string directory = Path.Combine(ExternalSourceCache.ReferenceSourceDirectory, "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string assembly = Path.Combine(directory, "FrameworkPackage.dll");
        const string source = "public class FrameworkPackage { public object Read() => System.Web.HttpContext.Current; }";
        var compilation = CSharpCompilation.Create("FrameworkPackage",
            [CSharpSyntaxTree.ParseText(source)],
            new[] { "mscorlib.dll", "System.dll", "System.Web.dll" }
                .Select(name => MetadataReference.CreateFromFile(Path.Combine(framework, name))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var emitted = compilation.Emit(assembly);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        string file = WriteSource(directory, assembly, source, "FrameworkPackage");
        var document = (await WorkspaceService.FindDocumentAsync(file, default))!;
        var model = (await document.GetSemanticModelAsync())!;
        Assert.DoesNotContain(model.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(model.Compilation.References,
            r => Path.GetFileName(r.Display) == "System.Private.CoreLib.dll");
        foreach (string name in new[] { "HttpContext", "Current" })
        {
            var position = new TextDocumentPositionParams(new TextDocumentIdentifier(LspConverters.PathToUri(file)),
                new Position(0, source.IndexOf(name, StringComparison.Ordinal)));
            var hover = await HoverHandler.HoverAsync(position, default);
            Assert.NotNull(hover);
            Assert.Contains(name, hover!.Contents.Value);
            var locations = await NavigationHandlers.DefinitionAsync(position, false, default);
            Assert.NotEmpty(locations);
            Assert.Contains(locations, l => File.ReadAllLines(LspConverters.UriToPath(l.Uri))[l.Range.Start.Line].Contains(name, StringComparison.Ordinal));
        }
    }

    [RequiresFrameworkReferenceAssembliesFact]
    public async Task UserControlImplementationMembersBindAndKeepReferenceAssemblyProvenance()
    {
        string assembly = Path.Combine(DecompiledFrameworkTests.FrameworkReferenceDirectory()!, "System.Web.dll");
        Assert.NotEqual(assembly, ReferenceAssemblyRedirector.RedirectForBrowsing(assembly));
        string source = """
            namespace System.Web.UI {
                public class UserControl : TemplateControl {
                    void Initialize(Page page) { _page = page; HookUpAutomaticHandlers(); }
                }
                public class UserControlControlBuilder : ControlBuilder {
                    bool Read() => InDesigner;
                }
                public class FileLevelUserControlBuilder : RootBuilder { }
            }
            """;
        string directory = Path.Combine(ExternalSourceCache.ReferenceSourceDirectory, "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = WriteSource(directory, assembly, source, "System.Web.UI.UserControl");
        var document = (await WorkspaceService.FindDocumentAsync(file, default))!;
        foreach (string name in new[] { "TemplateControl", "_page", "HookUpAutomaticHandlers", "InDesigner", "RootBuilder" })
        {
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, source.IndexOf(name, StringComparison.Ordinal), default);
            Assert.NotNull(symbol);
            Assert.Equal(name, symbol!.Name);
            Assert.Equal(assembly, await SourceMemberLocator.AssemblyPathAsync(symbol, document.Project, default), ignoreCase: true);
        }
    }

    [Fact]
    public async Task CoreRuntimePrivateFieldSupportsHoverAndDefinitionFromSourceLinkDocument()
    {
        using var offline = ExternalSourceScope.Offline();
        const string name = "_stringLength";
        Assert.NotNull(typeof(string).GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        string directory = Path.Combine(ExternalSourceCache.SourceLinkDirectory, "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = $"class Reader {{ int Read(string value) => value.{name}; }}";
        string file = WriteSource(directory, typeof(string).Assembly.Location, source, "Reader", ExternalSourceKind.SourceLink);
        var position = new TextDocumentPositionParams(new TextDocumentIdentifier(LspConverters.PathToUri(file)),
            new Position(0, source.IndexOf(name, StringComparison.Ordinal)));
        var hover = await HoverHandler.HoverAsync(position, default);
        Assert.NotNull(hover);
        Assert.Contains(name, hover!.Contents.Value);
        var locations = await NavigationHandlers.DefinitionAsync(position, false, default);
        Assert.Contains(locations, l => File.ReadAllLines(LspConverters.UriToPath(l.Uri))[l.Range.Start.Line].Contains(name, StringComparison.Ordinal));
    }

    [RequiresFrameworkSnapshotFact]
    public async Task PublishedUserControlSupportsHoverAndDefinition()
    {
        using var online = ExternalSourceScope.Online();
        string assembly = Path.Combine(Path.GetDirectoryName(ExternalSourceNetworkTests.FrameworkSystemAssembly())!, "System.Web.dll");
        var result = await ReferenceSourceService.TryResolveAsync(null, "System.Web.UI.UserControl", assembly, default);
        Assert.NotNull(result);
        ExternalSourceProject.Ensure(result!, "System.Web.UI.UserControl");
        var text = SourceText.From(await File.ReadAllTextAsync(result!.FilePath));
        foreach (string name in new[] { "TemplateControl", "_page", "HookUpAutomaticHandlers", "InDesigner", "RootBuilder" })
        {
            var line = text.Lines.GetLinePosition(text.ToString().IndexOf(name, StringComparison.Ordinal));
            var position = new TextDocumentPositionParams(new TextDocumentIdentifier(LspConverters.PathToUri(result.FilePath)),
                new Position(line.Line, line.Character));
            var hover = await HoverHandler.HoverAsync(position, default);
            Assert.NotNull(hover);
            Assert.Contains(name, hover!.Contents.Value);
            var locations = await NavigationHandlers.DefinitionAsync(position, false, default);
            Assert.NotEmpty(locations);
            Assert.All(locations, l => Assert.True(File.Exists(LspConverters.UriToPath(l.Uri))));
            Assert.Contains(locations, l => File.ReadAllLines(LspConverters.UriToPath(l.Uri))[l.Range.Start.Line].Contains(name, StringComparison.Ordinal));
        }
    }

    private static string WriteSource(string directory, string assembly, string source, string type,
        ExternalSourceKind kind = ExternalSourceKind.ReferenceSource)
    {
        string file = Path.Combine(directory, "Fetched.cs");
        File.WriteAllText(file, source);
        ExternalSourceProject.Ensure(new ExternalSourceResult(kind,
            assembly, file, [new LinePosition(0, 0)], "test"), type);
        return file;
    }
}
