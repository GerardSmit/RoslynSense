using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using RoslynMCP.Services.ExternalSource;

namespace RoslynMCP.Services;

/// <summary>Where a metadata symbol's real source lives, once it has been fetched.</summary>
/// <param name="Url">Where it came from, or null when the PDB carried the source itself.</param>
public sealed record SourceLinkResult(string FilePath, int Line, string? Url, bool Embedded = false);

/// <summary>
/// Resolves a symbol that came from a NuGet package or the framework to the source file it was
/// actually compiled from, using the Source Link map its PDB carries.
/// </summary>
/// <remarks>
/// <para>
/// This is the difference between reading a dependency and reading a reconstruction of it.
/// Decompilation gives correct code with the comments, names and structure removed; Source Link
/// gives the file the author wrote, at the line the symbol is declared on. Every step can fail
/// independently — no PDB, no Source Link block, an unreachable host, a checksum that does not
/// match — and each failure returns null so the caller falls back to decompiling.
/// </para>
/// <para>
/// The checksum is verified rather than trusted. A Source Link URL usually points at a public
/// host and a commit hash, but nothing in the format guarantees the bytes fetched are the bytes
/// compiled, and source that silently disagrees with the assembly is worse than no source:
/// breakpoints land on the wrong line and the code says something the binary does not do.
/// </para>
/// </remarks>
public static class SourceLinkService
{
    /// <summary>The <c>SourceLink</c> custom debug information kind.</summary>
    private static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    internal static readonly Guid Sha1 = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    internal static readonly Guid Sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");

    /// <summary>A source file larger than this is not a source file.</summary>
    private const int MaxDownloadBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Type lookups and Source Link maps, keyed by the assembly and when it was last written.
    /// CoreLib declares some seven thousand types and <see cref="FindType"/> walks them all, which
    /// is not a thing to do on every keystroke-driven navigation.
    /// </summary>
    private static readonly ConcurrentDictionary<(string Assembly, long Stamp, string Type), int> s_typeRows = new();

    private static readonly ConcurrentDictionary<(string Assembly, long Stamp), string?> s_maps = new();

    public static string CacheDirectory => ExternalSourceCache.SourceLinkDirectory;

