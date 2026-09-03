using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
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

/// <summary>A type and a method, which is all a call site says about what it is calling.</summary>
internal readonly record struct MetadataForwarderKey(string TypeName, string MethodName);

/// <summary>
/// A method in a compiled assembly that reads whatever setting it is handed —
/// <c>Config.GetSetting(name)</c> over <c>ConfigurationManager.AppSettings</c>.
/// </summary>
/// <remarks>
/// The shape that made the external-reference count read zero on a DNN site. Every framework of
/// that generation wraps the read once, in its own assembly, and from then on nothing anywhere
/// names a configuration API at the call site: the site says <c>Config.GetSetting("Key")</c> and
/// the read is a compilation unit away, behind a parameter. A scan that only recognises the
/// framework's own shapes sees the wrapper's body — with no literal in it — and every one of its
/// hundreds of callers as ordinary calls to an ordinary method.
/// </remarks>
/// <param name="ParameterIndex">Which of the method's own parameters reaches the read, counted as
/// the declaration counts them: <c>this</c> is not one.</param>
/// <param name="Read">The read the parameter reaches, carried whole and with an empty literal, so
/// that a wrapper is classified by exactly the rules a direct read is.</param>
internal readonly record struct MetadataConfigurationForwarder(
    int ParameterIndex, MetadataConfigurationCandidate Read)
{
    public MetadataForwarderKey Key =>
        new(Read.ContainingTypeName, Read.ContainingMethodName);
}

