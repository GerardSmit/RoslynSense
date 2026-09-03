using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Services.ExternalSource;

/// <summary>
/// Where a line of a decompiled or fetched file lands in the IL: the assembly, the MethodDef
/// token, the offset, and the 1-based line the mapping actually settled on.
/// </summary>
/// <param name="MethodDisplayName">
/// <c>Namespace.Type.Method</c>, for engines that can only break on a function by name.
/// </param>
/// <param name="DocumentPath">
/// The path the PDB records for this file, when the file was fetched from a PDB document —
/// engines that bind through their own symbol reader can be handed this path instead of the
/// cache path. Empty for decompiled and reference-source files, which appear in no PDB.
/// </param>
/// <param name="Exact">
/// Whether the offset corresponds to the requested line. False for reference source, where no
/// offsets exist and the method entry is the closest honest target.
/// </param>
internal sealed record DebugSourceTarget(
    string AssemblyPath,
    int MethodToken,
    int IlOffset,
    int Line,
    int Column,
    string MethodDisplayName,
    string DocumentPath,
    string Origin,
    bool Exact);

/// <summary>
/// <see cref="DebugFrameSource"/> read backwards: instead of turning a stopped frame's IL
/// position into a line of decompiled or fetched text, this turns a line of that text back into
/// an IL position — which is what lets a breakpoint, run-to-cursor, or set-next-statement land
/// inside a file no PDB document names.
/// </summary>
/// <remarks>
/// Each cache lane is inverted with the same data the forward direction used, so the two agree:
/// decompiled files through the decompiler's own sequence-point map, embedded and Source Link
/// files through the PDB's sequence points (the cached file is byte-identical to the compiled
/// document — checksums verified that on the way in — so its line numbers are the PDB's line
/// numbers). Reference source was never compiled and has no offsets; the enclosing member's
/// method entry is the closest honest target, and the result says so via <c>Exact</c>.
/// </remarks>
internal static class DebugSourceMapper
{
    /// <summary>The IL target for a line of an external file; null when the path is not one of
    /// ours or the line maps to nothing executable.</summary>
    public static async Task<DebugSourceTarget?> TryMapAsync(
        string filePath, int line, CancellationToken ct = default)
    {
        if (filePath is not { Length: > 0 } || line <= 0)
            return null;

        try
        {
            if (DecompiledSourceService.IsDecompiledPath(filePath))
                return await FromDecompiledAsync(filePath, line, ct).ConfigureAwait(false);

            if (!ExternalSourceCache.IsExternalSourcePath(filePath))
                return null;

            if (ExternalSourceProject.TryReadSidecar(filePath) is not var (assemblyPath, typeName)
                || !File.Exists(assemblyPath))
            {
                return null;
            }

            string full = Path.GetFullPath(filePath);
            if (IsUnder(full, ExternalSourceCache.EmbeddedDirectory)
                || IsUnder(full, SourceLinkService.CacheDirectory))
            {
                return await FromPdbDocumentAsync(full, line, assemblyPath, ct).ConfigureAwait(false);
            }

            if (IsUnder(full, ExternalSourceCache.ReferenceSourceDirectory))
                return await FromMemberAsync(full, line, assemblyPath, typeName, ct).ConfigureAwait(false);

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceLog.Warn(
                $"Could not map {Path.GetFileName(filePath)}:{line} back to IL: {ex.Message}",
                key: $"debug-source-map:{filePath}");
            return null;
        }
    }

    private static bool IsUnder(string fullPath, string directory) =>
        fullPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static async Task<DebugSourceTarget?> FromDecompiledAsync(
        string filePath, int line, CancellationToken ct)
    {
        if (await DecompiledSourceService.TryReadFrameManifestAsync(filePath, ct).ConfigureAwait(false)
            is not var (assemblyPath, typeName) || !File.Exists(assemblyPath))
        {
            return null;
        }

        var mapped = await DecompiledSourceService.TryMapLineToIlAsync(
            assemblyPath, typeName, filePath, line, ct).ConfigureAwait(false);
        if (mapped is not { } m)
            return null;
        var (token, offset, actualLine, column) = m;

        return new DebugSourceTarget(
            assemblyPath, token, offset, actualLine, column,
            MethodDisplayName(assemblyPath, token),
            DocumentPath: "",
            Origin: "decompiled",
            Exact: true);
    }

