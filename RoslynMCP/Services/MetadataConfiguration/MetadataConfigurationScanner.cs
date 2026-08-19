using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace RoslynMCP.Services.MetadataConfiguration;

/// <summary>
/// A configuration read found in a referenced assembly's IL: the string it names, the member it
/// was passed to, and enough about the call to decide later whether it is a configuration read at
/// all.
/// </summary>
/// <param name="DeclaringTypeName">The metadata name of the type declaring the called member.</param>
/// <param name="ReceiverMemberName">The member that produced the receiver, when the call came
/// straight off another call — <c>ConfigurationManager.AppSettings["Key"]</c> is a
/// <c>get_AppSettings</c> followed by a <c>get_Item</c>, and the second says nothing without the
/// first.</param>
/// <param name="ContainingTypeName">The type the call was compiled into, so a click has something
/// to decompile.</param>
/// <param name="ContainingMethodName">The method it was compiled into, so the click can land on
/// the call rather than on the top of the file.</param>
internal readonly record struct MetadataConfigurationCandidate(
    string Literal,
    string MemberName,
    string DeclaringTypeName,
    string? ReceiverMemberName,
    string? ReceiverTypeName,
    string ContainingTypeName,
    string ContainingMethodName);

/// <summary>
/// Reads the configuration keys a compiled assembly names inside itself.
/// </summary>
/// <remarks>
/// <para>
/// The gap this fills is the one Roslyn cannot: a package that binds its own section — a NuGet
/// library reading <c>Kestrel</c> or <c>Logging</c>, a Framework library reading
/// <c>ConfigurationManager.AppSettings["Timeout"]</c> — has that name in a method body, and method
/// bodies are the one thing a metadata reference does not surface as symbols. Without this, the
/// keys those libraries read appear in <c>appsettings.json</c> and <c>web.config</c> with nothing
/// reading them, which is indistinguishable from dead.
/// </para>
/// <para>
/// Two filters keep it cheap, and neither decides correctness. The assembly reference table says
/// whether an assembly can even see the configuration types — over a 600-assembly sample that
/// skipped 463 of them without opening a single method body, and the whole scan ran in under a
/// quarter second. Within the survivors the callee's <em>name</em> narrows millions of call sites
/// to a few dozen worth asking about. Whether a candidate is really a configuration read is
/// decided by <see cref="MetadataConfigurationIndex"/> against the real type system, because a
/// name alone matches <c>RegistryKey.GetValue</c> and <c>Dictionary.get_Item</c> just as readily.
/// </para>
/// </remarks>
internal static class MetadataConfigurationScanner
{
    /// <summary>
    /// The modern APIs whose string argument <em>is</em> a configuration path. <c>Bind</c> and
    /// <c>Configure</c> are deliberately absent: their string argument is a named-options name,
    /// never a path, and the section they bind arrives as an <c>IConfiguration</c> that a
    /// <c>GetSection</c> at the same site already named.
    /// </summary>
    private static readonly string[] s_apis =
        ["GetSection", "GetRequiredSection", "GetValue", "GetConnectionString", "BindConfiguration"];

    /// <summary>
    /// The Framework shape: a name looked up in a collection that a static property handed over.
    /// Meaningless on their own — every dictionary in the world has a <c>get_Item</c> — so they
    /// count only behind one of <see cref="s_collections"/>.
    /// </summary>
    private static readonly string[] s_lookups = ["get_Item", "Get"];

    private static readonly string[] s_collections = ["get_AppSettings", "get_ConnectionStrings"];

    /// <summary>Namespaces the configuration types live in, on either framework.</summary>
    private static readonly string[] s_configurationNamespaces =
    [
        "Microsoft.Extensions.Configuration",
        "Microsoft.Extensions.Options",
        "System.Configuration",
        "System.Web.Configuration",
    ];

    private static readonly ConcurrentDictionary<string, Entry> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(
        DateTime WrittenUtc, ImmutableArray<MetadataConfigurationCandidate> Candidates);

    public static void Clear() => s_cache.Clear();

