using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;

namespace RoslynMCP.Services;

/// <summary>Defers reading assembly images until a compilation observes their metadata.</summary>
/// <remarks>
/// MSBuild constructs metadata references before replacing project outputs with source project
/// references. The default service opens and copies every image during that conversion, including
/// references that no compilation ever uses. Like Visual Studio's metadata references, these
/// references capture the image on first observation. Once observed, that immutable image (or
/// opening failure) remains attached to the reference and its aliases. A later workspace gets a
/// fresh cache and can observe rebuilt files.
/// </remarks>
[ExportWorkspaceServiceFactory(typeof(IMetadataService), ServiceLayer.Host), Shared]
internal sealed class LazyMetadataServiceFactory : IWorkspaceServiceFactory
{
    [ImportingConstructor]
    public LazyMetadataServiceFactory()
    {
    }

    public IWorkspaceService CreateService(HostWorkspaceServices workspaceServices) =>
        new LazyMetadataService(workspaceServices.GetRequiredService<IDocumentationProviderService>());
}

internal sealed class LazyMetadataService : IMetadataService
{
    private readonly MetadataReferenceCache _references;

    public LazyMetadataService(IDocumentationProviderService documentation)
    {
        // Retain Roslyn's weak, per-workspace cache and per-path synchronization. In particular,
        // do not reuse an image from an evicted workspace after its DLL has been rebuilt.
        _references = new MetadataReferenceCache((path, properties) =>
            new DeferredReference(path, properties, documentation.GetDocumentationProvider(path)));
    }

    public PortableExecutableReference GetReference(string resolvedPath, MetadataReferenceProperties properties) =>
        (PortableExecutableReference)_references.GetReference(resolvedPath, properties);

    private sealed class DeferredReference : PortableExecutableReference
    {
        private readonly Lazy<PortableExecutableReference> _image;
        private readonly Lazy<Metadata> _metadata;
        private readonly DocumentationProvider _documentation;

        public DeferredReference(string path, MetadataReferenceProperties properties, DocumentationProvider documentation)
            : this(path, properties, documentation, new Lazy<PortableExecutableReference>(
                () => MetadataReference.CreateFromFile(path, properties, documentation),
                LazyThreadSafetyMode.ExecutionAndPublication))
        {
        }

        private DeferredReference(string path, MetadataReferenceProperties properties,
            DocumentationProvider documentation, Lazy<PortableExecutableReference> image,
            Lazy<Metadata>? metadata = null)
            : base(properties, path, documentation)
        {
            _image = image;
            _metadata = metadata ?? new Lazy<Metadata>(() => image.Value.GetMetadata(),
                LazyThreadSafetyMode.ExecutionAndPublication);
            _documentation = documentation;
        }

        // The original Roslyn reference owns the image. GetMetadata returns a shallow copy of
        // that same immutable metadata. Keep one copy so its additional-module factory and
        // decoded assembly are also shared, rather than reopening netmodules on every bind.
        // The default eager reader closes the manifest DLL handle after the first read;
        // additional netmodules retain Roslyn's ordinary lazy-loading behavior.
        protected override Metadata GetMetadataImpl() => _metadata.Value;

        protected override DocumentationProvider CreateDocumentationProvider() => _documentation;

        protected override PortableExecutableReference WithPropertiesImpl(MetadataReferenceProperties properties) =>
            new DeferredReference(FilePath!, properties, _documentation, _image, _metadata);
    }
}
