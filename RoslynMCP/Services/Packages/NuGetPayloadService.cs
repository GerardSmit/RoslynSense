using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Versioning;

namespace RoslynMCP.Services.Packages;

/// <summary>
/// The parts of a .nupkg that no feed API exposes: the embedded icon, README and license file.
/// </summary>
public sealed record PackagePayload(
    byte[]? Icon,
    string? IconMediaType,
    string? Readme,
    string? LicenseText,
    string? LicenseExpression);

/// <summary>
/// Gets a package's own bytes when the feed's metadata is not enough.
/// </summary>
/// <remarks>
/// The V3 registration index carries a description and a license expression but not the embedded
/// icon, the README file, or a license shipped as a file — and a private feed frequently exposes
/// no icon URL at all. Those all live inside the .nupkg, so it is fetched once per package
/// version and read for every one of them at the same time.
///
/// The global packages folder is checked first and used in place. On a restored solution that
/// means the details pane and the Installed tab's icons cost no network at all.
/// </remarks>
public static class NuGetPayloadService
{
    /// <summary>A package larger than this is a payload we have no business downloading to read a README.</summary>
    private const long MaxNupkgBytes = 32L * 1024 * 1024;

    /// <summary>Text longer than this is truncated: a README is for reading, not for streaming.</summary>
    private const int MaxTextBytes = 256 * 1024;

    public static string CacheDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "RoslynMCP", "NuGet");

    private static readonly ConcurrentDictionary<string, Lazy<Task<PackagePayload?>>> s_payloads =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Everything worth reading out of a package, resolved once per version.</summary>
    public static Task<PackagePayload?> ReadAsync(string id, string version, CancellationToken ct)
    {
        string key = $"{id}/{version}".ToLowerInvariant();

        var entry = s_payloads.GetOrAdd(key, _ => new Lazy<Task<PackagePayload?>>(
            () => LoadAsync(id, version, ct), LazyThreadSafetyMode.ExecutionAndPublication));

        var task = entry.Value;

        // A transient failure must not be cached for the life of the daemon — and "failure" here
        // includes a null result, because a feed hiccup and "this package has no README" are the
        // same value. Only a payload that was actually read is worth keeping.
        if (task.IsCompleted && (!task.IsCompletedSuccessfully || task.Result is null))
        {
            s_payloads.TryRemove(key, out _);
            return task.IsCompletedSuccessfully ? task : Task.FromResult<PackagePayload?>(null);
        }

        return task;
    }

    /// <summary>The .nupkg on disk, from the global packages folder, the cache, or a feed.</summary>
    public static async Task<string?> EnsureNupkgAsync(string id, string version, CancellationToken ct)
    {
        if (!NuGetVersion.TryParse(version, out var parsed))
            return null;

        string normalized = parsed.ToNormalizedString();

        if (FromGlobalPackagesFolder(id, normalized) is { } restored)
            return restored;

        string directory = Path.Combine(CacheDirectory, "payload", Fingerprint($"{id}/{normalized}"));
        string target = Path.Combine(directory, $"{id}.{normalized}.nupkg");
        if (File.Exists(target))
            return target;

        return await DownloadAsync(id, parsed, directory, target, ct) ? target : null;
    }

    private static string? FromGlobalPackagesFolder(string id, string normalizedVersion)
    {
        try
        {
            if (NuGetFeedContext.GlobalPackagesFolder() is not { Length: > 0 } root)
                return null;

            string candidate = Path.Combine(
                root,
                id.ToLowerInvariant(),
                normalizedVersion.ToLowerInvariant(),
                $"{id.ToLowerInvariant()}.{normalizedVersion.ToLowerInvariant()}.nupkg");

            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> DownloadAsync(
        string id, NuGetVersion version, string directory, string target, CancellationToken ct)
    {
        string? temporary = null;
        try
        {
            var resource = await NuGetService.FindPackageResourceAsync(id, ct);
            if (resource is null)
                return false;

            Directory.CreateDirectory(directory);
            temporary = target + ".partial";

            using var cache = NuGetFeedContext.RentCache();
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                bool found = await resource.CopyNupkgToStreamAsync(
                    id, version, stream, cache, NullLogger.Instance, ct);
                if (!found)
                    return false;
            }

            if (new FileInfo(temporary).Length > MaxNupkgBytes)
                return false;

            File.Move(temporary, target, overwrite: true);
            temporary = null;
            return true;
        }
        // A cancelled download is the caller moving on, not a package that could not be found.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not download {id} {version}: {ex.Message}", key: $"nuget-payload:{id}");
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    private static async Task<PackagePayload?> LoadAsync(string id, string version, CancellationToken ct)
    {
        string? nupkg = await EnsureNupkgAsync(id, version, ct);
        if (nupkg is null)
            return null;

        // NuGet.Packaging's readers sit alongside NuGet.Frameworks, which resolves only through
        // MSBuildLocator's assembly resolver.
        WorkspaceService.EnsureRegistered();
        return Read(nupkg);
    }

    // NoInlining: the JIT resolves a method's types when it prepares the method, so inlining
    // this into an async caller would load NuGet.Packaging before EnsureRegistered() had run.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PackagePayload? Read(string nupkgPath)
    {
        try
        {
            // Entries are read in place and never extracted, which is what keeps a crafted
            // package from writing outside a directory the way an unpacking installer can.
            using var archive = ZipFile.OpenRead(nupkgPath);
            using var reader = new PackageArchiveReader(nupkgPath);
            var nuspec = reader.NuspecReader;

            var (icon, mediaType) = ReadIcon(archive, nuspec.GetIcon());
            var license = nuspec.GetLicenseMetadata();

            return new PackagePayload(
                icon,
                mediaType,
                ReadText(archive, nuspec.GetReadme()),
                license is { Type: LicenseType.File } ? ReadText(archive, license.License) : null,
                license is { Type: LicenseType.Expression } ? license.License : null);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not read '{Path.GetFileName(nupkgPath)}': {ex.Message}", key: "nuget-payload-read");
            return null;
        }
    }

    private static (byte[]? Bytes, string? MediaType) ReadIcon(ZipArchive archive, string? entryName)
    {
        if (Entry(archive, entryName) is not { } entry || entry.Length > MaxTextBytes * 4)
            return (null, null);

        try
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return (buffer.ToArray(), MediaTypeFor(entryName!));
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? ReadText(ZipArchive archive, string? entryName)
    {
        if (Entry(archive, entryName) is not { } entry)
            return null;

        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var buffer = new char[MaxTextBytes];
            int read = reader.ReadBlock(buffer, 0, buffer.Length);
            string text = new(buffer, 0, read);

            return reader.EndOfStream ? text : text + "\n\n…";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A nuspec names entries with forward slashes; the archive may store either separator, and
    /// the comparison has to be case-insensitive because packages are built on both platforms.
    /// </summary>
    private static ZipArchiveEntry? Entry(ZipArchive archive, string? entryName)
    {
        if (entryName is not { Length: > 0 })
            return null;

        string normalized = entryName.Replace('\\', '/').TrimStart('/');
        return archive.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string MediaTypeFor(string entryName) =>
        Path.GetExtension(entryName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            _ => "image/png",
        };

    internal static string Fingerprint(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
}