    /// <summary>
    /// The PDB lane: find which PDB document the cached file was made from — the cache path is a
    /// pure function of the document, so each document's path is recomputed and compared — then
    /// pick the sequence point for the line out of that document.
    /// </summary>
    private static async Task<DebugSourceTarget?> FromPdbDocumentAsync(
        string filePath, int line, string assemblyPath, CancellationToken ct)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        using var pdb = await PdbLocator.OpenAsync(peReader, assemblyPath, ct).ConfigureAwait(false);
        if (pdb is null)
            return null;

        var pdbReader = pdb.Provider.GetMetadataReader();
        bool embedded = IsUnder(filePath, ExternalSourceCache.EmbeddedDirectory);
        string? map = embedded ? null : SourceLinkService.ReadSourceLinkMap(pdbReader);
        if (!embedded && map is null)
            return null;

        string fileName = Path.GetFileName(filePath);
        DocumentHandle documentHandle = default;
        string documentPath = "";
        foreach (var handle in pdbReader.Documents)
        {
            string name = pdbReader.GetString(pdbReader.GetDocument(handle).Name);
            // The file name survives caching unchanged, so it rules out most documents before
            // any fingerprint is computed.
            if (!string.Equals(
                    Path.GetFileName(name.Replace('\\', '/')), fileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? candidate = embedded
                ? SourceLinkService.EmbeddedCachePath(assemblyPath, name)
                : SourceLinkService.SourceLinkCachePath(map!, name);
            if (candidate is not null
                && string.Equals(Path.GetFullPath(candidate), filePath, StringComparison.OrdinalIgnoreCase))
            {
                documentHandle = handle;
                documentPath = name;
                break;
            }
        }

        if (documentHandle.IsNil)
            return null;

        // The line's sequence point, across every method compiled from this document. Like a
        // breakpoint in real source, a line with no point slides down to the next one that has.
        int documentRow = MetadataTokens.GetRowNumber(documentHandle);
        (int Token, int Offset, int Line, int Column)? best = null;
        foreach (var informationHandle in pdbReader.MethodDebugInformation)
        {
            var information = pdbReader.GetMethodDebugInformation(informationHandle);
            foreach (var point in information.GetSequencePoints())
            {
                if (point.IsHidden
                    || MetadataTokens.GetRowNumber(point.Document) != documentRow
                    || point.StartLine < line)
                {
                    continue;
                }
                bool better = best is not { } b
                    || point.StartLine < b.Line
                    || (point.StartLine == b.Line && point.Offset < b.Offset);
                if (better)
                {
                    int token = 0x06000000 | MetadataTokens.GetRowNumber(informationHandle);
                    best = (token, point.Offset, point.StartLine, Math.Max(1, point.StartColumn));
                }
            }
        }

        if (best is not { } picked)
            return null;

        return new DebugSourceTarget(
            assemblyPath, picked.Token, picked.Offset, picked.Line, picked.Column,
            MethodDisplayName(peReader.GetMetadataReader(), picked.Token),
            documentPath,
            Origin: embedded ? "embedded" : "source link",
            Exact: true);
    }

    /// <summary>
    /// The reference-source lane. The snapshot was never compiled, so no line maps to an offset;
    /// the member enclosing the line is located in the syntax, matched to a MethodDef by name and
    /// arity, and the target is that method's entry.
    /// </summary>
    private static async Task<DebugSourceTarget?> FromMemberAsync(
        string filePath, int line, string assemblyPath, string typeName, CancellationToken ct)
    {
        string text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: ct);
        var root = await tree.GetRootAsync(ct).ConfigureAwait(false);
        var lines = (await tree.GetTextAsync(ct).ConfigureAwait(false)).Lines;
        if (line > lines.Count)
            return null;

        if (EnclosingMember(root, lines[line - 1].Start) is not { } member)
            return null;
        var (metadataName, parameterCount, _) = member;

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        if (SourceLinkService.FindType(typeName, metadata) is not { } typeHandle)
            return null;

        // Name first, arity as a tiebreak — mirroring how the forward direction located the
        // member in the file.
        MethodDefinitionHandle found = default;
        foreach (var handle in metadata.GetTypeDefinition(typeHandle).GetMethods())
        {
            var method = metadata.GetMethodDefinition(handle);
            if (metadata.GetString(method.Name) != metadataName)
                continue;
            if (DebugFrameSource.ParameterCountOf(metadata, method) == parameterCount)
            {
                found = handle;
                break;
            }
            if (found.IsNil)
                found = handle;
        }

        if (found.IsNil)
            return null;

        int token = MetadataTokens.GetToken(found);
        return new DebugSourceTarget(
            assemblyPath, token, IlOffset: 0, line, Column: 1,
            MethodDisplayName(metadata, token),
            DocumentPath: "",
            Origin: "reference source",
            Exact: false);
    }

