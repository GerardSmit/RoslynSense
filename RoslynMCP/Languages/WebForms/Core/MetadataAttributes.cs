using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>One attribute a member carries, and the single string it was constructed with.</summary>
/// <param name="AttributeName">The attribute type's full metadata name.</param>
/// <param name="Value">The constructor argument as written.</param>
internal readonly record struct MetadataAttributeArgument(string AttributeName, string Value);

/// <summary>
/// A member's attribute arguments read straight out of the assembly file.
/// </summary>
/// <remarks>
/// Roslyn cannot supply these. <c>WebSysDescriptionAttribute</c> and <c>WebCategoryAttribute</c>
/// have <c>internal</c> constructors — they are System.Web's own, applied to System.Web's own
/// members — and Roslyn refuses to bind an attribute to an inaccessible constructor, so
/// <c>AttributeData.AttributeConstructor</c> comes back null and <c>ConstructorArguments</c> comes
/// back empty. The attribute class is named, the argument is not. Metadata has no such rule, and
/// the blob is where the resource key actually is.
/// </remarks>
internal static class MetadataAttributes
{
    /// <summary>Every custom attribute blob opens with this.</summary>
    private const ushort BlobProlog = 1;

    private readonly record struct MemberKey(
        string AssemblyPath, string TypeName, string MemberName, bool IsEvent);

    /// <summary>Per member rather than per assembly: a hover asks about one property, and reading
    /// every attribute System.Web applies to answer it would be most of a megabyte of strings.</summary>
    private static readonly ConcurrentDictionary<MemberKey, ImmutableArray<MetadataAttributeArgument>> s_members = new();

    /// <summary>
    /// The single-string arguments of the attributes on one property or event of
    /// <paramref name="typeName"/>, which is a full metadata name (<c>Ns.Outer+Inner</c>).
    /// Empty when the file, the type, the member or its attributes are not there.
    /// </summary>
    public static ImmutableArray<MetadataAttributeArgument> StringArguments(
        string assemblyPath, string typeName, string memberName, bool isEvent) =>
        s_members.GetOrAdd(
            new MemberKey(assemblyPath, typeName, memberName, isEvent),
            static key =>
            {
                try
                {
                    return Read(key);
                }
                catch
                {
                    // A missing, truncated or unmanaged file is a reason to say nothing about a
                    // symbol, not to fail the request that asked about it.
                    return [];
                }
            });

    private static ImmutableArray<MetadataAttributeArgument> Read(MemberKey key)
    {
        using var stream = File.OpenRead(key.AssemblyPath);
        using var pe = new PEReader(stream);

        if (!pe.HasMetadata)
            return [];

        var metadata = pe.GetMetadataReader();
        if (FindType(metadata, key.TypeName) is not { } type
            || FindMember(metadata, type, key) is not { } attributes)
            return [];

        var arguments = ImmutableArray.CreateBuilder<MetadataAttributeArgument>();

        foreach (var handle in attributes)
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (AttributeName(metadata, attribute) is { Length: > 0 } name
                && StringArgument(metadata, attribute) is { Length: > 0 } value)
                arguments.Add(new MetadataAttributeArgument(name, value));
        }

        return arguments.ToImmutable();
    }

    /// <summary>Matches on the simple name first, which compares against the metadata heap without
    /// allocating; the full name is only built for the handful of types that share it.</summary>
    private static TypeDefinition? FindType(MetadataReader metadata, string typeName)
    {
        string simpleName = typeName[(typeName.LastIndexOfAny(['.', '+']) + 1)..];

        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (metadata.StringComparer.Equals(type.Name, simpleName) && FullName(metadata, type) == typeName)
                return type;
        }

        return null;
    }

    private static string FullName(MetadataReader metadata, TypeDefinition type)
    {
        string name = metadata.GetString(type.Name);

        if (type.GetDeclaringType() is { IsNil: false } declaring)
            return FullName(metadata, metadata.GetTypeDefinition(declaring)) + "+" + name;

        string ns = metadata.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static CustomAttributeHandleCollection? FindMember(
        MetadataReader metadata, TypeDefinition type, MemberKey key)
    {
        if (key.IsEvent)
        {
            foreach (var handle in type.GetEvents())
            {
                var @event = metadata.GetEventDefinition(handle);
                if (metadata.StringComparer.Equals(@event.Name, key.MemberName))
                    return @event.GetCustomAttributes();
            }

            return null;
        }

        foreach (var handle in type.GetProperties())
        {
            var property = metadata.GetPropertyDefinition(handle);
            if (metadata.StringComparer.Equals(property.Name, key.MemberName))
                return property.GetCustomAttributes();
        }

        return null;
    }

    private static string? AttributeName(MetadataReader metadata, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var reference = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (reference.Parent.Kind != HandleKind.TypeReference)
                    return null;
                var referenced = metadata.GetTypeReference((TypeReferenceHandle)reference.Parent);
                return Qualify(metadata.GetString(referenced.Namespace), metadata.GetString(referenced.Name));

            case HandleKind.MethodDefinition:
                var definition = metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                return FullName(metadata, metadata.GetTypeDefinition(definition.GetDeclaringType()));

            default:
                return null;
        }

        static string Qualify(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>
    /// The one string the attribute was constructed with, or <c>null</c> when it was constructed
    /// with anything else. The signature is read rather than assumed: <c>[Description]</c> and
    /// <c>[Category]</c> also have parameterless constructors, and a blob with no arguments would
    /// otherwise be decoded as a string of whatever follows.
    /// </summary>
    private static string? StringArgument(MetadataReader metadata, CustomAttribute attribute)
    {
        if (!TakesSingleString(metadata, attribute.Constructor))
            return null;

        var value = metadata.GetBlobReader(attribute.Value);
        return value.Length >= sizeof(ushort) && value.ReadUInt16() == BlobProlog
            ? value.ReadSerializedString()
            : null;
    }

    private static bool TakesSingleString(MetadataReader metadata, EntityHandle constructor)
    {
        var signature = constructor.Kind switch
        {
            HandleKind.MemberReference =>
                metadata.GetMemberReference((MemberReferenceHandle)constructor).Signature,
            HandleKind.MethodDefinition =>
                metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).Signature,
            _ => default,
        };

        if (signature.IsNil)
            return false;

        var reader = metadata.GetBlobReader(signature);
        reader.ReadSignatureHeader();

        return reader.ReadCompressedInteger() == 1
            && reader.ReadSignatureTypeCode() == SignatureTypeCode.Void
            && reader.ReadSignatureTypeCode() == SignatureTypeCode.String;
    }
}
