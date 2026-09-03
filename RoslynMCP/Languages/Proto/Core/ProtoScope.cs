using System.Collections.Immutable;

namespace RoslynMCP.Languages.Proto.Core;

/// <summary>One of the types protoc ships in its own <c>google/protobuf</c> imports and generates
/// into the <c>Google.Protobuf</c> runtime rather than into the project.</summary>
/// <param name="FullName">The proto name, always under <c>google.protobuf</c>.</param>
/// <param name="ProtoPath">The import path that has to be written for the type to be visible.</param>
/// <param name="ClrTypeName">The type protoc binds it to, which lives in the runtime assembly and
/// therefore has no generated file in the project to navigate to.</param>
internal sealed record ProtoWellKnownType(string FullName, string ProtoPath, string ClrTypeName);

/// <summary>
/// The types <c>google/protobuf/*.proto</c> declares, and the runtime classes protoc binds them to.
/// </summary>
/// <remarks>
/// A table rather than a lookup through the import graph, because the well-known protos are not
/// part of any project: they ship inside the Grpc.Tools package and are handed to protoc through
/// its own import path. <see cref="ProtoImportResolver.StandardImportsDirectory"/> usually finds
/// them on disk and then they resolve like any other import — this table is what keeps
/// <c>google.protobuf.Timestamp</c> resolving when it does not, which is every machine where the
/// package was restored somewhere this process cannot see.
/// </remarks>
internal static class ProtoWellKnownTypes
{
    public static readonly ImmutableArray<ProtoWellKnownType> All =
    [
        Entry("any.proto", "Any"),
        Entry("duration.proto", "Duration"),
        Entry("empty.proto", "Empty"),
        Entry("field_mask.proto", "FieldMask"),
        Entry("struct.proto", "Struct"),
        Entry("struct.proto", "Value"),
        Entry("struct.proto", "ListValue"),
        Entry("struct.proto", "NullValue"),
        Entry("timestamp.proto", "Timestamp"),
        Entry("wrappers.proto", "DoubleValue"),
        Entry("wrappers.proto", "FloatValue"),
        Entry("wrappers.proto", "Int64Value"),
        Entry("wrappers.proto", "UInt64Value"),
        Entry("wrappers.proto", "Int32Value"),
        Entry("wrappers.proto", "UInt32Value"),
        Entry("wrappers.proto", "BoolValue"),
        Entry("wrappers.proto", "StringValue"),
        Entry("wrappers.proto", "BytesValue"),
    ];

    private static readonly Dictionary<string, ProtoWellKnownType> s_byFullName =
        All.ToDictionary(type => type.FullName, StringComparer.Ordinal);

    private static readonly HashSet<string> s_paths =
        [.. All.Select(type => type.ProtoPath)];

    /// <summary>The entry for a fully-qualified proto name, or <c>null</c> when it names something
    /// the runtime does not ship.</summary>
    public static ProtoWellKnownType? Find(string fullName) =>
        s_byFullName.TryGetValue(fullName, out var found) ? found : null;

    /// <summary>Whether an import path names one of protoc's own protos, which is how an
    /// unresolvable import is told apart from one that merely points at a missing package.</summary>
    public static bool IsWellKnownPath(string protoPath) => s_paths.Contains(protoPath);

    private static ProtoWellKnownType Entry(string file, string name) =>
        new($"google.protobuf.{name}", $"google/protobuf/{file}", $"Google.Protobuf.WellKnownTypes.{name}");
}

/// <summary>What a name in a <c>.proto</c> turned out to refer to.</summary>
/// <param name="FullName">The fully-qualified proto name the lookup settled on, which is not the
/// text that was written: <c>UUID</c> written in package <c>common</c> resolves to
/// <c>common.UUID</c>, and that is the name protoc's generated code carries.</param>
/// <param name="Declaration">The declaration, when it is in a file that could be read.</param>
/// <param name="File">The file <paramref name="Declaration"/> was declared in — a reference
/// resolves across the import graph, so this is very often not the file the reference is in.</param>
/// <param name="WellKnown">Set whenever the name is one of protoc's own, whether or not the
/// declaration was also found, because the C# type is in the runtime either way and a hover wants
/// to say so.</param>
internal sealed record ProtoTypeResolution(
    string FullName,
    ProtoDeclaration? Declaration,
    ProtoFile? File,
    ProtoWellKnownType? WellKnown)
{
    public bool IsWellKnown => WellKnown is not null;
}