    /// <summary>The metadata name and arity of the member a position sits in, or null when the
    /// position is outside anything that compiles to a method of its own.</summary>
    private static (string MetadataName, int ParameterCount, bool IsStatic)? EnclosingMember(
        Microsoft.CodeAnalysis.SyntaxNode root, int position)
    {
        var node = root.FindToken(position).Parent;
        while (node is not null)
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    return (method.Identifier.ValueText,
                        method.ParameterList.Parameters.Count,
                        method.Modifiers.Any(SyntaxKind.StaticKeyword));
                case ConstructorDeclarationSyntax ctor:
                    bool isStatic = ctor.Modifiers.Any(SyntaxKind.StaticKeyword);
                    return (isStatic ? ".cctor" : ".ctor",
                        ctor.ParameterList.Parameters.Count, isStatic);
                case DestructorDeclarationSyntax:
                    return ("Finalize", 0, false);
                case AccessorDeclarationSyntax accessor:
                    return AccessorMetadataName(accessor);
                case PropertyDeclarationSyntax property:
                    // The line is on the property but not inside an accessor — an expression
                    // body, or the declaration line itself. The getter is the natural target.
                    string propertyPrefix = property.AccessorList?.Accessors
                        .FirstOrDefault()?.Keyword.ValueText == "set" ? "set_" : "get_";
                    return (propertyPrefix + property.Identifier.ValueText,
                        0, property.Modifiers.Any(SyntaxKind.StaticKeyword));
                case IndexerDeclarationSyntax indexer:
                    return ("get_Item",
                        indexer.ParameterList.Parameters.Count,
                        false);
            }
            node = node.Parent;
        }
        return null;
    }

    private static (string, int, bool)? AccessorMetadataName(AccessorDeclarationSyntax accessor)
    {
        string prefix = accessor.Keyword.ValueText switch
        {
            "get" => "get_",
            "set" or "init" => "set_",
            "add" => "add_",
            "remove" => "remove_",
            _ => "",
        };
        if (prefix.Length == 0)
            return null;

        return accessor.Parent?.Parent switch
        {
            PropertyDeclarationSyntax property => (prefix + property.Identifier.ValueText, 0,
                property.Modifiers.Any(SyntaxKind.StaticKeyword)),
            IndexerDeclarationSyntax indexer => (prefix + "Item",
                indexer.ParameterList.Parameters.Count, false),
            EventDeclarationSyntax @event => (prefix + @event.Identifier.ValueText, 1,
                @event.Modifiers.Any(SyntaxKind.StaticKeyword)),
            _ => null,
        };
    }

    private static string MethodDisplayName(string assemblyPath, int methodToken)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            return MethodDisplayName(peReader.GetMetadataReader(), methodToken);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>The <c>Namespace.Type.Method</c> form function-name breakpoints take. Nested
    /// types are joined with a dot — that is how netcoredbg spells them.</summary>
    private static string MethodDisplayName(MetadataReader metadata, int methodToken)
    {
        try
        {
            int row = methodToken & 0xFFFFFF;
            if (row == 0 || row > metadata.MethodDefinitions.Count)
                return "";
            var method = metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(row));
            string typeName = DebugFrameSource
                .ReflectionTypeNameOf(metadata, method.GetDeclaringType())
                .Replace('+', '.');
            return $"{typeName}.{metadata.GetString(method.Name)}";
        }
        catch (BadImageFormatException)
        {
            return "";
        }
    }
}
