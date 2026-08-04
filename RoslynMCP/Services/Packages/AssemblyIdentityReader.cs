using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace RoslynMCP.Services.Packages;

/// <param name="PublicKeyToken">Lowercase hex, or <c>null</c> when the assembly is not strong-named.</param>
public sealed record AssemblyIdentityInfo(
    string Name,
    Version Version,
    string? PublicKeyToken,
    string Culture)
{
    /// <summary>The identity a binding redirect is keyed on — everything but the version.</summary>
    public string Key => $"{Name}|{PublicKeyToken ?? ""}|{Culture}";

    public override string ToString() =>
        $"{Name}, Version={Version}, Culture={Culture}, PublicKeyToken={PublicKeyToken ?? "null"}";
}

/// <param name="References">What the assembly expects to bind to at runtime.</param>
public sealed record AssemblyFileInfo(
    string Path,
    AssemblyIdentityInfo Identity,
    ImmutableArray<AssemblyIdentityInfo> References);

/// <summary>
/// Assembly identities read out of the file, without loading anything.
/// </summary>
/// <remarks>
/// <see cref="System.Reflection.Assembly"/> is the wrong tool here twice over: loading holds the
/// file open for the life of the process — a daemon that analyzed a project could never build it
/// again — and a load resolves against *this* process's binding rules, which is precisely the thing
/// under examination. The metadata reader answers what is on disk.
/// </remarks>
public static class AssemblyIdentityReader
{
    /// <returns><c>null</c> when the file is not a managed assembly, or cannot be read.</returns>
    public static AssemblyFileInfo? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);

            if (!pe.HasMetadata)
                return null;

            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
                return null;

            var definition = metadata.GetAssemblyDefinition();

            var identity = new AssemblyIdentityInfo(
                metadata.GetString(definition.Name),
                definition.Version,
                TokenFromPublicKey(metadata.GetBlobBytes(definition.PublicKey)),
                CultureOf(metadata.GetString(definition.Culture)));

            var references = ImmutableArray.CreateBuilder<AssemblyIdentityInfo>();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);

                references.Add(new AssemblyIdentityInfo(
                    metadata.GetString(reference.Name),
                    reference.Version,
                    // A reference carries the token directly rather than the full key.
                    TokenFromBytes(metadata.GetBlobBytes(reference.PublicKeyOrToken)),
                    CultureOf(metadata.GetString(reference.Culture))));
            }

            return new AssemblyFileInfo(path, identity, references.ToImmutable());
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            // A native dll in a bin folder is normal, and a file being written while a build runs
            // is not this feature's problem to report.
            return null;
        }
    }

    /// <summary>
    /// The token is the last eight bytes of the key's SHA-1, reversed — the identity a config file
    /// names, which is never the key itself.
    /// </summary>
    private static string? TokenFromPublicKey(byte[] publicKey)
    {
        if (publicKey.Length == 0)
            return null;

        byte[] hash = SHA1.HashData(publicKey);
        return Convert.ToHexStringLower(hash.AsSpan(hash.Length - 8).ToArray().Reverse().ToArray());
    }

    private static string? TokenFromBytes(byte[] tokenOrKey) => tokenOrKey.Length switch
    {
        0 => null,
        8 => Convert.ToHexStringLower(tokenOrKey),
        _ => TokenFromPublicKey(tokenOrKey),
    };

    /// <summary>The invariant culture is written <c>neutral</c> in a config file, not empty.</summary>
    private static string CultureOf(string culture) =>
        culture.Length == 0 ? "neutral" : culture;
}