/// <summary>
/// Resolves a name written in one <c>.proto</c> to the declaration it refers to, anywhere in the
/// import graph that file can see.
/// </summary>
/// <remarks>
/// <para>
/// Two protobuf rules are reproduced here rather than approximated, because both change the answer
/// on ordinary code. The first is visibility: a file sees its own declarations and those of the
/// files it imports directly, and beyond that only what those files re-export with
/// <c>import public</c>. A plain <c>import</c> is not transitive — <c>widgets.proto</c> importing
/// <c>widgets/types.proto</c> does not let it name <c>google.protobuf.Timestamp</c> just because
/// that file does. Following every import transitively would resolve names protoc rejects, which
/// is worse than failing: the editor would offer navigation for a file that does not compile.
/// </para>
/// <para>
/// The second is C++ scoping. An unqualified name is looked up by taking its <b>first component</b>
/// and trying it in the innermost enclosing scope, then in each enclosing scope outward, then at
/// the root. Whichever scope owns that first component wins, and the rest of the name must resolve
/// underneath it — the search does <b>not</b> continue outward when it does not. That is what makes
/// a nested message named <c>common</c> shadow a package named <c>common</c> for everything below
/// it, and reproducing it is the difference between agreeing with protoc and guessing.
/// </para>
/// </remarks>
internal sealed class ProtoScope
{
    private readonly ImmutableArray<ProtoFile> _files;
    private readonly HashSet<string> _importedPaths;
    private readonly HashSet<string> _packages;

    private ProtoScope(
        ProtoFile file,
        ImmutableArray<ProtoFile> files,
        HashSet<string> importedPaths,
        HashSet<string> packages)
    {
        File = file;
        _files = files;
        _importedPaths = importedPaths;
        _packages = packages;
    }

    /// <summary>The file the scope was built for, and the one every reference passed to
    /// <see cref="Resolve(ProtoTypeRef)"/> has to come from.</summary>
    public ProtoFile File { get; }

    /// <summary>
    /// Every file whose declarations <see cref="File"/> may name, itself first and its imports in
    /// breadth-first order after it.
    /// </summary>
    /// <remarks>
    /// The order is the tie-break when two files declare the same fully-qualified name. protoc
    /// rejects that outright as a duplicate symbol, so there is no correct answer to give; taking
    /// the nearest one keeps navigation pointing at the file the user is most likely to have meant
    /// while the error is still on screen.
    /// </remarks>
    public ImmutableArray<ProtoFile> VisibleFiles => _files;

    /// <summary>
    /// Walks <paramref name="file"/>'s imports and builds the set of declarations it can name.
    /// </summary>
    /// <param name="projectDirectory">The owning project's directory, which is the proto root
    /// Grpc.Tools gives a file inside it. <c>null</c> leaves
    /// <see cref="ProtoImportResolver"/> to find one.</param>
    /// <remarks>
    /// Imports are read through <see cref="ProtoDocumentService"/> rather than parsed here, so a
    /// file open in the editor contributes its unsaved buffer to every other file that imports it
    /// and the parse itself is shared with whatever else already asked for that file.
    /// </remarks>
    public static ProtoScope Create(ProtoFile file, string? projectDirectory = null)
    {
        var files = ImmutableArray.CreateBuilder<ProtoFile>();
        files.Add(file);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file.FilePath };
        var importedPaths = new HashSet<string>(StringComparer.Ordinal);
        var packages = new HashSet<string>(StringComparer.Ordinal);
        AddPackage(packages, file.Package);

        // The flag is the visibility rule: the root file's own imports all count, and everything
        // reached through one of them counts only when it was re-exported.
        var pending = new Queue<(ProtoFile File, bool PublicOnly)>();
        pending.Enqueue((file, false));

        while (pending.Count > 0)
        {
            var (current, publicOnly) = pending.Dequeue();

            foreach (var import in current.Imports)
            {
                if (publicOnly && !import.IsPublic)
                    continue;

                // Recorded before the file is looked for, because a well-known import stays
                // meaningful when the package it lives in is nowhere on this machine.
                importedPaths.Add(import.Path);

                if (ProtoImportResolver.Resolve(import.Path, current.FilePath, projectDirectory)
                    is not { } resolved)
                {
                    continue;
                }

                // Import cycles are legal in protobuf as long as no type cycle results, and a
                // diamond is ordinary; either would loop forever without this.
                if (!visited.Add(resolved))
                    continue;

                if (ProtoDocumentService.GetParse(resolved) is not { } imported)
                    continue;

                files.Add(imported);
                AddPackage(packages, imported.Package);
                pending.Enqueue((imported, true));
            }
        }

        // A well-known import whose file could not be read still puts its package in scope, so that
        // `google.protobuf.Timestamp` gets past the first-component test and reaches the table.
        foreach (var wellKnown in ProtoWellKnownTypes.All)
        {
            if (importedPaths.Contains(wellKnown.ProtoPath))
                AddPackage(packages, PackageOf(wellKnown.FullName));
        }