    /// <summary>
    /// The real source for <paramref name="symbol"/>, or null when this assembly does not
    /// carry the information to find it.
    /// </summary>
    public static async Task<SourceLinkResult?> TryResolveAsync(
        ISymbol symbol, Project project, CancellationToken ct)
    {
        if (!Config.LspFeatureOptions.SourceLink)
            return null;

        try
        {
            if (SourceMemberLocator.GetOwningType(symbol) is not { } owningType)
                return null;

            if (await SourceMemberLocator.AssemblyPathAsync(symbol, project, ct).ConfigureAwait(false)
                is not { } assemblyPath)
                return null;

            return await ResolveCoreAsync(
                assemblyPath,
                symbol,
                SourceMemberLocator.GetReflectionTypeName(owningType),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Source Link lookup failed: {ex.Message}", key: "sourcelink");
            return null;
        }
    }

    /// <summary>
    /// The real source for a type named in an assembly, for callers that never had an
    /// <see cref="ISymbol"/> — the search panel and the metadata document handler.
    /// </summary>
    public static async Task<SourceLinkResult?> TryResolveForAssemblyAsync(
        string assemblyPath, string reflectionTypeName, CancellationToken ct)
    {
        if (!Config.LspFeatureOptions.SourceLink)
            return null;

        try
        {
            return await ResolveCoreAsync(assemblyPath, symbol: null, reflectionTypeName, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Source Link lookup failed: {ex.Message}", key: "sourcelink");
            return null;
        }
    }

    private static async Task<SourceLinkResult?> ResolveCoreAsync(
        string assemblyPath, ISymbol? symbol, string reflectionTypeName, CancellationToken ct)
    {
        // The compilation references a reference assembly, whose method bodies are all `throw
        // null` and which therefore has no sequence points and no PDB anywhere. The implementation
        // assembly is the one that was compiled from the source we are looking for. This has to
        // happen before the PE is opened, because everything below reads tokens out of that PE and
        // resolves them against that PE's PDB.
        assemblyPath = ReferenceAssemblyRedirector.RedirectToImplementation(assemblyPath, reflectionTypeName);
        if (!File.Exists(assemblyPath))
            return null;

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);

        using var pdb = await PdbLocator.OpenAsync(peReader, assemblyPath, ct).ConfigureAwait(false);
        if (pdb is null)
            return null;

        var pdbReader = pdb.Provider.GetMetadataReader();
        var metadata = peReader.GetMetadataReader();

        var methods = MethodsOf(symbol, reflectionTypeName, assemblyPath, metadata);
        if (methods.IsEmpty)
            return null;

        var (typeSimpleName, _) = SourceMemberLocator.SplitReflectionName(reflectionTypeName);

        if (BestDocument(pdbReader, methods, symbol is IMethodSymbol, typeSimpleName)
                is not var (documentHandle, line)
            || documentHandle.IsNil)
        {
            return null;
        }

        return await TryResolveDocumentAsync(pdbReader, assemblyPath, documentHandle, line, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The local file for one PDB document: unpacked from the PDB when it carries the source,
    /// downloaded from where its Source Link map points otherwise. Shared between navigation,
    /// which chose the document from a declaration, and the debugger, which chose it from the
    /// stopped frame's sequence point.
    /// </summary>
    internal static async Task<SourceLinkResult?> TryResolveDocumentAsync(
        MetadataReader pdbReader,
        string assemblyPath,
        DocumentHandle documentHandle,
        int line,
        CancellationToken ct)
    {
        var document = pdbReader.GetDocument(documentHandle);
        string documentPath = pdbReader.GetString(document.Name);
        byte[] hash = pdbReader.GetBlobBytes(document.Hash);
        var algorithm = pdbReader.GetGuid(document.HashAlgorithm);

        // Source the PDB carries needs no network, no URL and no host that might be down.
        if (EmbeddedSourceReader.TryRead(pdbReader, documentHandle) is { } embedded
            && Reconcile(embedded, hash, algorithm) is { } verifiedEmbedded)
        {
            string? saved = SaveEmbedded(assemblyPath, documentPath, verifiedEmbedded);
            if (saved is not null)
                return new SourceLinkResult(saved, line, Url: null, Embedded: true);
        }

        // The debugger calls in without the public entry points' Source Link gate, so it is
        // re-checked here before anything leaves the machine.
        if (!Config.LspFeatureOptions.SourceLink || !Config.LspFeatureOptions.ExternalSource)
            return null;

        string? map = ReadSourceLinkMap(pdbReader, assemblyPath);
        if (map is null || ResolveUrl(map, documentPath) is not { } url)
            return null;

        string? local = await FetchAsync(url, documentPath, hash, algorithm, ct).ConfigureAwait(false);
        return local is null ? null : new SourceLinkResult(local, line, url);
    }

    /// <summary>
    /// The document a type is declared in, and the line to land on.
    /// </summary>
    /// <remarks>
    /// A partial type is compiled from several files — <c>String</c> comes from <c>String.cs</c>,
    /// <c>String.Manipulation.cs</c>, <c>String.Searching.cs</c> and more — and its methods are
    /// spread across all of them. Taking the earliest line across every document picks whichever
    /// file happens to hold a method declared near its top, which is arbitrary. The file that most
    /// of the type's methods were compiled from is the one a reader means by "where is this type".
    /// </remarks>
    private static (DocumentHandle Document, int Line)? BestDocument(
        MetadataReader pdb,
        ImmutableArray<MethodDefinitionHandle> methods,
        bool singleMethod,
        string typeSimpleName)
    {
        var firstPoints = new List<(int Document, int Line, string Name)>(methods.Length);

        foreach (var handle in methods)
        {
            var debugInformation = pdb.GetMethodDebugInformation(
                MetadataTokens.MethodDebugInformationHandle(MetadataTokens.GetRowNumber(handle)));

            foreach (var point in debugInformation.GetSequencePoints())
            {
                if (point.IsHidden)
                    continue;

                string name = pdb.GetString(pdb.GetDocument(point.Document).Name);
                firstPoints.Add((MetadataTokens.GetRowNumber(point.Document), point.StartLine, name));
                break;
            }
        }

        if (ChooseDeclarationPoint(firstPoints, singleMethod, typeSimpleName) is not var (documentRow, line))
            return null;

        return (MetadataTokens.DocumentHandle(documentRow), line);
    }

    /// <summary>
    /// Picks the declaration point from each method's first sequence point. Separated from the
    /// metadata reading so the choice can be tested without building a PDB.
    /// </summary>
    /// <param name="singleMethod">
    /// True when the caller asked about one method rather than a whole type, in which case there
    /// is nothing to choose between — that method's own file is the answer.
    /// </param>
    internal static (int Document, int Line)? ChooseDeclarationPoint(
        IReadOnlyList<(int Document, int Line, string Name)> firstPoints,
        bool singleMethod,
        string typeSimpleName)
    {
        if (firstPoints.Count == 0)
            return null;

        if (singleMethod)
            return (firstPoints[0].Document, firstPoints[0].Line);

        var byDocument = new Dictionary<int, (int Count, int MinLine, bool Named)>();
        foreach (var (document, line, name) in firstPoints)
        {
            if (byDocument.TryGetValue(document, out var tally))
            {
                byDocument[document] = tally with
                {
                    Count = tally.Count + 1,
                    MinLine = Math.Min(tally.MinLine, line),
                };
            }
            else
            {
                byDocument[document] = (1, line, NamesTheType(name, typeSimpleName));
            }
        }

        int bestDocument = 0;
        (int Count, int MinLine, bool Named) best = (0, int.MaxValue, false);

        foreach (var (document, tally) in byDocument)
        {
            // A file named after the type outranks any count. Most of String's methods live in
            // String.Manipulation.cs, but a reader who asked for String means String.cs; falling
            // back to the count is for types whose file is not named after them at all.
            bool better = tally.Named != best.Named
                ? tally.Named
                : tally.Count > best.Count
                    || (tally.Count == best.Count && tally.MinLine < best.MinLine)
                    // Settled on the row so the answer never depends on dictionary ordering.
                    || (tally.Count == best.Count && tally.MinLine == best.MinLine && document < bestDocument);

            if (better)
                (bestDocument, best) = (document, tally);
        }

        return (bestDocument, best.MinLine);
    }

    /// <summary>Whether a document is the one named after the type: <c>.../String.cs</c>.</summary>
    private static bool NamesTheType(string documentPath, string typeSimpleName)
    {
        if (typeSimpleName.Length == 0)
            return false;

        string stem = Path.GetFileNameWithoutExtension(documentPath.Replace('\\', '/'));
        return string.Equals(stem, typeSimpleName, StringComparison.Ordinal);
    }

    private static string? ReadSourceLinkMap(MetadataReader pdb, string assemblyPath) =>
        s_maps.GetOrAdd(CacheKey(assemblyPath), _ => ReadSourceLinkMap(pdb));

    internal static string? ReadSourceLinkMap(MetadataReader pdb)
    {
        foreach (var handle in pdb.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
        {
            var information = pdb.GetCustomDebugInformation(handle);
            if (pdb.GetGuid(information.Kind) != SourceLinkKind)
                continue;

            return System.Text.Encoding.UTF8.GetString(pdb.GetBlobBytes(information.Value));
        }
        return null;
    }

    /// <summary>
    /// Applies the Source Link document map: keys are file prefixes ending in <c>*</c>, values
    /// the URL to substitute the rest of the path into.
    /// </summary>
    internal static string? ResolveUrl(string map, string documentPath)
    {
        using var json = JsonDocument.Parse(map);
        if (!json.RootElement.TryGetProperty("documents", out var documents))
            return null;

        string? bestUrl = null;
        int bestPrefixLength = -1;

        foreach (var entry in documents.EnumerateObject())
        {
            string pattern = entry.Name;
            string? target = entry.Value.GetString();
            if (target is null)
                continue;

            int star = pattern.IndexOf('*');
            if (star < 0)
            {
                if (PathsEqual(pattern, documentPath) && pattern.Length > bestPrefixLength)
                {
                    bestPrefixLength = pattern.Length;
                    bestUrl = target;
                }
                continue;
            }

            string prefix = pattern[..star];
            if (!documentPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            // The most specific mapping wins; a repository with submodules maps several.
            if (prefix.Length <= bestPrefixLength)
                continue;

            bestPrefixLength = prefix.Length;
            bestUrl = target.Replace("*",
                documentPath[prefix.Length..].Replace('\\', '/'), StringComparison.Ordinal);
        }

        return bestUrl;
    }

    /// <summary>
    /// The metadata methods that make up this symbol: the method itself, or every method of a
    /// type. Matched by name because there is no public map from an <see cref="ISymbol"/> to a
    /// metadata token.
    /// </summary>
    private static ImmutableArray<MethodDefinitionHandle> MethodsOf(
        ISymbol? symbol, string reflectionTypeName, string assemblyPath, MetadataReader metadata)
    {
        if (FindType(reflectionTypeName, assemblyPath, metadata) is not { } type)
            return [];

        var all = metadata.GetTypeDefinition(type).GetMethods();
        if (symbol is not IMethodSymbol method)
            return [.. all];

        string wanted = method.MethodKind switch
        {
            MethodKind.Constructor => ".ctor",
            MethodKind.StaticConstructor => ".cctor",
            _ => method.MetadataName,
        };

        var matches = all
            .Where(h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == wanted)
            .ToImmutableArray();

        // Overloads are not told apart — the signature would have to be decoded to do that, and
        // every overload of a method is declared in the same file anyway.
        return matches.IsEmpty ? [.. all] : matches;
    }

    private static TypeDefinitionHandle? FindType(
        string reflectionTypeName, string assemblyPath, MetadataReader metadata)
    {
        var (assembly, stamp) = CacheKey(assemblyPath);
        int row = s_typeRows.GetOrAdd(
            (assembly, stamp, reflectionTypeName),
            _ => FindType(reflectionTypeName, metadata) is { } handle
                ? MetadataTokens.GetRowNumber(handle)
                : 0);

        return row == 0 ? null : MetadataTokens.TypeDefinitionHandle(row);
    }

    /// <summary>Locates a type by its metadata name, following the nesting chain outwards.</summary>
    internal static TypeDefinitionHandle? FindType(string reflectionTypeName, MetadataReader metadata)
    {
        string[] nesting = reflectionTypeName.Split('+');
        string outermost = nesting[0];

        int lastDot = outermost.LastIndexOf('.');
        string @namespace = lastDot < 0 ? "" : outermost[..lastDot];
        string name = lastDot < 0 ? outermost : outermost[(lastDot + 1)..];

        // Nested types carry only their own name in metadata, with the nesting held separately —
        // and the namespace only ever sits on the outermost one, so the enclosing name to match
        // against is the bare name when the parent is that outermost type.
        string wanted = nesting.Length == 1 ? name : nesting[^1];
        string enclosing = nesting.Length == 2 ? name : nesting.Length > 2 ? nesting[^2] : string.Empty;

        foreach (var handle in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(handle);
            if (metadata.GetString(definition.Name) != wanted)
                continue;

            if (nesting.Length == 1)
            {
                if (metadata.GetString(definition.Namespace) == @namespace)
                    return handle;
                continue;
            }

            var declaring = definition.GetDeclaringType();
            if (declaring.IsNil)
                continue;

            var parent = metadata.GetTypeDefinition(declaring);
            if (metadata.GetString(parent.Name) == enclosing)
                return handle;
        }

        return null;
    }

    /// <summary>
    /// Where the local copy of an embedded document lives, computed without writing anything —
    /// so a cached file can be matched back to the PDB document it was unpacked from.
    /// </summary>
    internal static string EmbeddedCachePath(string assemblyPath, string documentPath) =>
        Path.Combine(
            ExternalSourceCache.EmbeddedDirectory,
            ExternalSourceCache.Fingerprint($"{assemblyPath}\n{documentPath}"),
            Path.GetFileName(documentPath.Replace('\\', '/')));

    /// <summary>
    /// Where the local copy of a Source Link document lives, from the map already read out of
    /// the PDB; null when the map has no entry for the document. Computed without fetching.
    /// </summary>
    internal static string? SourceLinkCachePath(string map, string documentPath) =>
        ResolveUrl(map, documentPath) is { } url
            ? Path.Combine(
                CacheDirectory,
                ExternalSourceCache.Fingerprint(url),
                Path.GetFileName(documentPath.Replace('\\', '/')))
            : null;

    /// <summary>Writes source unpacked from a PDB to the cache, so an editor has a file to open.</summary>
    private static string? SaveEmbedded(string assemblyPath, string documentPath, byte[] content)
    {
        string target = EmbeddedCachePath(assemblyPath, documentPath);

        if (File.Exists(target))
            return target;

        return ExternalSourceCache.WriteReadOnly(target, content) ? target : null;
    }

    /// <summary>
    /// Downloads the file, or returns the copy already on disk. The cache key is the URL, which
    /// for a Source Link map contains the commit, so two versions of a package never collide.
    /// </summary>
    private static async Task<string?> FetchAsync(
        string url, string documentPath, byte[] expectedHash, Guid algorithm, CancellationToken ct)
    {
        string cached = Path.Combine(
            CacheDirectory,
            ExternalSourceCache.Fingerprint(url),
            Path.GetFileName(documentPath.Replace('\\', '/')));
        // Kept in step with SourceLinkCachePath, which predicts this location from the map.

        if (File.Exists(cached))
            return cached;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        byte[]? content = await HttpFetch.GetAsync(uri, MaxDownloadBytes, ct).ConfigureAwait(false);
        if (content is null)
            return null;

        if (Reconcile(content, expectedHash, algorithm) is not { } verified)
        {
            ServiceLog.Warn(
                $"Source Link content for {Path.GetFileName(documentPath)} did not match the " +
                "checksum in the PDB; falling back to decompilation.",
                key: $"sourcelink-hash:{url}");
            return null;
        }

        return ExternalSourceCache.WriteReadOnly(cached, verified) ? cached : null;
    }

    /// <summary>
    /// Whether the downloaded bytes are the bytes the assembly was compiled from. An unknown
    /// algorithm or an absent hash is treated as a mismatch — the point of the check is to not
    /// show source that might be wrong.
    /// </summary>
    internal static bool Matches(byte[] content, byte[] expected, Guid algorithm)
    {
        if (expected.Length == 0)
            return false;

        byte[] actual;
        if (algorithm == Sha256)
            actual = SHA256.HashData(content);
        else if (algorithm == Sha1)
            actual = SHA1.HashData(content);
        else
            return false;

        return actual.AsSpan().SequenceEqual(expected);
    }

    /// <summary>
    /// The form of <paramref name="content"/> that the PDB's checksum attests to, or null when no
    /// form of it does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A checksum is over bytes, but the bytes a build machine compiled are not always the bytes a
    /// host serves. .NET's own repositories are built on Windows with git's line-ending conversion
    /// on, so every checksum in the framework's PDBs is over CRLF text — while
    /// <c>raw.githubusercontent.com</c> serves the LF bytes that are actually committed. Comparing
    /// strictly rejects the genuine article for every file in the BCL, which is the difference
    /// between this feature working and not.
    /// </para>
    /// <para>
    /// This is not a weakening of the check. Each candidate is still required to hash to exactly
    /// what the PDB recorded; all that is admitted is that the same text can be spelled with
    /// different line terminators. Line numbers are identical across the variants, so navigation
    /// is unaffected, and the variant that actually matches is the one written to the cache.
    /// </para>
    /// </remarks>
    internal static byte[]? Reconcile(byte[] content, byte[] expected, Guid algorithm)
    {
        foreach (byte[] candidate in Variants(content))
        {
            if (Matches(candidate, expected, algorithm))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<byte[]> Variants(byte[] content)
    {
        // Overwhelmingly the common case, and the only one most packages need.
        yield return content;

        bool hasBom = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF;
        byte[] body = hasBom ? content[3..] : content;

        string text = System.Text.Encoding.UTF8.GetString(body);
        string lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        string crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        foreach (string spelling in (string[])[crlf, lf])
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(spelling);
            yield return hasBom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes;

            // The build machine may also have disagreed about the byte order mark.
            yield return hasBom ? bytes : [0xEF, 0xBB, 0xBF, .. bytes];
        }
    }

    /// <summary>Identifies a build of an assembly, so a rebuild does not reuse stale lookups.</summary>
    private static (string Assembly, long Stamp) CacheKey(string assemblyPath)
    {
        long stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(assemblyPath).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stamp = 0;
        }

        return (assemblyPath, stamp);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
