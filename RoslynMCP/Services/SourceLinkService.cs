using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services;

/// <summary>Where a metadata symbol's real source lives, once it has been fetched.</summary>
public sealed record SourceLinkResult(string FilePath, int Line, string Url);

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

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Hosts that answered, so one unreachable feed does not slow every navigation.</summary>
    private static readonly HashSet<string> s_failedHosts = [];

    private static readonly SemaphoreSlim s_gate = new(1, 1);

    public static string CacheDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "RoslynMCP", "SourceLink");

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
            if (AssemblyPath(symbol, project) is not { } assemblyPath)
                return null;

            var located = Locate(symbol, assemblyPath);
            if (located is not var (documentPath, line, url, hash, algorithm) || url is null)
                return null;

            string? local = await FetchAsync(url, documentPath, hash, algorithm, ct);
            return local is null ? null : new SourceLinkResult(local, line, url);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Source Link lookup failed: {ex.Message}", key: "sourcelink");
            return null;
        }
    }

    /// <summary>The file the compiler read this symbol from, and where to get it.</summary>
    private static (string DocumentPath, int Line, string? Url, byte[] Hash, Guid Algorithm)? Locate(
        ISymbol symbol, string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);

        if (!peReader.TryOpenAssociatedPortablePdb(
                assemblyPath,
                path => File.Exists(path) ? File.OpenRead(path) : null,
                out var pdbProvider,
                out _)
            || pdbProvider is null)
        {
            return null;
        }

        using (pdbProvider)
        {
            var pdb = pdbProvider.GetMetadataReader();
            string? map = ReadSourceLinkMap(pdb);
            if (map is null)
                return null;

            var metadata = peReader.GetMetadataReader();
            var methods = MethodsOf(symbol, metadata);
            if (methods.IsEmpty)
                return null;

            // The declaration is the earliest line any of the symbol's methods was compiled
            // from — for a type that is close to its own declaration, and for a method it is
            // its signature.
            (DocumentHandle Document, int Line)? best = null;
            foreach (var handle in methods)
            {
                var debugInformation = pdb.GetMethodDebugInformation(
                    MetadataTokens.MethodDebugInformationHandle(
                        MetadataTokens.GetRowNumber(handle)));

                foreach (var point in debugInformation.GetSequencePoints())
                {
                    if (point.IsHidden)
                        continue;
                    if (best is null || point.StartLine < best.Value.Line)
                        best = (point.Document, point.StartLine);
                    break;
                }
            }

            if (best is not var (documentHandle, line) || documentHandle.IsNil)
                return null;

            var document = pdb.GetDocument(documentHandle);
            string documentPath = pdb.GetString(document.Name);

            return (documentPath, line, ResolveUrl(map, documentPath),
                pdb.GetBlobBytes(document.Hash), pdb.GetGuid(document.HashAlgorithm));
        }
    }

    private static string? ReadSourceLinkMap(MetadataReader pdb)
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
        ISymbol symbol, MetadataReader metadata)
    {
        var containingType = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (containingType is null)
            return [];

        var typeDefinition = FindType(containingType, metadata);
        if (typeDefinition is not { } type)
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

    private static TypeDefinitionHandle? FindType(INamedTypeSymbol symbol, MetadataReader metadata)
    {
        string @namespace = symbol.ContainingNamespace?.IsGlobalNamespace == false
            ? symbol.ContainingNamespace.ToDisplayString()
            : "";

        // Nested types carry only their own name in metadata, with the nesting held separately.
        string name = symbol.MetadataName;
        var outer = symbol.ContainingType;

        foreach (var handle in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(handle);
            if (metadata.GetString(definition.Name) != name)
                continue;

            if (outer is null)
            {
                if (metadata.GetString(definition.Namespace) == @namespace)
                    return handle;
                continue;
            }

            var declaring = definition.GetDeclaringType();
            if (declaring.IsNil)
                continue;

            var parent = metadata.GetTypeDefinition(declaring);
            if (metadata.GetString(parent.Name) == outer.MetadataName)
                return handle;
        }

        return null;
    }

    private static string? AssemblyPath(ISymbol symbol, Project project)
    {
        if (symbol.ContainingAssembly is not { } assembly)
            return null;

        var compilation = project.GetCompilationAsync().GetAwaiter().GetResult();
        if (compilation?.GetMetadataReference(assembly) is not PortableExecutableReference reference)
            return null;

        return reference.FilePath is { Length: > 0 } path && File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Downloads the file, or returns the copy already on disk. The cache key is the URL, which
    /// for a Source Link map contains the commit, so two versions of a package never collide.
    /// </summary>
    private static async Task<string?> FetchAsync(
        string url, string documentPath, byte[] expectedHash, Guid algorithm, CancellationToken ct)
    {
        string cached = Path.Combine(
            CacheDirectory, Fingerprint(url), Path.GetFileName(documentPath.Replace('\\', '/')));

        if (File.Exists(cached))
            return cached;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            // http would let anything on the path serve the source that gets read as the
            // dependency's; the format's own convention is https.
            return null;
        }

        await s_gate.WaitAsync(ct);
        try
        {
            if (s_failedHosts.Contains(uri.Host))
                return null;
        }
        finally
        {
            s_gate.Release();
        }

        byte[] content;
        try
        {
            using var response = await s_http.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                return null;

            content = await response.Content.ReadAsByteArrayAsync(ct);
            if (content.Length > MaxDownloadBytes)
                return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await RememberFailureAsync(uri.Host);
            return null;
        }
        catch (HttpRequestException)
        {
            await RememberFailureAsync(uri.Host);
            return null;
        }

        if (!Matches(content, expectedHash, algorithm))
        {
            ServiceLog.Warn(
                $"Source Link content for {Path.GetFileName(documentPath)} did not match the " +
                "checksum in the PDB; falling back to decompilation.",
                key: $"sourcelink-hash:{url}");
            return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            await File.WriteAllBytesAsync(cached, content, ct);
            File.SetAttributes(cached, FileAttributes.ReadOnly);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return null;
        }

        return cached;
    }

    private static async Task RememberFailureAsync(string host)
    {
        await s_gate.WaitAsync();
        try { s_failedHosts.Add(host); }
        finally { s_gate.Release(); }
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

    private static string Fingerprint(string url) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..16];

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
