using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using RoslynMCP.Services.MetadataConfiguration;
using Xunit;

namespace RoslynMCP.Tests;

public class MetadataConstantStringsTests
{
    [Fact]
    public void InvalidMethodTokensAreUnknown()
    {
        using var image = CreateImage(token => [0x72, .. BitConverter.GetBytes(token), 0x2a]);
        var resolver = new MetadataConstantStrings(image, image.GetMetadataReader());
        Assert.Null(resolver.Resolve(unchecked((int)0xff000001)));
        Assert.Null(resolver.Resolve(0x0600ffff));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolvesReleaseAndDebugLiteralGetters(bool debug)
    {
        using var image = CreateImage(token => debug
            ? [0x00, 0x72, .. BitConverter.GetBytes(token), 0x0a, 0x2b, 0x00, 0x06, 0x2a]
            : [0x72, .. BitConverter.GetBytes(token), 0x2a]);
        var resolver = new MetadataConstantStrings(image, image.GetMetadataReader());

        Assert.Equal("ExampleLibrary License Key", resolver.Resolve(0x06000001));
        Assert.Equal("ExampleLibrary License Key", resolver.Resolve(0x06000001));
    }

    [Fact]
    public void SkipsUnreachableObfuscatorTokenInstructions()
    {
        // An obfuscated helper branches over an unreachable ldtoken/pop pair.
        using var image = CreateImage(token =>
            [0x2b, 0x06, 0xd0, 0x01, 0x00, 0x00, 0x02, 0x26,
                0x72, .. BitConverter.GetBytes(token), 0x2a]);

        Assert.Equal("ExampleLibrary License Key",
            new MetadataConstantStrings(image, image.GetMetadataReader()).Resolve(0x06000001));
    }

    [Theory]
    [InlineData(0x28)] // call: could compute a string or produce a side effect
    [InlineData(0x7e)] // ldsfld: mutable external state
    [InlineData(0x02)] // ldarg.0: argument-dependent value
    [InlineData(0x2c)] // brfalse.s: conditional return
    public void DoesNotTreatOtherInstructionsAsConstants(byte instruction)
    {
        using var image = CreateImage(token =>
            [instruction, 0x00, 0x00, 0x00, 0x00, 0x72, .. BitConverter.GetBytes(token), 0x2a]);
        var resolver = new MetadataConstantStrings(image, image.GetMetadataReader());

        Assert.Null(resolver.Resolve(0x06000001));
        Assert.Null(resolver.Resolve(0x06000001));
    }

    [Fact]
    public void RejectsLoopsAndTruncatedLiteralOperands()
    {
        using var loop = CreateImage(_ => [0x2b, 0xfe]);
        using var truncated = CreateImage(_ => [0x72, 0x01]);

        Assert.Null(new MetadataConstantStrings(loop, loop.GetMetadataReader()).Resolve(0x06000001));
        Assert.Null(new MetadataConstantStrings(truncated, truncated.GetMetadataReader()).Resolve(0x06000001));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RequiresStaticParameterlessMethods(bool instance, bool parameter)
    {
        using var image = CreateImage(token => [0x72, .. BitConverter.GetBytes(token), 0x2a], instance, parameter);

        Assert.Null(new MetadataConstantStrings(image, image.GetMetadataReader()).Resolve(0x06000001));
    }

    private static PEReader CreateImage(Func<int, byte[]> instructions, bool instance = false, bool parameter = false)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("LiteralFixture.dll"),
            metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("LiteralFixture"), new Version(1, 0),
            default, default, 0, AssemblyHashAlgorithm.None);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"),
            default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("LiteralGetter"),
            default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        var il = instructions(MetadataTokens.GetToken(metadata.GetOrAddUserString("ExampleLibrary License Key")));
        var bodies = new BlobBuilder();
        bodies.WriteByte((byte)((il.Length << 2) | 2)); // tiny method header
        bodies.WriteBytes(il);
        byte[] signature = parameter ? [0x00, 0x01, 0x0e, 0x0e] : [(byte)(instance ? 0x20 : 0x00), 0x00, 0x0e];
        metadata.AddMethodDefinition(MethodAttributes.Public | (instance ? 0 : MethodAttributes.Static),
            MethodImplAttributes.IL, metadata.GetOrAddString("get_Key"), metadata.GetOrAddBlob(signature),
            0, MetadataTokens.ParameterHandle(1));

        var builder = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies);
        var image = new BlobBuilder();
        builder.Serialize(image);
        return new PEReader(new MemoryStream(image.ToArray()));
    }
}
