using System.Collections.Concurrent;
using System.Reflection;

namespace RoslynMCP.Services.MetadataConfiguration;

/// <summary>Finds IL for explicitly referenced .NET Framework targeting-pack assemblies.</summary>
/// <remarks>
/// The installed, serviced runtime can differ from the project's target patch level. Its IL is
/// useful for external configuration reads, but must never replace the compilation's references.
/// Only the named assembly is looked up; this does not scan the runtime or the whole GAC.
/// </remarks>
internal static class FrameworkImplementationAssembly
{
    private static readonly ConcurrentDictionary<(string Path, long Stamp), string> s_cache = new();

    public static void Clear() => s_cache.Clear();

    public static string Resolve(string referencePath)
    {
        if (!OperatingSystem.IsWindows() || !IsFrameworkReference(referencePath))
            return referencePath;

        try
        {
            return s_cache.GetOrAdd((referencePath, File.GetLastWriteTimeUtc(referencePath).Ticks),
                key => Resolve(key.Path, Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return referencePath;
        }
    }

    internal static bool IsFrameworkReference(string path) =>
        path.Replace('\\', '/').Contains("/.NETFramework/v", StringComparison.OrdinalIgnoreCase);

    // The explicit Windows root also lets tests exercise resolution without depending on an
    // installed targeting pack or changing process-wide environment variables.
    internal static string Resolve(string referencePath, string windowsDirectory)
    {
        if (!IsFrameworkReference(referencePath) || string.IsNullOrEmpty(windowsDirectory))
            return referencePath;

        try
        {
            var identity = AssemblyName.GetAssemblyName(referencePath);
            if (identity.GetPublicKeyToken() is not { Length: > 0 })
                return referencePath;

            foreach (var candidate in Candidates(referencePath, identity, windowsDirectory))
            {
                try
                {
                    if (File.Exists(candidate) && SameIdentity(identity, AssemblyName.GetAssemblyName(candidate)))
                        return candidate;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
                {
                    // A missing, native, or inaccessible candidate must not hide other copies.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
        }

        return referencePath;
    }

    internal static bool SameIdentity(AssemblyName reference, AssemblyName implementation) =>
        string.Equals(reference.Name, implementation.Name, StringComparison.OrdinalIgnoreCase)
        && reference.Version == implementation.Version
        && string.Equals(reference.CultureName ?? "", implementation.CultureName ?? "", StringComparison.OrdinalIgnoreCase)
        && reference.GetPublicKeyToken() is { Length: > 0 } token
        && implementation.GetPublicKeyToken() is { } otherToken
        && token.AsSpan().SequenceEqual(otherToken);

    private static IEnumerable<string> Candidates(string referencePath, AssemblyName identity, string windowsDirectory)
    {
        string file = Path.GetFileName(referencePath);
        string runtime = referencePath.Replace('\\', '/').Contains("/.NETFramework/v4", StringComparison.OrdinalIgnoreCase)
            ? "v4.0.30319" : "v2.0.50727";

        foreach (string architecture in new[] { "Framework", "Framework64" })
            yield return Path.Combine(windowsDirectory, "Microsoft.NET", architecture, runtime, file);

        foreach (string root in new[] { Path.Combine(windowsDirectory, "Microsoft.NET", "assembly"), Path.Combine(windowsDirectory, "assembly") })
        foreach (string architecture in new[] { "GAC_MSIL", "GAC_32", "GAC_64", "GAC" })
        {
            string directory = Path.Combine(root, architecture, identity.Name!);
            string[] versions;
            try
            {
                versions = Directory.Exists(directory) ? Directory.GetDirectories(directory) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string version in versions.Order(StringComparer.OrdinalIgnoreCase))
                yield return Path.Combine(version, file);
        }
    }
}