    /// <summary>Every candidate in one assembly, re-read only when the file changes.</summary>
    public static ImmutableArray<MetadataConfigurationCandidate> Candidates(string assemblyPath)
    {
        DateTime writtenUtc;

        try
        {
            writtenUtc = File.GetLastWriteTimeUtc(assemblyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        if (s_cache.TryGetValue(assemblyPath, out var cached) && cached.WrittenUtc == writtenUtc)
            return cached.Candidates;

        var candidates = Read(assemblyPath);
        s_cache[assemblyPath] = new Entry(writtenUtc, candidates);
        return candidates;
    }

    private static ImmutableArray<MetadataConfigurationCandidate> Read(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);

            if (!pe.HasMetadata)
                return [];

            var md = pe.GetMetadataReader();

            if (!ReferencesConfiguration(md))
                return [];

            var candidates = ImmutableArray.CreateBuilder<MetadataConfigurationCandidate>();

            foreach (var handle in md.MethodDefinitions)
            {
                var method = md.GetMethodDefinition(handle);

                if (method.RelativeVirtualAddress == 0)
                    continue;

                byte[] il;

                try
                {
                    il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
                }
                catch (BadImageFormatException)
                {
                    continue;
                }

                if (il.Length > 0)
                {
                    ReadBody(
                        md, il, TypeName(md, method.GetDeclaringType()), md.GetString(method.Name),
                        candidates);
                }
            }

            return candidates.ToImmutable();
        }
        catch (Exception ex)
            when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            // An unreadable or unmanaged reference contributes nothing; it is not an error.
            return [];
        }
    }

    /// <summary>
    /// Whether the assembly names a configuration type at all. Read from the type reference table
    /// rather than the assembly reference table: <c>ConfigurationManager</c> reaches a
    /// netstandard library through a type forward, so the assembly it appears to come from is not
    /// the one that defines it, while the namespace on the reference is the same either way.
    /// </summary>
    /// <remarks>
    /// This is the filter that makes the scan affordable. Over a 600-assembly sample it rejected
    /// three quarters of them without a method body being read, and the whole scan finished in
    /// under a quarter second.
    /// </remarks>
    private static bool ReferencesConfiguration(MetadataReader md)
    {
        foreach (var handle in md.TypeReferences)
        {
            string ns = md.GetString(md.GetTypeReference(handle).Namespace);

            if (ns.Length == 0)
                continue;

            foreach (string configuration in s_configurationNamespaces)
            {
                if (ns.StartsWith(configuration, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private const int Ldstr = 0x72;
    private const int Call = 0x28;
    private const int Callvirt = 0x6F;

    /// <summary>
    /// One method body: a string load that survives to the next call is that call's last
    /// argument. Only pushes may intervene — <c>GetValue("Key", fallback)</c> loads its fallback
    /// after the key — and a branch or another call ends the string's life, so a literal is never
    /// attributed to a call that did not receive it. The call before the string is remembered
    /// separately, because the Framework's <c>AppSettings["Key"]</c> puts the collection there.
    /// </summary>
    private static void ReadBody(
        MetadataReader md, byte[] il, string containingType, string containingMethod,
        ImmutableArray<MetadataConfigurationCandidate>.Builder candidates)
    {
        string? pending = null;
        string? receiverMember = null;
        string? receiverType = null;

        for (int i = 0; i < il.Length;)
        {
            int opcode = il[i];
            int operand = OperandSize(ref opcode, il, ref i);

            if (operand < 0 || i + operand > il.Length)
                return;

            switch (opcode)
            {
                case Ldstr:
                    pending = UserString(md, BitConverter.ToInt32(il, i));
                    break;

                case Call or Callvirt:
                {
                    var (member, declaring) = Member(md, BitConverter.ToInt32(il, i));

                    if (pending is not null && member is { Length: > 0 }
                        && declaring is { Length: > 0 } && IsCandidate(member, receiverMember))
                    {
                        candidates.Add(new MetadataConfigurationCandidate(
                            pending, member, declaring, receiverMember, receiverType,
                            containingType, containingMethod));
                    }

                    pending = null;
                    receiverMember = member;
                    receiverType = declaring;
                    break;
                }

                default:
                    if (!IsPush(opcode))
                        pending = null;

                    break;
            }

            i += operand;
        }
    }

    private static bool IsCandidate(string member, string? receiverMember) =>
        Array.IndexOf(s_apis, member) >= 0
        || (Array.IndexOf(s_lookups, member) >= 0
            && receiverMember is not null && Array.IndexOf(s_collections, receiverMember) >= 0);

    /// <summary>Loads that can sit between a string and the call receiving it.</summary>
    private static bool IsPush(int opcode) =>
        opcode is 0x00                          // nop
            or >= 0x02 and <= 0x0E              // ldarg / ldloc, short forms
            or >= 0x14 and <= 0x23              // ldnull, ldc.*
            or 0x25                             // dup
            or 0xFE09 or 0xFE0C;                // ldarg / ldloc, long forms

    private static string? UserString(MetadataReader md, int token)
    {
        try
        {
            return md.GetUserString(MetadataTokens.UserStringHandle(token));
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The called member's name, and the metadata name of the type declaring it.</summary>
    private static (string? Member, string? DeclaringType) Member(MetadataReader md, int token)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(token);

            switch (handle.Kind)
            {
                case HandleKind.MemberReference:
                    var member = md.GetMemberReference((MemberReferenceHandle)handle);
                    return (md.GetString(member.Name), ParentName(md, member.Parent));

                case HandleKind.MethodDefinition:
                    var method = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                    return (md.GetString(method.Name), TypeName(md, method.GetDeclaringType()));

                case HandleKind.MethodSpecification:
                    // GetValue<T> and BindConfiguration<TOptions> arrive as instantiations.
                    return Member(md, MetadataTokens.GetToken(
                        md.GetMethodSpecification((MethodSpecificationHandle)handle).Method));

                default:
                    return (null, null);
            }
        }
        catch (Exception ex)
            when (ex is BadImageFormatException or ArgumentException or InvalidCastException)
        {
            return (null, null);
        }
    }

    private static string? ParentName(MetadataReader md, EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                var reference = md.GetTypeReference((TypeReferenceHandle)parent);
                return Join(md.GetString(reference.Namespace), md.GetString(reference.Name));

            case HandleKind.TypeDefinition:
                return TypeName(md, (TypeDefinitionHandle)parent);

            default:
                return null;
        }
    }

    private static string TypeName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        return Join(md.GetString(type.Namespace), md.GetString(type.Name));
    }

    private static string Join(string? ns, string name) =>
        ns is { Length: > 0 } ? ns + "." + name : name;

    /// <summary>
    /// The operand width of the opcode at <paramref name="i"/>, having advanced past the opcode
    /// itself. Negative when the stream cannot be trusted any further.
    /// </summary>
    private static int OperandSize(ref int opcode, byte[] il, ref int i)
    {
        if (opcode == 0xFE)
        {
            if (i + 1 >= il.Length)
                return -1;

            opcode = 0xFE00 | il[i + 1];
            i += 2;

            return opcode switch
            {
                0xFE06 or 0xFE07 or 0xFE09 or 0xFE0A or 0xFE0B or 0xFE0C or 0xFE0D or 0xFE0E
                    or 0xFE15 or 0xFE16 or 0xFE1C => 4,
                0xFE0F or 0xFE12 => 1,
                _ => 0,
            };
        }

        i += 1;

        return opcode switch
        {
            // switch: a jump table whose width is decided by its own first operand.
            0x45 => il.Length - i >= 4 ? 4 + (4 * BitConverter.ToInt32(il, i)) : -1,
            0x0E or 0x10 or 0x11 or 0x12 or 0x13 or 0x1F or 0x2B or 0x2C or 0x2D or 0x2E or 0x2F
                or 0x30 or 0x31 or 0x32 or 0x33 or 0x34 or 0x35 or 0x36 or 0x37 or 0xDE => 1,
            0x20 or 0x22 or 0x27 or 0x28 or 0x38 or 0x39 or 0x3A or 0x3B or 0x3C or 0x3D or 0x3E
                or 0x3F or 0x40 or 0x41 or 0x42 or 0x43 or 0x44 => 4,
            0x21 or 0x23 => 8,
            0x6F or 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x79 or 0x7B or 0x7C or 0x7D
                or 0x7E or 0x7F or 0x80 or 0x81 or 0x8C or 0x8D or 0x8F or 0xA3 or 0xA4 or 0xA5
                or 0xC2 or 0xC6 or 0xD0 => 4,
            _ => 0,
        };
    }
}
