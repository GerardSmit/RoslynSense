using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host;
using System.Runtime.CompilerServices;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public sealed class LazyMetadataServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lazy-metadata-" + Guid.NewGuid().ToString("N"));
    private static readonly PortableExecutableReference s_runtime = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

    public LazyMetadataServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static AdhocWorkspace Workspace() => new(HostComposition.HostServices);

    private static IMetadataService Service(AdhocWorkspace workspace) =>
        Assert.IsType<LazyMetadataService>(workspace.Services.GetRequiredService<IMetadataService>());

    private string Emit(string source, string name = "Library", OutputKind kind = OutputKind.DynamicallyLinkedLibrary)
    {
        string path = Path.Combine(_directory, name + (kind == OutputKind.NetModule ? ".netmodule" : ".dll"));
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], [s_runtime],
            new CSharpCompilationOptions(kind));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }

    private static CSharpCompilation Consumer(string source, PortableExecutableReference reference) =>
        CSharpCompilation.Create("Consumer", [CSharpSyntaxTree.ParseText(source)], [s_runtime, reference],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static void AssertEmits(CSharpCompilation compilation)
    {
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }

    [Fact]
    public void ReferenceDoesNotReadAnExclusivelyLockedImageUntilFirstObservation()
    {
        string path = Emit("public class Product { public int Value; }");
        using var workspace = Workspace();
        PortableExecutableReference reference;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            reference = Service(workspace).GetReference(path, MetadataReferenceProperties.Assembly);
            Assert.Equal(path, reference.FilePath);
            Assert.Same(reference, Service(workspace).GetReference(path, MetadataReferenceProperties.Assembly));
        }

        AssertEmits(Consumer("public class Consumer { public int Read(Product p) => p.Value; }", reference));
        // First observation snapshots bytes and releases the original file, so builds can replace it.
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void FirstObservationSeesReplacementAndObservedReferencesKeepTheirImage()
    {
        string path = Emit("public class Product { public int VersionOne; }");
        using var workspace = Workspace();
        var reference = Service(workspace).GetReference(path, MetadataReferenceProperties.Assembly);
        Emit("public class Product { public int VersionTwo; }");
        var before = Consumer("public class Consumer { public int Read(Product p) => p.VersionTwo; }", reference);
        AssertEmits(before);
        var metadataId = reference.GetMetadataId();

        Emit("public class Product { public int VersionThree; }");
        AssertEmits(before);
        AssertEmits(Consumer("public class Consumer { public int Read(Product p) => p.VersionTwo; }", reference));
        Assert.Equal(metadataId, reference.GetMetadataId());
        Assert.Empty(before.GetTypeByMetadataName("Product")!.GetMembers("VersionThree"));

        using var freshWorkspace = Workspace();
        var fresh = Service(freshWorkspace).GetReference(path, MetadataReferenceProperties.Assembly);
        AssertEmits(Consumer("public class Consumer { public int Read(Product p) => p.VersionThree; }", fresh));
        Assert.NotEqual(metadataId, fresh.GetMetadataId());
    }

    [Fact]
    public void AliasesAndInteropPropertiesShareTheObservedImageAndDocumentation()
    {
        string path = Emit("public class Product { public int Value; }");
        File.WriteAllText(Path.ChangeExtension(path, ".xml"),
            "<doc><members><member name=\"T:Product\"><summary>Product documentation.</summary></member></members></doc>");
        using var workspace = Workspace();
        var service = Service(workspace);
        var reference = service.GetReference(path, MetadataReferenceProperties.Assembly);
        var aliased = service.GetReference(path, MetadataReferenceProperties.Assembly.WithAliases(["external"]));
        var embedded = reference.WithEmbedInteropTypes(true);
        var compilation = Consumer("extern alias external; public class Consumer { public external::Product Value; }", aliased);
        AssertEmits(compilation);
        var assembly = Assert.IsAssignableFrom<IAssemblySymbol>(compilation.GetAssemblyOrModuleSymbol(aliased));
        Assert.Contains("Product documentation.", assembly.GetTypeByMetadataName("Product")!.GetDocumentationCommentXml());
        Assert.Equal(reference.GetMetadataId(), aliased.GetMetadataId());
        Assert.Equal(reference.GetMetadataId(), embedded.GetMetadataId());
        Assert.Equal<string>(["external"], aliased.Properties.Aliases);
        Assert.True(embedded.Properties.EmbedInteropTypes);
        Assert.False(reference.Properties.EmbedInteropTypes);
    }

    [Fact]
    public void ModuleReferenceCanBeLinkedAndEmitted()
    {
        string path = Emit("public class ModuleProduct { public int Value; }", "ProductModule", OutputKind.NetModule);
        using var workspace = Workspace();
        var reference = Service(workspace).GetReference(path, MetadataReferenceProperties.Module);
        Assert.Equal(MetadataImageKind.Module, reference.Properties.Kind);
        Assert.IsType<ModuleMetadata>(reference.GetMetadata());
        AssertEmits(Consumer("public class Consumer { public int Read(ModuleProduct p) => p.Value; }", reference));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void MultimoduleAssemblySharesAdditionalModuleSnapshotsAcrossBindsAndAliases()
    {
        string modulePath = Emit("public class ModuleProduct { public int Value; }", "ProductPart", OutputKind.NetModule);
        string assemblyPath = Path.Combine(_directory, "Aggregate.dll");
        var manifest = CSharpCompilation.Create("Aggregate",
            [CSharpSyntaxTree.ParseText("public class ManifestProduct { }")],
            [s_runtime, MetadataReference.CreateFromFile(modulePath, MetadataReferenceProperties.Module)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using (var output = File.Create(assemblyPath))
        {
            var result = manifest.Emit(output);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        }

        try
        {
            AssertAdditionalModuleSnapshotsAreShared(assemblyPath);
        }
        finally
        {
            // Roslyn's existing reader may keep constituent netmodule streams open. The helper
            // keeps those references out of this frame so their finalizers can release the
            // temporary fixture before Dispose removes it, including when an assertion fails.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertAdditionalModuleSnapshotsAreShared(string path)
    {
        using var workspace = Workspace();
        var reference = Service(workspace).GetReference(path, MetadataReferenceProperties.Assembly);
        AssertEmits(Consumer("public class Consumer { public int Read(ModuleProduct p) => p.Value; }", reference));
        var first = Assert.IsType<AssemblyMetadata>(reference.GetMetadata()).GetModules();
        Assert.Equal(2, first.Length);
        var second = Assert.IsType<AssemblyMetadata>(reference.GetMetadata()).GetModules();
        var alias = reference.WithAliases(["external"]);
        AssertEmits(Consumer("extern alias external; public class Consumer { public external::ModuleProduct Value; }", alias));
        var aliased = Assert.IsType<AssemblyMetadata>(alias.GetMetadata()).GetModules();
        Assert.Equal(first.Select(module => module.Id), second.Select(module => module.Id));
        Assert.Equal(first.Select(module => module.Id), aliased.Select(module => module.Id));
    }

    [Fact]
    public void OpeningFailureIsStableEvenIfTheFileAppearsLater()
    {
        string path = Path.Combine(_directory, "Library.dll");
        using var workspace = Workspace();
        var reference = Service(workspace).GetReference(path, MetadataReferenceProperties.Assembly);
        var failure = Assert.ThrowsAny<IOException>(() => reference.GetMetadata());
        Emit("public class Product { }");
        Assert.Same(failure, Assert.ThrowsAny<IOException>(() => reference.GetMetadata()));
        Assert.Contains(Consumer("class Consumer { }", reference).GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        using var freshWorkspace = Workspace();
        AssertEmits(Consumer("class Consumer { Product p; }",
            Service(freshWorkspace).GetReference(path, MetadataReferenceProperties.Assembly)));
    }

    [Fact]
    public void MalformedImageProducesCompilerDiagnosticsWithoutPoisoningNewWorkspace()
    {
        string path = Path.Combine(_directory, "Library.dll");
        File.WriteAllText(path, "This is not an assembly.");
        using var workspace = Workspace();
        var reference = Service(workspace).GetReference(path, MetadataReferenceProperties.Assembly);
        Assert.Contains(Consumer("class Consumer { }", reference).GetDiagnostics(), d => d.Id == "CS0009");
        Emit("public class Product { }");
        Assert.Contains(Consumer("class Consumer { }", reference).GetDiagnostics(), d => d.Id == "CS0009");
        using var freshWorkspace = Workspace();
        AssertEmits(Consumer("class Consumer { Product p; }",
            Service(freshWorkspace).GetReference(path, MetadataReferenceProperties.Assembly)));
    }
}
