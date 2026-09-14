using Microsoft.CodeAnalysis;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public sealed class FrameworkReferenceProbeTests
{
    [Fact]
    public void CoreProbeDoesNotOpenUnrelatedAssemblies()
    {
        using var workspace = new AdhocWorkspace();
        var core = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var unrelated = new UnreadableReference("unrelated.dll");
        var project = workspace.AddProject("App", LanguageNames.CSharp)
            .AddMetadataReferences([core, unrelated]);

        Assert.True(WorkspaceService.ResolvesCorlib(project));
        Assert.Equal(0, unrelated.ReadCount);
    }

    [Fact]
    public void UnconventionalCoreFileNameUsesFullReferenceFallback()
    {
        using var workspace = new AdhocWorkspace();
        var core = MetadataReference.CreateFromImage(
            File.ReadAllBytes(typeof(object).Assembly.Location), filePath: "custom-core.dll");
        var project = workspace.AddProject("App", LanguageNames.CSharp).AddMetadataReference(core);

        Assert.True(WorkspaceService.ResolvesCorlib(project));
    }

    [Fact]
    public void ConventionalNameAloneDoesNotProveFrameworkPresence()
    {
        using var workspace = new AdhocWorkspace();
        // An ordinary assembly renamed to a core-looking filename does not define System.Object.
        var fakeCore = MetadataReference.CreateFromImage(
            File.ReadAllBytes(typeof(FrameworkReferenceProbeTests).Assembly.Location), filePath: "mscorlib.dll");
        var unrelated = MetadataReference.CreateFromFile(typeof(WorkspaceService).Assembly.Location);
        var project = workspace.AddProject("App", LanguageNames.CSharp)
            .AddMetadataReferences([fakeCore, unrelated]);

        Assert.False(WorkspaceService.ResolvesCorlib(project));
    }

    [Fact]
    public void FailedConventionalCandidateStillTriesUnconventionalCoreReference()
    {
        using var workspace = new AdhocWorkspace();
        var fakeCore = MetadataReference.CreateFromImage(
            File.ReadAllBytes(typeof(FrameworkReferenceProbeTests).Assembly.Location), filePath: "mscorlib.dll");
        var actualCore = MetadataReference.CreateFromImage(
            File.ReadAllBytes(typeof(object).Assembly.Location), filePath: "custom-core.dll");
        var project = workspace.AddProject("App", LanguageNames.CSharp)
            .AddMetadataReferences([fakeCore, actualCore]);

        Assert.True(WorkspaceService.ResolvesCorlib(project));
    }

    private sealed class UnreadableReference(string path) : PortableExecutableReference(default, path)
    {
        public int ReadCount { get; private set; }
        protected override DocumentationProvider CreateDocumentationProvider() => DocumentationProvider.Default;
        protected override Metadata GetMetadataImpl()
        {
            ReadCount++;
            throw new IOException("This unrelated assembly should remain deferred.");
        }
        protected override PortableExecutableReference WithPropertiesImpl(MetadataReferenceProperties properties) =>
            throw new NotSupportedException();
    }
}
