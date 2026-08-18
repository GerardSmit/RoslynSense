using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RoslynMCP.Services.ExternalSource;

/// <summary>
/// Which snapshot of <c>microsoft/referencesource</c> corresponds to a .NET Framework version.
/// </summary>
/// <remarks>
/// <para>
/// The repository is a series of drops rather than a live branch: each release was published as
/// one commit, so pinning a version means pinning a commit. There are only a handful, they will
/// never change, and .NET Framework itself is finished — a table is the honest representation.
/// </para>
/// <para>
/// A version with no entry gets no source. Serving the nearest snapshot instead would be worse
/// than decompiling: a decompilation is always of the assembly in hand, whereas source from the
/// wrong release looks authoritative while quietly describing different code.
/// </para>
/// </remarks>
internal static class ReferenceSourceCommitMap
{
    public const string Repository = "microsoft/referencesource";

    private static readonly Dictionary<string, string> s_commits = new(StringComparer.OrdinalIgnoreCase)
    {
        // 4.8 is the last release, and 4.8.1 shipped no new reference sources.
        ["net481"] = "74eb1593e09a636270482f1c0525aabdccb1f364",
        ["net48"] = "74eb1593e09a636270482f1c0525aabdccb1f364",
        ["net472"] = "3b1eaf5203992df69de44c783a3eda37d3d4cd10",
        ["net47"] = "4251daa76e0aae7330139978648fc04f5c7b8ccb",
        ["net462"] = "1acafe20a789a55daa17aac6bb47d1b0ec04519f",
        ["net461"] = "e458f8df6ded689323d4bd1a2a725ad32668aaec",
        ["net46"] = "ec178a5e7deb87a9cc7e0982ee32b7d965735b16",

        // 4.7.1 published no drop of its own, and 4.5.x predate the repository.
    };

    /// <summary>The commit for a target framework moniker, or null when none was published.</summary>
    public static string? CommitFor(string? tfm) =>
        tfm is { Length: > 0 } && s_commits.TryGetValue(tfm, out string? commit) ? commit : null;

    /// <summary>
    /// The framework version an assembly belongs to, as a moniker.
    /// </summary>
    /// <remarks>
    /// The path is asked first because it is the most specific thing available: a compilation
    /// references
    /// <c>...\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\System.dll</c>, which
    /// names the version exactly. The assembly's own <c>TargetFrameworkAttribute</c> is the
    /// fallback, since facades and the GAC copies are not laid out that way.
    /// </remarks>
    public static string? TfmForAssembly(string assemblyPath)
    {
        if (VersionFromPath(assemblyPath) is { } fromPath)
            return fromPath;

        return VersionFromAttribute(assemblyPath);
    }

    private static string? VersionFromPath(string assemblyPath)
    {
        // The version directory sits directly under a ".NETFramework" one.
        var directory = new DirectoryInfo(Path.GetDirectoryName(assemblyPath) ?? ".");

        for (var current = directory; current is not null; current = current.Parent)
        {
            if (current.Parent is { Name: ".NETFramework" } && current.Name.StartsWith('v'))
                return Moniker(current.Name);
        }

        return null;
    }

    private static string? VersionFromAttribute(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);

            if (!peReader.HasMetadata)
                return null;

            var metadata = peReader.GetMetadataReader();
            foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(handle);
                if (AttributeTypeName(metadata, attribute) != "TargetFrameworkAttribute")
                    continue;

                // The single fixed argument is ".NETFramework,Version=v4.7.2".
                var value = attribute.DecodeValue(new StringArgumentProvider());
                if (value.FixedArguments.Length == 0 || value.FixedArguments[0].Value is not string moniker)
                    continue;

                const string Marker = "Version=v";
                int at = moniker.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
                if (at < 0 || !moniker.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase))
                    return null;

                return Moniker(moniker[(at + Marker.Length)..]);
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            // An unreadable assembly simply has no version to offer.
        }

        return null;
    }

    /// <summary>Folds a framework version as written on disk into a moniker: <c>v4.7.2</c> to
    /// <c>net472</c>. Matches how project classification spells the same thing.</summary>
    internal static string? Moniker(string version)
    {
        string digits = version.Trim().TrimStart('v', 'V').Replace(".", "", StringComparison.Ordinal);
        return digits.Length == 0 ? null : "net" + digits;
    }

    private static string? AttributeTypeName(MetadataReader metadata, CustomAttribute attribute)
    {
        if (attribute.Constructor.Kind != HandleKind.MemberReference)
            return null;

        var member = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (member.Parent.Kind != HandleKind.TypeReference)
            return null;

        return metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)member.Parent).Name);
    }

    /// <summary>
    /// Decodes attribute arguments far enough to read a string. Only the first fixed argument of
    /// <c>TargetFrameworkAttribute</c> is ever wanted, so every other type is a placeholder.
    /// </summary>
    private sealed class StringArgumentProvider : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSystemType() => "System.Type";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            reader.GetString(reader.GetTypeDefinition(handle).Name);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            reader.GetString(reader.GetTypeReference(handle).Name);

        public string GetTypeFromSerializedName(string name) => name;

        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

        public bool IsSystemType(string type) => type == "System.Type";
    }
}
