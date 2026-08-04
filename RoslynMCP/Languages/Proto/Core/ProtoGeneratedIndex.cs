using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Proto.Core;

/// <summary>The <c>.proto</c> declaration a generated C# symbol came from.</summary>
/// <param name="FilePath">The absolute path of the <c>.proto</c>.</param>
/// <param name="FullName">The fully-qualified proto name, which
/// <see cref="ProtoFile.FindByFullName"/> turns back into a declaration with a span once the
/// caller has parsed the file.</param>
/// <remarks>
/// A name and a path rather than a <see cref="ProtoDeclaration"/>, because the index parses the
/// files on disk and the caller is usually looking at an editor buffer. Handing back the index's
/// own declaration objects would give the caller spans measured against text it is not showing.
/// </remarks>
internal readonly record struct ProtoDeclarationRef(
    string FilePath,
    string FullName,
    ProtoDeclarationKind Kind);

/// <summary>
/// The bridge between one project's <c>.proto</c> files and the C# that <c>protoc</c> generated
/// from them: which documents came from which <c>.proto</c>, and which <see cref="ISymbol"/>
/// stands for each proto declaration — in both directions.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the markup packs there is no projection here. <c>Grpc.Tools</c> writes real
/// <c>.cs</c> files and adds them as ordinary <c>Compile</c> items, so Roslyn has already bound
/// them and every navigation feature reduces to plain <c>SymbolFinder</c> once the proto
/// declaration has been turned into a symbol. Turning it into a symbol is the only hard part,
/// and it is what this type does.
/// </para>
/// <para>
/// Nothing here reproduces protoc's naming rules. <c>widget_id</c> becoming <c>WidgetId</c>,
/// <c>CHANNEL_ALPHA</c> becoming <c>Alpha</c> and a package becoming a namespace are all
/// conventions that change between protoc releases, differ per plugin and are overridable per
/// file; a binder built on them silently points at the wrong member the day one of them moves.
/// Instead every binding reads an anchor protoc left in its own output — the source header, the
/// descriptor index expression, the <c>…FieldNumber</c> constant, the <c>OriginalName</c>
/// attribute and <c>__ServiceName</c> — so the C# name is discovered rather than predicted.
/// </para>
/// <para>
/// Lookups are keyed by fully-qualified proto name because that name is unique across a
/// descriptor pool by protobuf's own rule, which means a caller can ask about a declaration it
/// parsed itself without the index having to hand out — or recognise — declaration instances.
/// </para>
/// </remarks>
internal sealed class ProtoGeneratedIndex
{
    /// <summary>The index of a project that has no <c>.proto</c> files or has never been built.</summary>
    public static readonly ProtoGeneratedIndex Empty = new();

    private readonly Dictionary<string, ImmutableArray<Document>> _documents =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, INamedTypeSymbol> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, INamedTypeSymbol> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServiceBinding> _services = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPropertySymbol> _properties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IFieldSymbol> _enumMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RpcBinding> _rpcs = new(StringComparer.Ordinal);

    private readonly Dictionary<ISymbol, ProtoDeclarationRef> _reverse =
        new(SymbolEqualityComparer.Default);

    private readonly HashSet<DocumentId> _generated = [];

    private readonly HashSet<string> _generatedPaths = new(StringComparer.OrdinalIgnoreCase);

    private ImmutableArray<string> _protoFiles = [];
    private ImmutableArray<string> _compiledProtoFiles = [];

    private ProtoGeneratedIndex()
    {
    }

    /// <summary>Whether the project produced no generated output at all — the never-built case,
    /// in which every lookup below returns <c>null</c>.</summary>
    public bool IsEmpty => _documents.Count == 0;

    /// <summary>Every <c>.proto</c> the index looked at, absolute and normalised. This is what was
    /// found, not what the project compiles: a file sitting under the project directory is in here
    /// whether or not anything ever generated from it.</summary>
    public ImmutableArray<string> ProtoFiles => _protoFiles;

    /// <summary>
    /// The <c>.proto</c> files a generated document names in its header, which is the set the
    /// project provably compiles.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="ProtoFiles"/> and derived differently: this one is protoc's own
    /// record of what it was asked to build, so it excludes a <c>.proto</c> that merely sits in the
    /// tree and includes one linked in from outside it. Empty until the project has been built.
    /// </remarks>
    public ImmutableArray<string> CompiledProtoFiles => _compiledProtoFiles;

    /// <summary>
    /// The generated documents built from one <c>.proto</c>, in path order. There is usually more
    /// than one: <c>Grpc.Tools</c> writes the messages and the gRPC stubs into separate files.
    /// </summary>
    public ImmutableArray<Document> DocumentsFor(string protoAbsolutePath) =>
        _documents.TryGetValue(ProtoDocumentService.Normalize(protoAbsolutePath), out var found) ? found : [];

    /// <summary>
    /// Whether protoc wrote this document, and it is therefore a pass-through rather than a place
    /// anybody should be sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A set lookup against what the scan already decided, not a fresh look at the file. The path
    /// cannot answer it — <c>Protobuf_OutputPath</c> points wherever the build points it, and
    /// generated code is checked in beside the schema as often as it is left in <c>obj</c> — and
    /// re-reading the header per call would put a file read on every result of every search.
    /// </para>
    /// <para>
    /// Membership is protoc's own <c>// source:</c> line and nothing else, so a document whose
    /// <c>.proto</c> was never found on disk still counts: failing to match the header says
    /// something about this index, not about who wrote the file.
    /// </para>
    /// </remarks>
    public bool IsGenerated(Document document) => _generated.Contains(document.Id);

    /// <summary>
    /// The same answer for a caller holding a path rather than a document.
    /// </summary>
    /// <remarks>
    /// One recording read two ways, so the two overloads cannot disagree about a file — which
    /// matters because find-usages drops a generated document by the first and go-to-definition
    /// drops a generated location by the second, and a file only one of them recognised would be
    /// hidden from one feature and offered by the other. The path is normalised on the way in
    /// because the set was: a workspace document and a URI round-trip spell the same file
    /// differently.
    /// </remarks>
    public bool IsGenerated(string filePath) =>
        _generatedPaths.Contains(ProtoDocumentService.Normalize(filePath));

