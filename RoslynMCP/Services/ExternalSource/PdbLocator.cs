using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using RoslynMCP.Config;

namespace RoslynMCP.Services.ExternalSource;

/// <summary>Where the PDB backing a navigation came from.</summary>
internal enum PdbOrigin
{
    /// <summary>Carried inside the assembly itself.</summary>
    Embedded,

    /// <summary>Found on disk beside the assembly, or where its debug directory said.</summary>
    Local,

    /// <summary>Downloaded from a symbol server.</summary>
    SymbolServer,
}

/// <summary>An open portable PDB and where it was found. Disposing it closes the reader.</summary>
internal sealed class PdbHandle(MetadataReaderProvider provider, PdbOrigin origin, string? path)
    : IDisposable
{
    public MetadataReaderProvider Provider { get; } = provider;

    public PdbOrigin Origin { get; } = origin;

    /// <summary>The file it was read from, or null when it was embedded in the assembly.</summary>
    public string? Path { get; } = path;

    public void Dispose() => Provider.Dispose();
}

/// <summary>
/// Finds the portable PDB for an assembly, looking further afield than the file next to it.
/// </summary>
/// <remarks>
/// <para>
/// This is what stands between Source Link and the .NET framework. The shared framework ships no
/// PDBs at all — <c>Microsoft.NETCore.App</c> is a directory of DLLs and nothing else — so the
/// usual "open the PDB beside the assembly" lookup fails for every BCL type, which is why F12
/// into <c>System.String</c> has always decompiled. The symbols exist; they are on Microsoft's
/// symbol server, keyed by an identity the assembly itself carries.
/// </para>
/// <para>
/// Nothing here trusts the server. The identity in the debug directory is checked against the one
/// inside the downloaded PDB, so a name collision or a substituted file is rejected rather than
/// used to point navigation at the wrong source.
/// </para>
/// </remarks>
internal static class PdbLocator
{
    /// <summary>Portable PDBs are large; CoreLib's is around 14 MB.</summary>
    private const long MaxPdbBytes = 200L * 1024 * 1024;

    /// <summary>How long a missing PDB stays missing before the servers are asked again.</summary>
    private static readonly TimeSpan NotFoundMemo = TimeSpan.FromDays(7);

    /// <summary>
    /// Tried in order. msdl carries the framework and Microsoft's own packages; the NuGet symbol
    /// server carries whatever publishers pushed a symbol package for.
    /// </summary>
    private static readonly string[] s_symbolServers =
    [
        "https://msdl.microsoft.com/download/symbols",
        "https://symbols.nuget.org/download/symbols",
    ];

    /// <summary>SSQP keys already known to be absent, so a miss costs nothing the second time.</summary>
    private static readonly ConcurrentDictionary<string, byte> s_missing = new();

    /// <summary>One download per key at a time, so ten navigations do not fetch the same PDB.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_downloadGates = new();

    /// <summary>
    /// Opens the PDB for an assembly: embedded, then local, then downloaded.
    /// </summary>
    /// <param name="peReader">Reader over <paramref name="assemblyPath"/>, left open.</param>
    /// <returns>An open PDB, or null when there is none to be had. The caller disposes it.</returns>
    public static async Task<PdbHandle?> OpenAsync(
        PEReader peReader, string assemblyPath, CancellationToken ct)
    {
        var entries = peReader.ReadDebugDirectory();

        if (TryOpenEmbedded(peReader, entries) is { } embedded)
            return embedded;

        if (TryOpenLocal(peReader, assemblyPath) is { } local)
            return local;

        if (!LspFeatureOptions.SymbolServer || !LspFeatureOptions.ExternalSource)
            return null;

        return await TryDownloadAsync(peReader, entries, assemblyPath, ct).ConfigureAwait(false);
    }

    private static PdbHandle? TryOpenEmbedded(
        PEReader peReader, IReadOnlyList<DebugDirectoryEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Type != DebugDirectoryEntryType.EmbeddedPortablePdb)
                continue;

