using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace RoslynMCP.Services.MetadataConfiguration;

/// <summary>
/// Resolves literal-returning helpers without loading or executing the referenced assembly.
/// Only a small, side-effect-free subset of IL is accepted; all other methods are unknown.
/// The cache belongs to one assembly scan, including unsuccessful resolutions.
/// </summary>
internal sealed class MetadataConstantStrings(PEReader pe, MetadataReader md)
{
    private readonly Dictionary<int, string?> _cache = [];

    public string? Resolve(int methodToken)
    {
        if (_cache.TryGetValue(methodToken, out var cached)) return cached;
        string? result;
        try
        {
            result = ResolveCore(methodToken);
        }
        catch (BadImageFormatException) { result = null; }
        catch (ArgumentException) { result = null; }
        catch (InvalidOperationException) { result = null; }
        _cache[methodToken] = result;
        return result;
    }

    private string? ResolveCore(int methodToken)
    {
        var handle = MetadataTokens.EntityHandle(methodToken);
        if (handle.Kind == HandleKind.MemberReference)
        {
            // A same-module TypeDef parent needs no assembly-wide method-name search.
            var member = md.GetMemberReference((MemberReferenceHandle)handle);
            if (member.Parent.Kind != HandleKind.TypeDefinition) return null;
            var signature = md.GetBlobBytes(member.Signature);
            MethodDefinitionHandle match = default;
            foreach (var candidateHandle in md.GetTypeDefinition((TypeDefinitionHandle)member.Parent).GetMethods())
            {
                var candidate = md.GetMethodDefinition(candidateHandle);
                if (!md.StringComparer.Equals(candidate.Name, md.GetString(member.Name)) ||
                    !md.GetBlobBytes(candidate.Signature).AsSpan().SequenceEqual(signature)) continue;
                if (!match.IsNil) return null;
                match = candidateHandle;
            }
            return match.IsNil ? null : Resolve(MetadataTokens.GetToken(match));
        }
        if (handle.Kind != HandleKind.MethodDefinition) return null;
        var method = md.GetMethodDefinition((MethodDefinitionHandle)handle);
        if ((method.Attributes & MethodAttributes.Static) == 0 || method.RelativeVirtualAddress == 0)
            return null;
        var sig = md.GetBlobReader(method.Signature);
        var header = sig.ReadSignatureHeader();
        if (header.Kind != SignatureKind.Method || header.IsInstance || header.IsGeneric ||
            sig.ReadCompressedInteger() != 0 || sig.ReadSignatureTypeCode() != SignatureTypeCode.String)
            return null;

        var body = pe.GetMethodBody(method.RelativeVirtualAddress);
        if (body.ExceptionRegions.Length != 0) return null;
        var il = body.GetILBytes();
        if (il is null || il.Length > 4096) return null;

        string? stack = null;
        Dictionary<int, string>? locals = null;
        var offset = 0;
        for (var steps = 0; steps < 256 && offset < il.Length; steps++)
        {
            var op = il[offset++];
            var local = -1;
            var store = false;
            switch (op)
            {
                case 0x00: // nop
                    continue;
                case 0x72: // ldstr
                    if (stack is not null || offset + 4 > il.Length) return null;
                    var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
                    if ((token & unchecked((int)0xff000000)) != 0x70000000) return null;
                    stack = md.GetUserString(MetadataTokens.UserStringHandle(token & 0x00ffffff));
                    offset += 4;
                    continue;
                case 0x2a: // ret
                    return stack;
                case 0x2b: // br.s; unreachable obfuscation instructions are never evaluated.
                    if (offset >= il.Length) return null;
                    var shortDelta = (sbyte)il[offset++];
                    if (shortDelta < 0 || shortDelta >= il.Length - offset) return null;
                    offset += shortDelta;
                    continue;
                case 0x38: // br
                    if (offset + 4 > il.Length) return null;
                    var delta = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
                    offset += 4;
                    if (delta < 0 || delta >= il.Length - offset) return null;
                    offset += delta;
                    continue;
                case >= 0x06 and <= 0x09: // ldloc.0 ... ldloc.3
                    local = op - 0x06;
                    break;
                case >= 0x0a and <= 0x0d: // stloc.0 ... stloc.3
                    local = op - 0x0a;
                    store = true;
                    break;
                case 0x11: // ldloc.s
                case 0x13: // stloc.s
                    if (offset >= il.Length) return null;
                    local = il[offset++];
                    store = op == 0x13;
                    break;
                case 0xfe:
                    if (offset + 3 > il.Length) return null;
                    var extended = il[offset++];
                    if (extended is not (0x0c or 0x0e)) return null; // ldloc / stloc
                    local = BinaryPrimitives.ReadUInt16LittleEndian(il.AsSpan(offset, 2));
                    offset += 2;
                    store = extended == 0x0e;
                    break;
                default:
                    // In particular: no calls, field reads, arguments, conditional branches,
                    // string construction, exceptions, or writes to externally visible state.
                    return null;
            }
            if (store)
            {
                if (stack is null) return null;
                (locals ??= [])[local] = stack;
                stack = null;
            }
            else
            {
                if (stack is not null || locals is null || !locals.TryGetValue(local, out stack))
                    return null;
            }
        }
        return null;
    }
}