    public INamedTypeSymbol? TypeFor(ProtoMessage message) => Lookup(_messages, message.FullName);

    public INamedTypeSymbol? TypeFor(ProtoEnum @enum) => Lookup(_enums, @enum.FullName);

    /// <summary>The static class protoc's gRPC plugin names after the service, which holds the
    /// base and the client as nested types.</summary>
    public INamedTypeSymbol? ServiceTypeFor(ProtoService service) =>
        Binding(service)?.Type;

    /// <summary>The abstract class a server-side implementation derives from — the type
    /// find-implementations searches for to reach the hand-written service.</summary>
    public INamedTypeSymbol? ServiceBaseFor(ProtoService service) => Binding(service)?.Base;

    public INamedTypeSymbol? ServiceClientFor(ProtoService service) => Binding(service)?.Client;

    public IPropertySymbol? PropertyFor(ProtoField field) => Lookup(_properties, field.FullName);

    public IFieldSymbol? MemberFor(ProtoEnumValue value) => Lookup(_enumMembers, value.FullName);

    /// <summary>The virtual method on the service base that an implementation overrides.</summary>
    public IMethodSymbol? BaseMethodFor(ProtoRpc rpc) => Binding(rpc)?.BaseMethod;

    /// <summary>
    /// The client's blocking call for a unary rpc, or its only call for a streaming one.
    /// </summary>
    /// <remarks>
    /// One of two overloads — the plugin emits a <c>CallOptions</c> form beside the
    /// <c>headers</c>/<c>deadline</c>/<c>cancellationToken</c> form, and this is the first
    /// declared. A caller looking for every call site wants <see cref="MethodsFor"/>, which
    /// carries both.
    /// </remarks>
    public IMethodSymbol? ClientMethodFor(ProtoRpc rpc) => Binding(rpc)?.ClientMethod;

    /// <summary>The client's <c>…Async</c> call, which the plugin emits only where it also emits
    /// a blocking one; <c>null</c> for a streaming rpc, whose single call is already async.</summary>
    public IMethodSymbol? ClientAsyncMethodFor(ProtoRpc rpc) => Binding(rpc)?.ClientAsyncMethod;

    /// <summary>Every generated method that stands for one rpc: the base's virtual method and all
    /// of the client's overloads. This is the set find-references has to search, because a call
    /// picks whichever overload its argument list fits.</summary>
    public ImmutableArray<IMethodSymbol> MethodsFor(ProtoRpc rpc) =>
        Binding(rpc) is { } binding ? binding.All : [];

    /// <summary>
    /// The proto declaration a generated symbol was generated from, or <c>null</c> when the
    /// symbol is not generated code.
    /// </summary>
    /// <param name="symbol">The symbol under the caret.</param>
    /// <param name="includeInherited">
    /// Also answer for a hand-written override or subclass. A caret on
    /// <c>public override Task&lt;Reply&gt; GetWidgetsById(…)</c> in a service implementation is
    /// on the rpc as far as the user is concerned, but the symbol there is the override and not
    /// the generated virtual, so the exact map cannot see it.
    /// </param>
    public ProtoDeclarationRef? DeclarationFor(ISymbol symbol, bool includeInherited = false)
    {
        var definition = symbol.OriginalDefinition;

        if (_reverse.TryGetValue(definition, out var found))
            return found;

        if (!includeInherited)
            return null;

        // Deliberately not the containing type's hierarchy for a method: every member of a
        // service implementation is inside a class deriving from the generated base, and walking
        // out to it would answer "the service" for a private helper that has nothing to do with
        // any rpc.
        for (var method = (definition as IMethodSymbol)?.OverriddenMethod;
             method is not null;
             method = method.OverriddenMethod)
        {
            if (_reverse.TryGetValue(method.OriginalDefinition, out found))
                return found;
        }

        for (var type = (definition as INamedTypeSymbol)?.BaseType;
             type is not null;
             type = type.BaseType)
        {
            if (_reverse.TryGetValue(type.OriginalDefinition, out found))
                return found;
        }

        return null;
    }

    private ServiceBinding? Binding(ProtoService service) =>
        _services.TryGetValue(service.FullName, out var found) ? found : null;

    private RpcBinding? Binding(ProtoRpc rpc) =>
        _rpcs.TryGetValue(rpc.FullName, out var found) ? found : null;

    private static TSymbol? Lookup<TSymbol>(Dictionary<string, TSymbol> map, string fullName)
        where TSymbol : class, ISymbol =>
        fullName.Length > 0 && map.TryGetValue(fullName, out var found) ? found : null;

    private sealed record ServiceBinding(
        INamedTypeSymbol Type,
        INamedTypeSymbol? Base,
        INamedTypeSymbol? Client);

    private sealed record RpcBinding(
        IMethodSymbol? BaseMethod,
        IMethodSymbol? ClientMethod,
        IMethodSymbol? ClientAsyncMethod,
        ImmutableArray<IMethodSymbol> All);

    // ---- Entry point -----------------------------------------------------------------------

    private sealed record ScanCacheEntry(Compilation Compilation, Dictionary<string, GeneratedProto> Scan);

    private static readonly ConcurrentDictionary<ProjectId, ScanCacheEntry> s_scans = new();

    private sealed record IndexCacheEntry(
        Compilation Compilation, string Fingerprint, ProtoGeneratedIndex Index);

    private static readonly ConcurrentDictionary<ProjectId, IndexCacheEntry> s_indexes = new();