/// <summary>One assembly's worth of reads, and the wrappers that read on someone else's behalf.</summary>
internal readonly record struct MetadataConfigurationScan(
    ImmutableArray<MetadataConfigurationCandidate> Candidates,
    ImmutableArray<MetadataConfigurationForwarder> Forwarders)
{
    public static MetadataConfigurationScan Empty { get; } = new([], []);

    public bool IsEmpty => Candidates.IsEmpty && Forwarders.IsEmpty;
}

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

    private static readonly ConcurrentDictionary<(string Path, string Wrappers), ForwardedEntry>
        s_forwardedCache = new();

    private sealed record Entry(DateTime WrittenUtc, MetadataConfigurationScan Scan);

    private sealed record ForwardedEntry(
        DateTime WrittenUtc, ImmutableArray<MetadataConfigurationCandidate> Candidates);

    public static void Clear()
    {
        s_cache.Clear();
        s_forwardedCache.Clear();
    }

    /// <summary>Everything one assembly says on its own, re-read only when the file changes.</summary>
    public static MetadataConfigurationScan Scan(string assemblyPath)
    {
        if (WrittenUtc(assemblyPath) is not { } writtenUtc)
            return MetadataConfigurationScan.Empty;

        if (s_cache.TryGetValue(assemblyPath, out var cached) && cached.WrittenUtc == writtenUtc)
            return cached.Scan;

        var scan = Read(assemblyPath);
        s_cache[assemblyPath] = new Entry(writtenUtc, scan);
        return scan;
    }

    /// <summary>
    /// The literals one assembly hands to a known set of wrappers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A second pass, because a wrapper is not known until the first one has finished: the
    /// assembly declaring <c>Config.GetSetting</c> and the assemblies calling it are different
    /// files, and the calling ones say nothing about configuration at all. Same shape as the
    /// source side, which also needs a round to find the wrapper before a round can find its
    /// callers.
    /// </para>
    /// <para>
    /// The type-reference gate is what keeps a second pass from doubling the cost. An assembly
    /// that never names the wrapper's declaring type cannot be calling it, and that is decided
    /// from the metadata tables without a method body being read — the same filter, on a different
    /// set of names, that made the first pass affordable.
    /// </para>
    /// </remarks>
    public static ImmutableArray<MetadataConfigurationCandidate> ForwardedCandidates(
        string assemblyPath, ImmutableArray<MetadataForwarderKey> wrappers)
    {
        if (wrappers.IsDefaultOrEmpty || WrittenUtc(assemblyPath) is not { } writtenUtc)
            return [];

        string key = string.Join(
            "|", wrappers.Select(w => w.TypeName + "." + w.MethodName).OrderBy(n => n, StringComparer.Ordinal));

        if (s_forwardedCache.TryGetValue((assemblyPath, key), out var cached)
            && cached.WrittenUtc == writtenUtc)
        {
            return cached.Candidates;
        }

        var candidates = ReadForwarded(assemblyPath, wrappers);
        s_forwardedCache[(assemblyPath, key)] = new ForwardedEntry(writtenUtc, candidates);
        return candidates;
    }

    private static DateTime? WrittenUtc(string assemblyPath)
    {
        try
        {
            return File.GetLastWriteTimeUtc(assemblyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static MetadataConfigurationScan Read(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);

            if (!pe.HasMetadata)
                return MetadataConfigurationScan.Empty;

            var md = pe.GetMetadataReader();

            if (!ReferencesConfiguration(md))
                return MetadataConfigurationScan.Empty;

            var candidates = ImmutableArray.CreateBuilder<MetadataConfigurationCandidate>();
            var forwarders = ImmutableArray.CreateBuilder<MetadataConfigurationForwarder>();

            foreach (var (method, il) in Bodies(pe, md))
            {
                ReadBody(
                    md, il, TypeName(md, method.GetDeclaringType()), md.GetString(method.Name),
                    (method.Attributes & MethodAttributes.Static) != 0, candidates, forwarders);
            }

            return new MetadataConfigurationScan(candidates.ToImmutable(), forwarders.ToImmutable());
        }
        catch (Exception ex)
            when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            // An unreadable or unmanaged reference contributes nothing; it is not an error.
            return MetadataConfigurationScan.Empty;
        }
    }

    private static ImmutableArray<MetadataConfigurationCandidate> ReadForwarded(
        string assemblyPath, ImmutableArray<MetadataForwarderKey> wrappers)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);

            if (!pe.HasMetadata)
                return [];

            var md = pe.GetMetadataReader();

            var wanted = wrappers.ToLookup(w => w.MethodName, StringComparer.Ordinal);
            var types = new HashSet<string>(wrappers.Select(w => w.TypeName), StringComparer.Ordinal);

            if (!NamesAnyType(md, types))
                return [];

            var candidates = ImmutableArray.CreateBuilder<MetadataConfigurationCandidate>();

            foreach (var (method, il) in Bodies(pe, md))
            {
                ReadForwardedBody(
                    md, il, TypeName(md, method.GetDeclaringType()), md.GetString(method.Name),
                    wanted, candidates);
            }

            return candidates.ToImmutable();
        }
        catch (Exception ex)
            when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Every method in the assembly that has IL to read.</summary>
    private static IEnumerable<(MethodDefinition Method, byte[] Il)> Bodies(
        PEReader pe, MetadataReader md)
    {
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
                yield return (method, il);
        }
    }

    /// <summary>
    /// Whether the assembly names any of these types, whether it declares them or refers to them.
    /// </summary>
    /// <remarks>
    /// Both tables, because the assembly that declares a wrapper also tends to be its heaviest
    /// caller — DNN's own <c>Config.GetSetting</c> is read from all over DNN.
    /// </remarks>
    private static bool NamesAnyType(MetadataReader md, HashSet<string> types)
    {
        foreach (var handle in md.TypeReferences)
        {
            var reference = md.GetTypeReference(handle);

            if (types.Contains(Join(md.GetString(reference.Namespace), md.GetString(reference.Name))))
                return true;
        }

        foreach (var handle in md.TypeDefinitions)
        {
            if (types.Contains(TypeName(md, handle)))
                return true;
        }

        return false;
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
        bool isStatic,
        ImmutableArray<MetadataConfigurationCandidate>.Builder candidates,
        ImmutableArray<MetadataConfigurationForwarder>.Builder forwarders)
    {
        string? pending = null;
        int? pendingArgument = null;
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

                    if (member is { Length: > 0 } && declaring is { Length: > 0 }
                        && IsCandidate(member, receiverMember))
                    {
                        var read = new MetadataConfigurationCandidate(
                            pending ?? "", member, declaring, receiverMember, receiverType,
                            containingType, containingMethod);

                        // A literal decides on its own; the parameter is only consulted when
                        // there is no literal to be the key. A method that both names a key and
                        // passes one of its parameters along is read as naming the key, which
                        // costs a wrapper nobody could have told apart from a decoy anyway.
                        if (pending is not null)
                            candidates.Add(read);
                        else if (pendingArgument is { } index)
                            forwarders.Add(new MetadataConfigurationForwarder(index, read));
                    }

                    pending = null;
                    pendingArgument = null;
                    receiverMember = member;
                    receiverType = declaring;
                    break;
                }

                default:
                    if (Argument(opcode, il, i, isStatic) is { } argument)
                    {
                        pendingArgument = argument;
                    }
                    else if (!IsPush(opcode))
                    {
                        pending = null;
                        pendingArgument = null;
                    }

                    break;
            }

            i += operand;
        }
    }

    /// <summary>
    /// One method body, looking only for literals handed to a wrapper someone else declared.
    /// </summary>
    /// <remarks>
    /// The literal is taken to be the key with the same looseness the direct pass allows, and for
    /// the same reason: deciding which argument it fills would mean decoding the callee's
    /// signature at every call site. A wrapper whose other parameters are also strings can
    /// therefore be credited with the wrong one — which is why what a wrapper reads is still
    /// settled by the type system rather than here.
    /// </remarks>
    private static void ReadForwardedBody(
        MetadataReader md, byte[] il, string containingType, string containingMethod,
        ILookup<string, MetadataForwarderKey> wrappers,
        ImmutableArray<MetadataConfigurationCandidate>.Builder candidates)
    {
        string? pending = null;

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

                    if (pending is { Length: > 0 } literal && member is { Length: > 0 }
                        && declaring is { Length: > 0 }
                        && wrappers[member].Any(w => w.TypeName == declaring))
                    {
                        candidates.Add(new MetadataConfigurationCandidate(
                            literal, member, declaring, ReceiverMemberName: null,
                            ReceiverTypeName: null, containingType, containingMethod));
                    }

                    pending = null;
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

    /// <summary>
    /// The declaration-order parameter an <c>ldarg</c> loads, or null when the opcode is not one
    /// or loads an instance method's <c>this</c>.
    /// </summary>
    private static int? Argument(int opcode, byte[] il, int i, bool isStatic)
    {
        int slot = opcode switch
        {
            >= 0x02 and <= 0x05 => opcode - 0x02,           // ldarg.0 .. ldarg.3
            0x0E => i < il.Length ? il[i] : -1,             // ldarg.s
            0xFE09 => i + 1 < il.Length ? BitConverter.ToUInt16(il, i) : -1, // ldarg
            _ => -1,
        };

        if (slot < 0)
            return null;

        int index = isStatic ? slot : slot - 1;
        return index >= 0 ? index : null;
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