        return new ProtoScope(file, files.ToImmutable(), importedPaths, packages);
    }

    /// <summary>
    /// Resolves a reference written in <see cref="File"/>, working out for itself which declaration
    /// it sits in.
    /// </summary>
    public ProtoTypeResolution? Resolve(ProtoTypeRef reference) =>
        Resolve(reference, File.DeclarationAt(reference.Span.Start));

    /// <summary>
    /// Resolves a reference written inside <paramref name="containingDeclaration"/>, or <c>null</c>
    /// when it names nothing visible — or names a scalar, which is a built-in and not a declaration
    /// anyone can navigate to.
    /// </summary>
    public ProtoTypeResolution? Resolve(ProtoTypeRef reference, ProtoDeclaration? containingDeclaration)
    {
        if (reference.IsScalar)
            return null;

        return ResolveIn(reference.Text, ScopeOf(containingDeclaration, File));
    }

    /// <summary>
    /// Resolves <paramref name="name"/> as if it had been written in <paramref name="scope"/>, a
    /// fully-qualified scope name or the empty string for the root.
    /// </summary>
    public ProtoTypeResolution? ResolveIn(string name, string scope)
    {
        if (name.Length == 0)
            return null;

        if (name[0] == '.')
            return Find(name.TrimStart('.'));

        int dot = name.IndexOf('.');
        string head = dot < 0 ? name : name[..dot];

        foreach (string candidate in EnclosingScopes(scope))
        {
            string prefix = candidate.Length == 0 ? string.Empty : candidate + ".";
            var found = Find(prefix + head);

            // A package counts as the owner of the first component even though it is not a
            // declaration — `common.UUID` written in package `widgets` finds nothing called
            // `common` anywhere, and resolves only because `common` is a package name.
            if (found is null && !_packages.Contains(prefix + head))
                continue;

            // The scope that owns the first component owns the whole name. Returning whatever
            // `Find` says here — including nothing — is the rule, not a shortcut: protoc reports
            // an error rather than looking further out, and so does this.
            return dot < 0 ? found : Find(prefix + name);
        }

        return null;
    }

    /// <summary>The declaration with this fully-qualified proto name, searched across every visible
    /// file.</summary>
    public ProtoTypeResolution? Find(string fullName)
    {
        if (fullName.Length == 0)
            return null;

        var wellKnown = ProtoWellKnownTypes.Find(fullName);

        foreach (var file in _files)
        {
            if (file.FindByFullName(fullName) is { } declaration)
                return new ProtoTypeResolution(fullName, declaration, file, wellKnown);
        }

        // Falling back to the table only when the matching import was actually written keeps this
        // from inventing a resolution for a name nothing in the file may refer to.
        return wellKnown is not null && _importedPaths.Contains(wellKnown.ProtoPath)
            ? new ProtoTypeResolution(fullName, null, null, wellKnown)
            : null;
    }

    /// <summary>
    /// The fully-qualified name of the scope a declaration's members and references live in.
    /// </summary>
    /// <remarks>
    /// Only a message and a service open one. An <c>enum</c> does not — protobuf gives enum values
    /// C++ scoping — and neither does a <c>oneof</c> or an <c>extend</c> block, whose members belong
    /// to the message around them. Deriving the scope by walking to the nearest opener rather than
    /// by chopping the declaration's own <see cref="ProtoDeclaration.FullName"/> is also what keeps
    /// an <c>extend</c> honest: its full name is the name of the thing it extends, which lives in
    /// somebody else's package.
    /// </remarks>
    public static string ScopeOf(ProtoDeclaration? declaration, ProtoFile file)
    {
        for (var current = declaration; current is not null; current = current.Parent)
        {
            if (current.Kind is ProtoDeclarationKind.Message or ProtoDeclarationKind.Service)
                return current.FullName;
        }

        return file.Package;
    }

    /// <summary>
    /// Records a package and every prefix of it as a name that exists.
    /// </summary>
    /// <remarks>
    /// Every segment, because a package is not one symbol: <c>package a.b.c</c> declares <c>a</c>,
    /// <c>a.b</c> and <c>a.b.c</c>, and a reference to <c>b.c.Thing</c> from inside <c>a</c> is
    /// resolved by finding <c>a.b</c> first.
    /// </remarks>
    private static void AddPackage(HashSet<string> packages, string package)
    {
        if (package.Length == 0)
            return;

        for (int dot = package.IndexOf('.'); dot >= 0; dot = package.IndexOf('.', dot + 1))
            packages.Add(package[..dot]);

        packages.Add(package);
    }

    private static string PackageOf(string fullName)
    {
        int dot = fullName.LastIndexOf('.');
        return dot < 0 ? string.Empty : fullName[..dot];
    }

    /// <summary>The scope itself, then each enclosing one, then the root — the order protobuf looks
    /// an unqualified name up in.</summary>
    private static IEnumerable<string> EnclosingScopes(string scope)
    {
        while (scope.Length > 0)
        {
            yield return scope;

            int dot = scope.LastIndexOf('.');
            scope = dot < 0 ? string.Empty : scope[..dot];
        }

        yield return string.Empty;
    }
}