    /// <summary>
    /// The index for one project, built once per compilation and reused after.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two caches, not one. The generated half is keyed on the compilation alone — it can only
    /// change when a build rewrites the generated files, which is exactly what produces a new
    /// compilation — while the index around it also watches the <c>.proto</c> text, because a
    /// proto edit changes the names the caller will ask about without changing the compilation
    /// at all. Splitting them keeps a keystroke in a <c>.proto</c> to a parse of a few small
    /// files instead of a re-walk of every generated syntax tree.
    /// </para>
    /// <para>
    /// Compilations are snapshots, so reference equality is the correct staleness test.
    /// </para>
    /// </remarks>
    public static async Task<ProtoGeneratedIndex> GetAsync(Project project, CancellationToken ct)
    {
        if (project.Language != LanguageNames.CSharp || project.FilePath is not { Length: > 0 })
            return Empty;

        // The gate that keeps this cheap for the rest of the solution: a project with no protos
        // never reads a document, and most projects have no protos.
        var protoPaths = EnumerateProtoFiles(project);
        if (protoPaths.Count == 0)
            return Empty;

        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null)
            return Empty;

        string fingerprint = Fingerprint(protoPaths);

        if (s_indexes.TryGetValue(project.Id, out var cachedIndex)
            && ReferenceEquals(cachedIndex.Compilation, compilation)
            && string.Equals(cachedIndex.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return cachedIndex.Index;
        }

        Dictionary<string, GeneratedProto> scan;
        if (s_scans.TryGetValue(project.Id, out var cachedScan)
            && ReferenceEquals(cachedScan.Compilation, compilation))
        {
            scan = cachedScan.Scan;
        }
        else
        {
            scan = await ScanAsync(project, compilation, ct);
            s_scans[project.Id] = new ScanCacheEntry(compilation, scan);
        }

        var index = Build(protoPaths, scan, ct);
        s_indexes[project.Id] = new IndexCacheEntry(compilation, fingerprint, index);
        return index;
    }

    /// <summary>
    /// Whether any index already built calls this path protoc's output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a caller asks when it holds a path and no project to resolve it against — a location
    /// already converted for the wire. Deliberately a read of the cache and nothing else: it runs
    /// once per candidate location on every go-to-definition in the solution, so building an index
    /// here would put a directory walk and a compilation behind a question that is mostly asked
    /// about files with no connection to protobuf.
    /// </para>
    /// <para>
    /// That makes the answer scoped to what has been looked at, which is the correct scope rather
    /// than a limitation: the caller is asking because a contribution was just made, and making it
    /// is what built the index that knows. A solution with no protobuf has built none, and pays one
    /// test on an empty dictionary.
    /// </para>
    /// </remarks>
    public static bool IsKnownGenerated(string filePath)
    {
        if (s_indexes.IsEmpty)
            return false;

        foreach (var entry in s_indexes.Values)
        {
            if (entry.Index.IsGenerated(filePath))
                return true;
        }

        return false;
    }

    // ---- The .proto side --------------------------------------------------------------------

    private sealed record FileListEntry(DateTime StampUtc, IReadOnlyList<string> Files);

    private static readonly ConcurrentDictionary<string, FileListEntry> s_protoFiles =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan FileListLifetime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Every <c>.proto</c> that could belong to the project — the candidates a generated document's
    /// header is matched against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three sources, because no one of them is complete. The directory walk is the cheap answer
    /// and covers the ordinary layout. The <c>Protobuf</c> items are what covers a file linked in
    /// from outside the project directory, which the walk cannot see and which the binder would
    /// otherwise drop on the floor: the generated code exists and names the file, but with no
    /// candidate to match the header against, nothing in it binds. The workspace's additional files
    /// cover a host that registered the <c>.proto</c> itself.
    /// </para>
    /// <para>
    /// Output directories are skipped. <c>Grpc.Tools</c> copies imported protos out of packages
    /// into <c>obj</c>, and letting those match would give every declaration a second answer in a
    /// file the user cannot meaningfully edit.
    /// </para>
    /// <para>
    /// Keyed on the project file rather than its directory — two <c>.csproj</c> in one folder is
    /// legal, and they need not declare the same protos — and re-taken periodically rather than per
    /// call, since the alternative is walking a large project's tree on every navigation.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> EnumerateProtoFiles(Project project)
    {
        string projectPath = project.FilePath ?? string.Empty;

        if (s_protoFiles.TryGetValue(projectPath, out var cached)
            && DateTime.UtcNow - cached.StampUtc < FileListLifetime)
        {
            return cached.Files;
        }

        var files = new List<string>();

        if (Path.GetDirectoryName(projectPath) is { Length: > 0 } projectDir && Directory.Exists(projectDir))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(projectDir, "*.proto", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(projectDir, file);
                    string first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                    if (first.Equals("obj", StringComparison.OrdinalIgnoreCase)
                        || first.Equals("bin", StringComparison.OrdinalIgnoreCase))
                        continue;

                    files.Add(ProtoDocumentService.Normalize(file));
                }
            }
            catch (IOException)
            {
                // A directory vanished mid-walk; report what was found.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var extra = ProtoWorkspace.DeclaredProtoFiles(projectPath)
            .Concat(project.AdditionalDocuments
                .Select(document => document.FilePath)
                .OfType<string>()
                .Where(ProtoDocumentService.IsProtoFile)
                .Select(ProtoDocumentService.Normalize));

        foreach (string file in extra)
        {
            if (!files.Contains(file, StringComparer.OrdinalIgnoreCase))
                files.Add(file);
        }

        s_protoFiles[projectPath] = new FileListEntry(DateTime.UtcNow, files);
        return files;
    }

    /// <summary>What has to change before the bindings are re-derived.</summary>
    private static string Fingerprint(IReadOnlyList<string> protoPaths)
    {
        var sb = new StringBuilder();

        foreach (string path in protoPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(path).Append('#');

            // The buffer's checksum rather than its identity: a SourceText memoizes its own
            // checksum, so this costs a hash lookup on the array the editor already computed, and
            // it cannot collide two different buffers into one entry the way a hash code can.
            if (OpenDocumentStore.TryGet(path, out var open))
            {
                sb.Append('o').Append(Convert.ToHexString(open.GetChecksum().AsSpan()));
            }
            else
            {
                try
                {
                    var info = new FileInfo(path);
                    sb.Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append(':')
                      .Append(info.Exists ? info.Length : 0);
                }
                catch (IOException)
                {
                    sb.Append('?');
                }
                catch (UnauthorizedAccessException)
                {
                    sb.Append('?');
                }
            }

            sb.Append(';');
        }

        return sb.ToString();
    }