            try
            {
                return new PdbHandle(
                    peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry),
                    PdbOrigin.Embedded,
                    path: null);
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException)
            {
                return null;
            }
        }

        return null;
    }

    private static PdbHandle? TryOpenLocal(PEReader peReader, string assemblyPath)
    {
        try
        {
            if (peReader.TryOpenAssociatedPortablePdb(
                    assemblyPath,
                    path => File.Exists(path) ? File.OpenRead(path) : null,
                    out var provider,
                    out string? pdbPath)
                && provider is not null)
            {
                return new PdbHandle(provider, PdbOrigin.Local, pdbPath);
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            // No usable PDB beside the assembly; the symbol server is the next question.
        }

        return null;
    }

    private static async Task<PdbHandle?> TryDownloadAsync(
        PEReader peReader,
        IReadOnlyList<DebugDirectoryEntry> entries,
        string assemblyPath,
        CancellationToken ct)
    {
        if (ReadPortableCodeView(peReader, entries) is not { } codeView)
            return null;

        string pdbFileName = Path.GetFileName(codeView.Path.Replace('\\', '/'));
        if (pdbFileName.Length == 0)
            return null;

        // The SSQP key for a portable PDB is its GUID and age, which is exactly the identity
        // stamped into the PDB itself — so the answer can be checked rather than trusted.
        string cacheKey = SsqpKey(codeView);
        string cached = Path.Combine(
            ExternalSourceCache.SymbolDirectory, PortableIdentity(codeView), pdbFileName);
        if (File.Exists(cached))
            return OpenVerified(cached, codeView);

        if (s_missing.ContainsKey(cacheKey) || IsRecordedMissing(cached))
            return null;

        var gate = s_downloadGates.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another navigation may have fetched it while we waited.
            if (File.Exists(cached))
                return OpenVerified(cached, codeView);

            if (s_missing.ContainsKey(cacheKey))
                return null;

            ServiceLog.Info(
                $"Fetching symbols for {Path.GetFileNameWithoutExtension(assemblyPath)} so its real " +
                "source can be shown; this happens once per assembly.");

            foreach (string server in s_symbolServers)
            {
                ct.ThrowIfCancellationRequested();

                if (!Uri.TryCreate($"{server}/{cacheKey}", UriKind.Absolute, out var uri))
                    continue;

                byte[]? content = await HttpFetch.GetAsync(uri, MaxPdbBytes, ct).ConfigureAwait(false);
                if (content is null)
                    continue;

                if (!ExternalSourceCache.WriteReadOnly(cached, content))
                    return null;

                if (OpenVerified(cached, codeView) is { } opened)
                    return opened;

                // The bytes are not the PDB we asked for. Do not keep them.
                ExternalSourceCache.ClearReadOnly(cached);
                TryDelete(cached);
            }

            RecordMissing(cacheKey, cached);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The CodeView entry describing the <em>portable</em> PDB.
    /// </summary>
    /// <remarks>
    /// A ReadyToRun assembly carries two CodeView entries and the native one comes first: for
    /// <c>System.Private.CoreLib</c> that is <c>System.Private.CoreLib.ni.pdb</c>, which has no
    /// Source Link and no managed sequence points. Taking the first entry silently fetches many
    /// megabytes of the wrong thing, so the portable flag decides it.
    /// </remarks>
    internal static CodeViewDebugDirectoryData? ReadPortableCodeView(
        PEReader peReader, IReadOnlyList<DebugDirectoryEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Type != DebugDirectoryEntryType.CodeView || !entry.IsPortableCodeView)
                continue;

            try
            {
                var codeView = peReader.ReadCodeViewDebugDirectoryData(entry);
                return codeView.Path is null ? null : codeView;
            }
            catch (BadImageFormatException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The symbol-server path for a portable PDB: <c>name/{guid:N}ffffffff/name</c>.
    /// </summary>
    /// <remarks>
    /// The literal <c>ffffffff</c> is not the age. A symbol server indexes two different files
    /// under the same GUID — the portable PDB the compiler produced, and a Windows PDB converted
    /// from it for native debuggers — and distinguishes them by putting the converted one under
    /// the real age and the portable one under this sentinel. Asking with the real age returns an
    /// MSF file that <see cref="MetadataReaderProvider"/> cannot read at all, which is what the
    /// framework assemblies serve: 14 MB of the wrong format instead of 50 KB of the right one.
    /// </remarks>
    internal static string SsqpKey(CodeViewDebugDirectoryData codeView)
    {
        string pdbFileName = Path.GetFileName(codeView.Path.Replace('\\', '/'));
        return $"{pdbFileName}/{PortableIdentity(codeView)}/{pdbFileName}";
    }

    private static string PortableIdentity(CodeViewDebugDirectoryData codeView) =>
        $"{codeView.Guid:N}ffffffff";

    private static PdbHandle? OpenVerified(string path, CodeViewDebugDirectoryData expected)
    {
        try
        {
            var stream = File.OpenRead(path);
            var provider = MetadataReaderProvider.FromPortablePdbStream(stream);

            if (!IdentityMatches(provider, expected))
            {
                provider.Dispose();
                return null;
            }

            return new PdbHandle(provider, PdbOrigin.SymbolServer, path);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a PDB is the one the assembly names.
    /// </summary>
    /// <remarks>
    /// A portable PDB's id is twenty bytes: the GUID, then a four-byte stamp that is <em>not</em>
    /// the CodeView age — the age of a portable entry is always 1, while the stamp lives in the
    /// debug directory entry. Only the GUID is the identity, so only the GUID is compared.
    /// </remarks>
    private static bool IdentityMatches(MetadataReaderProvider provider, CodeViewDebugDirectoryData expected)
    {
        var header = provider.GetMetadataReader().DebugMetadataHeader;
        if (header is null)
            return false;

        var id = header.Id;
        if (id.Length < 16)
            return false;

        Span<byte> wanted = stackalloc byte[16];
        return expected.Guid.TryWriteBytes(wanted) && id.AsSpan()[..16].SequenceEqual(wanted);
    }

    private static string NotFoundMarker(string cachedPdbPath) => cachedPdbPath + ".notfound";

    private static bool IsRecordedMissing(string cachedPdbPath)
    {
        string marker = NotFoundMarker(cachedPdbPath);
        try
        {
            if (!File.Exists(marker))
                return false;

            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < NotFoundMemo)
                return true;

            File.Delete(marker);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Remembers that no server has this PDB. On disk as well as in memory, because the framework
    /// assemblies have no symbols on some feeds and a restart should not re-ask for all of them.
    /// </summary>
    private static void RecordMissing(string cacheKey, string cachedPdbPath)
    {
        s_missing.TryAdd(cacheKey, 0);

        try
        {
            string marker = NotFoundMarker(cachedPdbPath);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllBytes(marker, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory note still saves the repeat within this session.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale cache entry is caught by the identity check on the next open.
        }
    }

    /// <summary>Forgets which PDBs are known missing. For tests.</summary>
    internal static void ResetMissing() => s_missing.Clear();
}
