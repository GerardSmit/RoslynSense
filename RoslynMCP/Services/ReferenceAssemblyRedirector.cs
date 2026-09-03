using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RoslynMCP.Services;

/// <summary>
/// Maps a *reference assembly* path (SDK ref pack / nuget ref folder — compile-time surface
/// with all bodies compiled as <c>throw null</c>) to the runtime implementation assembly that
/// actually defines a given type, following type forwarders. Decompiling the reference
/// assembly is what produces the notorious "everything is throw null" output.
/// </summary>
internal static class ReferenceAssemblyRedirector
{
    private const int MaxForwardHops = 4;

    public static string RedirectToImplementation(string assemblyPath, string reflectionTypeName)
    {
        try
        {
            if (!IsReferenceAssembly(assemblyPath))
                return assemblyPath;

            string? implementationDir = FindImplementationDirectory(assemblyPath);
            if (implementationDir is null)
                return assemblyPath;

            // Forwarders are declared on top-level types.
            string topLevelType = reflectionTypeName.Split('+')[0];

            string candidate = Path.Combine(implementationDir, Path.GetFileName(assemblyPath));
            for (int hop = 0; hop < MaxForwardHops; hop++)
            {
                if (!File.Exists(candidate))
                    return assemblyPath;

                switch (LocateType(candidate, topLevelType))
                {
                    case (Defined: true, _):
                        return candidate;
                    case (_, ForwardedTo: { Length: > 0 } forwarded):
                        candidate = Path.Combine(implementationDir, forwarded + ".dll");
                        continue;
                    default:
                        return assemblyPath;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DecompiledSourceService] Implementation redirect failed: {ex.Message}");
        }
        return assemblyPath;
    }

    private static bool IsReferenceAssembly(string assemblyPath)
    {
        // Cheap path heuristics first; fall back to the assembly-level marker attribute.
        string normalized = assemblyPath.Replace('\\', '/');
        if (normalized.Contains("/ref/", StringComparison.OrdinalIgnoreCase))
            return true;

        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
                continue;
            var ctor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (ctor.Parent.Kind != HandleKind.TypeReference)
                continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)ctor.Parent);
            if (reader.GetString(type.Name) == "ReferenceAssemblyAttribute")
                return true;
        }
        return false;
    }

    /// <summary>Implementation directory for a reference assembly:
    /// - SDK ref pack:  …/packs/Microsoft.NETCore.App.Ref/{v}/ref/{tfm}/  →  …/shared/Microsoft.NETCore.App/{v'}/
    /// - nuget package: …/package/{v}/ref/{tfm}/  →  …/package/{v}/lib/{tfm}/ (or best available lib TFM)
    /// </summary>
    private static string? FindImplementationDirectory(string assemblyPath)
    {
        string normalized = Path.GetFullPath(assemblyPath);
        var parts = normalized.Split(Path.DirectorySeparatorChar);

        int packsIndex = Array.FindIndex(parts, p => p.Equals("packs", StringComparison.OrdinalIgnoreCase));
        if (packsIndex >= 0 && packsIndex + 2 < parts.Length
            && parts[packsIndex + 1].EndsWith(".Ref", StringComparison.OrdinalIgnoreCase))
        {
            string dotnetRoot = string.Join(Path.DirectorySeparatorChar, parts.Take(packsIndex));
            string sharedName = parts[packsIndex + 1][..^".Ref".Length];
            string refVersion = parts[packsIndex + 2];
            string sharedRoot = Path.Combine(dotnetRoot, "shared", sharedName);
            if (!Directory.Exists(sharedRoot))
                return null;

            // Exact version if present, otherwise the highest same major.minor.
            string exact = Path.Combine(sharedRoot, refVersion);
            if (Directory.Exists(exact))
                return exact;
            string prefix = string.Join('.', refVersion.Split('.').Take(2)) + ".";
            return Directory.EnumerateDirectories(sharedRoot)
                .Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.Ordinal))
                .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0))
                .FirstOrDefault();
        }

        int refIndex = Array.FindLastIndex(parts, p => p.Equals("ref", StringComparison.OrdinalIgnoreCase));
        if (refIndex > 0)
        {
            string packageRoot = string.Join(Path.DirectorySeparatorChar, parts.Take(refIndex));
            string tfm = refIndex + 1 < parts.Length - 1 ? parts[refIndex + 1] : "";
            string lib = Path.Combine(packageRoot, "lib");
            if (Directory.Exists(Path.Combine(lib, tfm)))
                return Path.Combine(lib, tfm);
            if (Directory.Exists(lib))
                return Directory.EnumerateDirectories(lib).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
        }
        return null;
    }

    /// <summary>Checks whether the assembly defines the type itself or forwards it elsewhere.</summary>
    private static (bool Defined, string? ForwardedTo) LocateType(string assemblyPath, string fullTypeName)
    {
        int lastDot = fullTypeName.LastIndexOf('.');
        string ns = lastDot < 0 ? "" : fullTypeName[..lastDot];
        string name = lastDot < 0 ? fullTypeName : fullTypeName[(lastDot + 1)..];

        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (!type.GetDeclaringType().IsNil)
                continue;
            if (reader.GetString(type.Name) == name && reader.GetString(type.Namespace) == ns)
                return (true, null);
        }

        foreach (var handle in reader.ExportedTypes)
        {
            var exported = reader.GetExportedType(handle);
            if (!exported.IsForwarder)
                continue;
            if (reader.GetString(exported.Name) != name || reader.GetString(exported.Namespace) != ns)
                continue;
            if (exported.Implementation.Kind == HandleKind.AssemblyReference)
            {
                var target = reader.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation);
                return (false, reader.GetString(target.Name));
            }
        }
        return (false, null);
    }
}