    // ---- The generated side -----------------------------------------------------------------

    /// <summary>One generated message class and the field properties it declares.</summary>
    /// <param name="Chain">The descriptor index path read off the class's <c>Descriptor</c>
    /// property: <c>[0]</c> for the first top-level message, <c>[0, 2]</c> for the third type
    /// nested in it.</param>
    private sealed record GeneratedMessage(
        ImmutableArray<int> Chain,
        INamedTypeSymbol Type,
        ImmutableDictionary<int, IPropertySymbol> PropertiesByFieldNumber);

    private sealed record GeneratedEnum(
        INamedTypeSymbol Type,
        ImmutableArray<(string? OriginalName, IFieldSymbol Symbol)> Members);

    /// <param name="ServiceName">The fully-qualified proto service name, verbatim from
    /// <c>__ServiceName</c>.</param>
    /// <param name="MemberNamesByRpc">Proto rpc name to the C# member name the plugin derived
    /// from it, read off the <c>__Method_…</c> fields.</param>
    private sealed record GeneratedService(
        string ServiceName,
        INamedTypeSymbol Type,
        INamedTypeSymbol? Base,
        INamedTypeSymbol? Client,
        ImmutableDictionary<string, string> MemberNamesByRpc);

    /// <summary>Everything generated from one <c>.proto</c>, gathered across its documents.</summary>
    private sealed class GeneratedProto
    {
        public readonly List<Document> Documents = [];
        public readonly List<GeneratedMessage> Messages = [];
        public readonly List<GeneratedEnum> Enums = [];
        public readonly List<GeneratedService> Services = [];
    }

    /// <summary>How far into a document the protoc header is looked for.</summary>
    private const int HeaderCharLimit = 1024;

    /// <summary>
    /// Walks the project's generated documents and records the shapes protoc left in them,
    /// keyed by the <c>.proto</c> path in each document's header.
    /// </summary>
    /// <remarks>
    /// Build output is tried first and everything else only if that found nothing, because
    /// <c>Protobuf_OutputPath</c> can point anywhere and a project that has protos but no
    /// <c>obj</c> output is either configured that way or has never been built. Every candidate
    /// is decided on its first kilobyte: parsing a document to find out it is not generated code
    /// would cost the whole project on every compilation.
    /// </remarks>
    private static async Task<Dictionary<string, GeneratedProto>> ScanAsync(
        Project project, Compilation compilation, CancellationToken ct)
    {
        var documents = new List<(Document Document, string Path)>();

        foreach (var document in project.Documents)
        {
            if (document.FilePath is { Length: > 0 } path)
                documents.Add((document, path));
        }

        documents.Sort((left, right) =>
            string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));

        var scan = new Dictionary<string, GeneratedProto>(StringComparer.OrdinalIgnoreCase);

        await ScanDocumentsAsync(
            documents.Where(entry => IsBuildOutput(entry.Path)).Select(entry => entry.Document),
            compilation, scan, ct);

        if (scan.Count == 0)
        {
            await ScanDocumentsAsync(
                documents.Where(entry => !IsBuildOutput(entry.Path)).Select(entry => entry.Document),
                compilation, scan, ct);
        }

