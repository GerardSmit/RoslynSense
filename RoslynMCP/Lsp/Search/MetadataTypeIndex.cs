using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Lsp.Search;

/// <summary>A public type read straight from an assembly's metadata tables.</summary>
/// <param name="Name">The simple name, backtick arity stripped — what a search matches on.</param>
/// <param name="Namespace">The containing namespace, empty for the global one.</param>
/// <param name="ReflectionName">The name the decompiler wants: namespace-qualified, nested
/// types joined with <c>+</c>, generic arity kept (<c>System.Collections.Generic.List`1</c>).</param>
public sealed record MetadataType(string Name, string Namespace, string ReflectionName);

/// <summary>
/// The public types of every assembly a solution references, for the "include non-solution
/// items" half of Search Everywhere.
/// </summary>
/// <remarks>
/// Read with <see cref="PEReader"/> rather than through a <see cref="Compilation"/>: the type
/// names live in the metadata tables, and reading them costs milliseconds per assembly where
/// asking Roslyn for an <c>IAssemblySymbol</c> costs a compilation. Cached per assembly path and
/// write time — reference assemblies change on package restore, not per keystroke.
/// </remarks>
public static class MetadataTypeIndex
{
    private static readonly ConcurrentDictionary<string, Entry> s_cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(DateTime WrittenUtc, IReadOnlyList<MetadataType> Types);

    /// <summary>Every referenced assembly of the solution, one entry per distinct path.</summary>
    public static IReadOnlyList<(string AssemblyPath, IReadOnlyList<MetadataType> Types)> ForSolution(
        Solution solution, CancellationToken ct)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            foreach (var reference in project.MetadataReferences)
            {
                if (reference is PortableExecutableReference { FilePath: { Length: > 0 } path })
                    paths.Add(path);
            }
        }

        var result = new List<(string, IReadOnlyList<MetadataType>)>(paths.Count);
        foreach (string path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var types = TypesOf(path);
            if (types.Count > 0)
                result.Add((path, types));
        }

        return result;
    }

    public static void Clear() => s_cache.Clear();

    private static IReadOnlyList<MetadataType> TypesOf(string assemblyPath)
    {
        DateTime writtenUtc;
        try
        {
            writtenUtc = File.GetLastWriteTimeUtc(assemblyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // One unreadable reference path must not fail the whole search — Read() below
            // already treats it that way.
            return [];
        }

        if (s_cache.TryGetValue(assemblyPath, out var cached) && cached.WrittenUtc == writtenUtc)
            return cached.Types;

        var types = Read(assemblyPath);
        s_cache[assemblyPath] = new Entry(writtenUtc, types);
        return types;
    }

    private static IReadOnlyList<MetadataType> Read(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return [];

            var reader = pe.GetMetadataReader();
            var types = new List<MetadataType>();

            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                var visibility = type.Attributes & TypeAttributes.VisibilityMask;

                // Public surface only: a search over every internal of every referenced dll is
                // noise the user cannot navigate into meaningfully.
                if (visibility is not (TypeAttributes.Public or TypeAttributes.NestedPublic))
                    continue;

                string name = reader.GetString(type.Name);
                if (name.Length == 0 || name[0] == '<' || name == "Module")
                    continue;

                string reflectionName;
                string ns;
                if (visibility == TypeAttributes.NestedPublic)
                {
                    if (ReflectionNameOfNested(reader, type) is not { } nested)
                        continue;
                    (reflectionName, ns) = nested;
                }
                else
                {
                    ns = reader.GetString(type.Namespace);
                    reflectionName = ns.Length == 0 ? name : $"{ns}.{name}";
                }

                types.Add(new MetadataType(StripArity(name), ns, reflectionName));
            }

            return types;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            // A native dll, a locked file, a half-written build output: not an index entry.
            return [];
        }
    }

    /// <summary>Walks the declaring chain: <c>Ns.Outer`1+Inner</c>, namespace of the outermost.</summary>
    private static (string ReflectionName, string Namespace)? ReflectionNameOfNested(
        MetadataReader reader, TypeDefinition type)
    {
        var names = new Stack<string>();
        names.Push(reader.GetString(type.Name));

        var current = type;
        for (int depth = 0; depth < 16; depth++)
        {
            var declaringHandle = current.GetDeclaringType();
            if (declaringHandle.IsNil)
            {
                string ns = reader.GetString(current.Namespace);
                string outermost = names.Pop();
                string qualified = ns.Length == 0 ? outermost : $"{ns}.{outermost}";
                return (names.Count == 0 ? qualified : $"{qualified}+{string.Join('+', names)}", ns);
            }

            current = reader.GetTypeDefinition(declaringHandle);
            string declaringName = reader.GetString(current.Name);
            if (declaringName.Length == 0 || declaringName[0] == '<')
                return null;

            // Every level of the chain must itself be visible, or the walk gives a name the
            // decompiler shows as an empty shell.
            var visibility = current.Attributes & TypeAttributes.VisibilityMask;
            if (visibility is not (TypeAttributes.Public or TypeAttributes.NestedPublic))
                return null;

            names.Push(declaringName);
        }

        return null;
    }

    /// <summary>"List`1" is searched as "List"; the reflection name keeps the backtick.</summary>
    private static string StripArity(string name)
    {
        int tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }
}