        return scan;
    }

    private static async Task ScanDocumentsAsync(
        IEnumerable<Document> documents,
        Compilation compilation,
        Dictionary<string, GeneratedProto> scan,
        CancellationToken ct)
    {
        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();

            var text = await document.GetTextAsync(ct);
            if (HeaderSourcePath(text) is not { } source)
                continue;

            var tree = await document.GetSyntaxTreeAsync(ct);

            // A tree the compilation does not own has no semantic model to ask, which happens
            // when the caller handed over a project from a different solution snapshot.
            if (tree is null || !compilation.ContainsSyntaxTree(tree))
                continue;

            if (!scan.TryGetValue(source, out var generated))
                scan[source] = generated = new GeneratedProto();

            generated.Documents.Add(document);

            var model = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(ct);

            foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                ct.ThrowIfCancellationRequested();

                switch (declaration)
                {
                    case ClassDeclarationSyntax classDeclaration:
                        ReadClass(classDeclaration, model, generated, ct);
                        break;

                    case EnumDeclarationSyntax enumDeclaration
                        when ReadEnum(enumDeclaration, model, ct) is { } @enum:
                        generated.Enums.Add(@enum);
                        break;
                }
            }
        }
    }

    private static void ReadClass(
        ClassDeclarationSyntax declaration,
        SemanticModel model,
        GeneratedProto generated,
        CancellationToken ct)
    {
        if (DescriptorChain(declaration) is { Indices.IsDefaultOrEmpty: false } anchor)
        {
            if (AbsoluteChain(anchor, model, generated, ct) is { IsDefaultOrEmpty: false } chain
                && model.GetDeclaredSymbol(declaration, ct) is { } type)
            {
                generated.Messages.Add(new GeneratedMessage(chain, type, FieldProperties(declaration, model, ct)));
            }

            return;
        }

        if (ServiceName(declaration) is not { } serviceName)
            return;

        if (model.GetDeclaredSymbol(declaration, ct) is not { } serviceType)
            return;

        INamedTypeSymbol? serviceBase = null;
        INamedTypeSymbol? client = null;

        foreach (var nested in declaration.Members.OfType<ClassDeclarationSyntax>())
        {
            if (nested.Modifiers.Any(SyntaxKind.AbstractKeyword))
                serviceBase ??= model.GetDeclaredSymbol(nested, ct);
            else if (DerivesFromClientBase(nested))
                client ??= model.GetDeclaredSymbol(nested, ct);
        }

        generated.Services.Add(new GeneratedService(
            serviceName, serviceType, serviceBase, client, RpcMemberNames(declaration)));
    }

    /// <summary>
    /// The anchor's indices resolved to a path from the file, by prepending the chain of the
    /// message class it hung them off.
    /// </summary>
    /// <remarks>
    /// The parent is always already read: protoc declares a message class before the
    /// <c>Types</c> container holding the ones nested in it, and the scan walks the file in
    /// document order. A parent that is nonetheless missing means its own anchor was unreadable,
    /// and binding a child against a path that does not start where it claims would be worse than
    /// leaving it unbound.
    /// </remarks>
    private static ImmutableArray<int> AbsoluteChain(
        DescriptorAnchor anchor,
        SemanticModel model,
        GeneratedProto generated,
        CancellationToken ct)
    {
        if (anchor.Parent is null)
            return anchor.Indices;

        if (model.GetSymbolInfo(anchor.Parent, ct).Symbol is not INamedTypeSymbol parent)
            return default;

        var owner = generated.Messages
            .FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(candidate.Type, parent));

        return owner is null ? default : owner.Chain.AddRange(anchor.Indices);
    }

    /// <summary>
    /// The <c>.proto</c> a generated document was written from, as protoc recorded it in the
    /// header — <c>//     source: widgets/widgets.proto</c>.
    /// </summary>
    /// <remarks>
    /// The path is relative to whichever <c>--proto_path</c> root protoc was given, and the
    /// header does not say which root that was; matching it to a file on disk is therefore a
    /// suffix test rather than a comparison. Only the first kilobyte is looked at, and the scan
    /// stops at the first line that is not a comment: protoc's header is the first thing in the
    /// file, so anything further in is a coincidence rather than a header.
    /// </remarks>
    private static string? HeaderSourcePath(SourceText text)
    {
        string head = text.ToString(TextSpan.FromBounds(0, Math.Min(text.Length, HeaderCharLimit)));

        foreach (var line in head.Split('\n'))
        {
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty)
                continue;

            if (!trimmed.StartsWith("//", StringComparison.Ordinal))
                return null;

            var body = trimmed[2..].Trim();
            if (!body.StartsWith("source:", StringComparison.Ordinal))
                continue;

            var path = body["source:".Length..].Trim();
            return path.IsEmpty ? null : path.ToString().Replace('\\', '/');
        }

        return null;
    }

    /// <summary>
    /// The descriptor index path a generated message class reports through its <c>Descriptor</c>
    /// property, or the default when the class is not a message.
    /// </summary>
    /// <remarks>
    /// <c>Descriptor.MessageTypes[0].NestedTypes[2]</c> is protoc pointing at its own declaration
    /// of the type, which makes the expression the one part of the generated file that says where
    /// a class came from without naming it. The class also carries an explicit
    /// <c>pb::IMessage.Descriptor</c> that forwards to this one; that one yields no chain and is
    /// skipped by the same test.
    /// </remarks>
    private static DescriptorAnchor DescriptorChain(ClassDeclarationSyntax declaration)
    {
        foreach (var property in declaration.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (property.Identifier.ValueText != "Descriptor")
                continue;

            if (ReturnedExpression(property) is not { } expression)
                continue;

            var anchor = IndexChain(expression);
            if (!anchor.Indices.IsDefaultOrEmpty)
                return anchor;
        }

        return default;
    }

    /// <summary>
    /// A <c>Descriptor</c> expression taken apart: the indices it applies, and the message class
    /// those indices are relative to when it did not spell the path out from the file.
    /// </summary>
    /// <param name="Parent">The expression naming the message class whose <c>Descriptor</c> the
    /// indices hang off, or <see langword="null"/> when the walk reached the file's own
    /// <c>MessageTypes</c> and the indices are therefore already absolute.</param>
    private readonly record struct DescriptorAnchor(ImmutableArray<int> Indices, ExpressionSyntax? Parent);

    private static ExpressionSyntax? ReturnedExpression(PropertyDeclarationSyntax property)
    {
        if (property.ExpressionBody is { } arrow)
            return arrow.Expression;

        var getter = property.AccessorList?.Accessors
            .FirstOrDefault(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration));

        if (getter?.ExpressionBody is { } getterArrow)
            return getterArrow.Expression;

        return getter?.Body?.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression;
    }

    private static DescriptorAnchor IndexChain(ExpressionSyntax expression)
    {
        var indices = new List<int>();

        for (var current = expression; current is ElementAccessExpressionSyntax access;)
        {
            if (access.ArgumentList.Arguments is not [{ Expression: LiteralExpressionSyntax { Token.Value: int index } }])
                break;

            if (access.Expression is not MemberAccessExpressionSyntax member)
                break;

            string name = member.Name.Identifier.ValueText;
            if (name is not ("MessageTypes" or "NestedTypes"))
                break;

            indices.Add(index);

            // MessageTypes is the file's own list, so it terminates the walk outward; the
            // indices came off innermost-first and the caller reads them the other way.
            if (name == "MessageTypes")
            {
                indices.Reverse();
                return new DescriptorAnchor([.. indices], Parent: null);
            }

            current = member.Expression;

            // A nested message does not spell its path out from the file: protoc hangs it off the
            // containing message's own property — Widget.Descriptor.NestedTypes[1] — so the walk
            // outward ends on a class rather than on MessageTypes. The indices gathered so far are
            // relative to whatever that class resolves to.
            if (current is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Descriptor" } parent)
            {
                indices.Reverse();
                return new DescriptorAnchor([.. indices], parent.Expression);
            }
        }

        return default;
    }

    /// <summary>
    /// Each field property of a generated message, by the proto field number the constant beside
    /// it carries.
    /// </summary>
    /// <remarks>
    /// The number, not the name, because the number is the field's identity: renaming a field is
    /// a source-compatible change to a proto and renumbering one is a wire break, so a binding
    /// made on the name would move to the wrong property exactly when the proto was edited
    /// safely. The constant is named after the property protoc emitted for the same field, which
    /// is what joins the two without knowing how the proto name became a C# name.
    /// </remarks>
    private static ImmutableDictionary<int, IPropertySymbol> FieldProperties(
        ClassDeclarationSyntax declaration, SemanticModel model, CancellationToken ct)
    {
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var field in declaration.Members.OfType<FieldDeclarationSyntax>())
        {
            if (!field.Modifiers.Any(SyntaxKind.ConstKeyword))
                continue;

            foreach (var variable in field.Declaration.Variables)
            {
                string name = variable.Identifier.ValueText;
                if (!name.EndsWith("FieldNumber", StringComparison.Ordinal))
                    continue;

                if (variable.Initializer?.Value is LiteralExpressionSyntax { Token.Value: int number })
                    numbers[name[..^"FieldNumber".Length]] = number;
            }
        }

        if (numbers.Count == 0)
            return ImmutableDictionary<int, IPropertySymbol>.Empty;

        var properties = ImmutableDictionary.CreateBuilder<int, IPropertySymbol>();

        foreach (var property in declaration.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (!numbers.TryGetValue(property.Identifier.ValueText, out int number))
                continue;

            if (model.GetDeclaredSymbol(property, ct) is { } symbol)
                properties[number] = symbol;
        }

        return properties.ToImmutable();
    }

    /// <summary>
    /// A generated enum and the proto name each of its members was written under, or <c>null</c>
    /// when the enum is not one protoc generated from an <c>enum</c> declaration.
    /// </summary>
    /// <remarks>
    /// The <c>OriginalName</c> attribute is what makes an enum recognisable at all: message
    /// classes announce themselves through their descriptor but a generated enum has no
    /// descriptor of its own, so the attributes on its members are the only mark it carries. That
    /// is also what keeps the oneof-case enums out — <c>ImageOneofCase</c> is nested in the same
    /// class, is shaped identically and stands for no proto declaration.
    /// </remarks>
    private static GeneratedEnum? ReadEnum(
        EnumDeclarationSyntax declaration, SemanticModel model, CancellationToken ct)
    {
        var members = ImmutableArray.CreateBuilder<(string?, IFieldSymbol)>();
        bool named = false;

        foreach (var member in declaration.Members)
        {
            if (model.GetDeclaredSymbol(member, ct) is not { } symbol)
                continue;

            string? original = OriginalName(member);
            named |= original is not null;
            members.Add((original, symbol));
        }

        if (!named || model.GetDeclaredSymbol(declaration, ct) is not { } type)
            return null;

        return new GeneratedEnum(type, members.ToImmutable());
    }

    private static string? OriginalName(EnumMemberDeclarationSyntax member)
    {
        foreach (var list in member.AttributeLists)
        {
            foreach (var attribute in list.Attributes)
            {
                if (AttributeName(attribute) != "OriginalName")
                    continue;

                foreach (var argument in attribute.ArgumentList?.Arguments ?? default)
                {
                    if (argument.NameEquals is null
                        && argument.Expression is LiteralExpressionSyntax { Token.Value: string name })
                        return name;
                }
            }
        }

        return null;
    }

    private static string AttributeName(AttributeSyntax attribute)
    {
        string name = SimpleName(attribute.Name);
        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^"Attribute".Length]
            : name;
    }

    /// <summary>The last identifier of a name, whatever it is qualified or aliased with —
    /// generated code writes <c>pbr::OriginalName</c> and <c>grpc::ClientBase&lt;T&gt;</c>.</summary>
    private static string SimpleName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax aliased => aliased.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    /// <summary>The fully-qualified proto service name a generated service class states, or
    /// <c>null</c> when the class is not one.</summary>
    private static string? ServiceName(ClassDeclarationSyntax declaration)
    {
        foreach (var field in declaration.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in field.Declaration.Variables)
            {
                if (variable.Identifier.ValueText == "__ServiceName"
                    && variable.Initializer?.Value is LiteralExpressionSyntax { Token.Value: string name })
                    return name;
            }
        }

        return null;
    }

    /// <summary>
    /// The C# member name the gRPC plugin gave each rpc, read off the <c>__Method_…</c> fields.
    /// </summary>
    /// <remarks>
    /// The field's initializer passes the rpc's proto name as the only string literal in it —
    /// <c>new grpc::Method&lt;…&gt;(grpc::MethodType.Unary, __ServiceName, "GetWidgetsById", …)</c>
    /// — while the field's own suffix is the C# name every generated member for that rpc is built
    /// from. One field therefore states both halves of the mapping.
    /// </remarks>
    private static ImmutableDictionary<string, string> RpcMemberNames(ClassDeclarationSyntax declaration)
    {
        const string prefix = "__Method_";
        var names = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var field in declaration.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in field.Declaration.Variables)
            {
                string name = variable.Identifier.ValueText;
                if (!name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                string member = name[prefix.Length..];
                if (member.Length == 0)
                    continue;

                string? rpc = variable.Initializer?.Value is ObjectCreationExpressionSyntax { ArgumentList: { } arguments }
                    ? arguments.Arguments
                        .Select(argument => argument.Expression)
                        .OfType<LiteralExpressionSyntax>()
                        .Select(literal => literal.Token.Value as string)
                        .FirstOrDefault(value => value is { Length: > 0 })
                    : null;

                names[rpc ?? member] = member;
            }
        }

        return names.ToImmutable();
    }

    private static bool DerivesFromClientBase(ClassDeclarationSyntax declaration) =>
        declaration.BaseList?.Types.Any(type => SimpleName(type.Type) == "ClientBase") == true;

    private static bool IsBuildOutput(string filePath) =>
        HasDirectorySegment(filePath, "obj") || HasDirectorySegment(filePath, "bin");

    private static bool HasDirectorySegment(string path, string segment)
    {
        int index = 0;

        while ((index = path.IndexOf(segment, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = index + segment.Length;
            bool startsSegment = index == 0 || path[index - 1] is '/' or '\\';
            bool endsSegment = end < path.Length && path[end] is '/' or '\\';

            if (startsSegment && endsSegment)
                return true;

            index = end;
        }

        return false;
    }

    // ---- Binding ----------------------------------------------------------------------------

    private static ProtoGeneratedIndex Build(
        IReadOnlyList<string> protoPaths,
        Dictionary<string, GeneratedProto> scan,
        CancellationToken ct)
    {
        var index = new ProtoGeneratedIndex { _protoFiles = [.. protoPaths] };

        if (scan.Count == 0)
            return index;

        foreach (var (source, generated) in scan)
        {
            ct.ThrowIfCancellationRequested();

            // Recorded before the match, because whether a document is generated does not depend
            // on this index having found the .proto behind it.
            foreach (var document in generated.Documents)
            {
                index._generated.Add(document.Id);

                if (document.FilePath is { Length: > 0 } documentPath)
                    index._generatedPaths.Add(ProtoDocumentService.Normalize(documentPath));
            }

            if (MatchProtoFile(protoPaths, source) is not { } path)
                continue;

            // Merged rather than assigned: two headers naming the same file is degenerate but
            // cheap to survive, and dropping one of them would lose its documents.
            index._documents[path] = index._documents.TryGetValue(path, out var already)
                ? [.. already, .. generated.Documents]
                : [.. generated.Documents];

            // Through the document service rather than the parser, so this shares the memoized
            // parse — and therefore the open buffer — with everything else looking at the file. A
            // direct parse here would re-read from disk and disagree with the editor mid-edit.
            if (ProtoDocumentService.GetParse(path) is not { } parse)
                continue;

            index.Bind(parse, generated);
        }

        index._compiledProtoFiles = [.. index._documents.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
        return index;
    }

    /// <summary>
    /// The <c>.proto</c> a generated document's header names, matched by suffix because the
    /// header records a path relative to a <c>--proto_path</c> root it does not name.
    /// </summary>
    /// <remarks>
    /// The shortest match wins when several files end the same way: the roots a build passes are
    /// directories the protos sit under, so the candidate closest to one is the one protoc was
    /// pointed at.
    /// </remarks>
    private static string? MatchProtoFile(IReadOnlyList<string> protoPaths, string source)
    {
        string? best = null;

        foreach (string path in protoPaths)
        {
            if (!EndsWithRelativePath(path, source))
                continue;

            if (best is null || path.Length < best.Length)
                best = path;
        }

        return best;
    }

    private static bool EndsWithRelativePath(string absolute, string relative)
    {
        if (absolute.Length < relative.Length)
            return false;

        int start = absolute.Length - relative.Length;

        for (int i = 0; i < relative.Length; i++)
        {
            char left = absolute[start + i];
            char right = relative[i];

            bool same = left == right
                || (left is '/' or '\\' && right is '/' or '\\')
                || char.ToUpperInvariant(left) == char.ToUpperInvariant(right);

            if (!same)
                return false;
        }

        // A directory boundary, so `types.proto` does not match `widgettypes.proto`.
        return start == 0 || absolute[start - 1] is '/' or '\\';
    }

    private void Bind(ProtoFile file, GeneratedProto generated)
    {
        BindMessages(file, file.Messages, ChainKey([]), MessagesByParent(generated), generated);
        BindEnums(file, file.Enums, EnumsIn(generated, container: null));

        foreach (var service in file.Services)
            BindService(file, service, generated);
    }

    /// <summary>
    /// Generated message classes grouped by the parent they are nested in, each group in
    /// descriptor order.
    /// </summary>
    private static Dictionary<string, List<GeneratedMessage>> MessagesByParent(GeneratedProto generated)
    {
        var groups = new Dictionary<string, List<GeneratedMessage>>(StringComparer.Ordinal);

        foreach (var message in generated.Messages)
        {
            string key = ChainKey(message.Chain.AsSpan()[..^1]);
            if (!groups.TryGetValue(key, out var siblings))
                groups[key] = siblings = [];

            siblings.Add(message);
        }

        foreach (var siblings in groups.Values)
            siblings.Sort((left, right) => left.Chain[^1].CompareTo(right.Chain[^1]));

        return groups;
    }

    private static string ChainKey(ReadOnlySpan<int> chain)
    {
        var sb = new StringBuilder();
        foreach (int index in chain)
            sb.Append(index).Append('.');
        return sb.ToString();
    }

    /// <summary>
    /// Binds a run of sibling messages to the classes generated for them, then recurses.
    /// </summary>
    /// <remarks>
    /// By rank within the group rather than by descriptor index, because the two do not agree: a
    /// <c>map</c> field adds an implicit entry type to its message's nested types, which takes a
    /// descriptor index but generates no class. Position among the classes that do exist is what
    /// tracks position among the messages that were declared, whichever order the two kinds were
    /// written in.
    /// </remarks>
    private void BindMessages(
        ProtoFile file,
        ImmutableArray<ProtoMessage> messages,
        string parentKey,
        Dictionary<string, List<GeneratedMessage>> byParent,
        GeneratedProto generated)
    {
        if (!byParent.TryGetValue(parentKey, out var siblings))
            return;

        int count = Math.Min(messages.Length, siblings.Count);

        for (int i = 0; i < count; i++)
        {
            var message = messages[i];
            var target = siblings[i];

            _messages.TryAdd(message.FullName, target.Type);
            Record(target.Type, file, message);

            foreach (var field in message.AllFields)
            {
                if (target.PropertiesByFieldNumber.TryGetValue(field.Number, out var property))
                {
                    _properties.TryAdd(field.FullName, property);
                    Record(property, file, field);
                }
            }

            BindEnums(file, message.Enums, EnumsIn(generated, target.Type));
            BindMessages(file, message.Messages, ChainKey(target.Chain.AsSpan()), byParent, generated);
        }
    }

    /// <summary>
    /// The generated enums declared in one scope: at namespace level for a file's own enums, and
    /// inside the bound message for a nested one.
    /// </summary>
    /// <remarks>
    /// The <c>Types</c> container is why this is not a plain <c>ContainingType</c> comparison.
    /// protoc puts everything a message declares nested into a static <c>Types</c> class, because a
    /// nested type and a field of the same name cannot both live in one C# class — so a nested
    /// <c>enum Kind</c> is <c>Widget.Types.Kind</c> and matching on <c>Widget</c> finds none of
    /// them. The message itself is still accepted, since the container is protoc's layout rather
    /// than the language's requirement; the oneof-case enums that do sit directly in the message
    /// never reach here, having no <c>OriginalName</c> to be recognised by.
    /// </remarks>
    private static List<GeneratedEnum> EnumsIn(GeneratedProto generated, INamedTypeSymbol? container)
    {
        if (container is null)
            return [.. generated.Enums.Where(candidate => candidate.Type.ContainingType is null)];

        var nested = container.GetTypeMembers(ProtoNaming.NestedTypesContainerName).FirstOrDefault();

        return
        [
            .. generated.Enums.Where(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate.Type.ContainingType, container)
                || SymbolEqualityComparer.Default.Equals(candidate.Type.ContainingType, nested))
        ];
    }

    /// <summary>
    /// Binds the enums declared in one scope, matching on the proto names their members carry and
    /// falling back to declaration order.
    /// </summary>
    /// <remarks>
    /// The member names are a fingerprint, and a reliable one within a scope: protobuf gives enum
    /// values the scope of the enum's parent, so two enums that shared a value name could not
    /// both be declared there. Two enums in <i>different</i> messages may well share one, which
    /// is why the candidates are the ones nested in the class already bound to this message
    /// rather than every enum in the file.
    /// </remarks>
    private void BindEnums(ProtoFile file, ImmutableArray<ProtoEnum> enums, List<GeneratedEnum> candidates)
    {
        if (candidates.Count == 0)
            return;

        var taken = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var @enum in enums)
        {
            var target = MatchEnum(@enum, candidates, taken);
            if (target is null)
                continue;

            taken.Add(target.Type);
            _enums.TryAdd(@enum.FullName, target.Type);
            Record(target.Type, file, @enum);

            foreach (var value in @enum.Values)
            {
                if (MatchEnumMember(value, target) is not { } member)
                    continue;

                _enumMembers.TryAdd(value.FullName, member);
                Record(member, file, value);
            }
        }
    }

    private static GeneratedEnum? MatchEnum(
        ProtoEnum @enum, List<GeneratedEnum> candidates, HashSet<ISymbol> taken)
    {
        var names = @enum.Values.Select(value => value.Name.Value).ToHashSet(StringComparer.Ordinal);

        GeneratedEnum? matched = null;
        int matches = 0;

        foreach (var candidate in candidates)
        {
            if (taken.Contains(candidate.Type))
                continue;

            var originals = candidate.Members
                .Select(member => member.OriginalName)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);

            if (!originals.SetEquals(names))
                continue;

            matched = candidate;
            matches++;
        }

        if (matches == 1)
            return matched;

        // Nothing fingerprinted, so fall back to where the enum was written: the generated enums
        // are emitted in declaration order, which is the order DeclarationIndex counts.
        return @enum.DeclarationIndex < candidates.Count && !taken.Contains(candidates[@enum.DeclarationIndex].Type)
            ? candidates[@enum.DeclarationIndex]
            : null;
    }

    /// <summary>
    /// The generated member for one proto enum value: by the name protoc recorded on it, and by
    /// the number when it recorded none.
    /// </summary>
    private static IFieldSymbol? MatchEnumMember(ProtoEnumValue value, GeneratedEnum target)
    {
        foreach (var (original, symbol) in target.Members)
        {
            if (string.Equals(original, value.Name.Value, StringComparison.Ordinal))
                return symbol;
        }

        foreach (var (_, symbol) in target.Members)
        {
            if (symbol.ConstantValue is int number && number == value.Number)
                return symbol;
        }

        return null;
    }

    private void BindService(ProtoFile file, ProtoService service, GeneratedProto generated)
    {
        var target = generated.Services.FirstOrDefault(
            candidate => string.Equals(candidate.ServiceName, service.FullName, StringComparison.Ordinal));

        // A file that declares one service and generated one service class is that service even
        // when the two names disagree, which they do when the proto's package was edited after
        // the last build.
        if (target is null && file.Services.Length == 1 && generated.Services.Count == 1)
            target = generated.Services[0];

        if (target is null)
            return;

        _services.TryAdd(service.FullName, new ServiceBinding(target.Type, target.Base, target.Client));

        Record(target.Type, file, service);
        Record(target.Base, file, service);
        Record(target.Client, file, service);

        foreach (var rpc in service.Rpcs)
            BindRpc(file, rpc, target);
    }

    private void BindRpc(ProtoFile file, ProtoRpc rpc, GeneratedService service)
    {
        // The plugin's own record of the name it chose, and the rpc's name when there is none —
        // which is what the plugin starts from anyway.
        string member = service.MemberNamesByRpc.TryGetValue(rpc.Name.Value, out var mapped)
            ? mapped
            : rpc.Name.Value;

        var baseMethods = MethodsNamed(service.Base, member);
        var clientMethods = MethodsNamed(service.Client, member);
        var clientAsyncMethods = MethodsNamed(service.Client, member + "Async");

        if (baseMethods.IsEmpty && clientMethods.IsEmpty && clientAsyncMethods.IsEmpty)
            return;

        ImmutableArray<IMethodSymbol> all = [.. baseMethods, .. clientMethods, .. clientAsyncMethods];

        _rpcs.TryAdd(rpc.FullName, new RpcBinding(
            baseMethods.FirstOrDefault(),
            clientMethods.FirstOrDefault(),
            clientAsyncMethods.FirstOrDefault(),
            all));

        foreach (var method in all)
            Record(method, file, rpc);
    }

    private static ImmutableArray<IMethodSymbol> MethodsNamed(INamedTypeSymbol? type, string name) =>
        type is null
            ? []
            : [.. type.GetMembers(name).OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary)];

    /// <summary>The other half of every binding: the way back from the generated symbol to the
    /// declaration it was generated from, which is what a caret in C# asks.</summary>
    private void Record(ISymbol? symbol, ProtoFile file, ProtoDeclaration declaration)
    {
        if (symbol is not null)
            _reverse.TryAdd(symbol, new ProtoDeclarationRef(file.FilePath, declaration.FullName, declaration.Kind));
    }
}
