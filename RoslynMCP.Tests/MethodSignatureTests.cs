using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Counting a method's parameters from its metadata signature — how an overload is picked when the
/// debugger has to call one by name inside the process it is attached to.
/// </summary>
public class MethodSignatureTests
{
    [Fact]
    public void APlainMethodReportsWhatItDeclares()
    {
        // Default calling convention, one parameter, returning void and taking a string.
        byte[] signature = [0x00, 0x01, 0x01, 0x0E];

        Assert.Equal(1, MethodSignature.ParameterCount(signature));
    }

    [Fact]
    public void AGenericMethodsParameterCountIsNotItsGenericCount()
    {
        // The generic flag puts an extra count in front of the one being asked for. Reading past
        // it is what would make a two-parameter method look like a one-parameter one — and the
        // whole job here is telling overloads apart.
        byte[] signature = [0x10, 0x01, 0x02, 0x01, 0x0E, 0x0E];

        Assert.Equal(2, MethodSignature.ParameterCount(signature));
    }

    [Fact]
    public void AMethodWithNoParametersSaysZero()
    {
        byte[] signature = [0x00, 0x00, 0x01];

        Assert.Equal(0, MethodSignature.ParameterCount(signature));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x00 })]
    // The generic count is there but the parameter count was cut off with it.
    [InlineData(new byte[] { 0x10, 0x01 })]
    public void ABlobTooShortToReadAnswersUnknown(byte[] signature)
    {
        // Never zero: "no parameters" and "cannot tell" pick different overloads, and a wrong one
        // called in a debuggee fails in a way that looks like the target's fault.
        Assert.Equal(MethodSignature.Unknown, MethodSignature.ParameterCount(signature));
    }

    [Fact]
    public void ACountTooLargeForOneByteAnswersUnknown()
    {
        // The compressed forms beyond one byte are not decoded. Declining is right; guessing the
        // low bits would name a real but wrong overload.
        byte[] signature = [0x00, 0x80, 0x01];

        Assert.Equal(MethodSignature.Unknown, MethodSignature.ParameterCount(signature));
    }

    [Fact]
    public void EveryMethodOfARealAssemblyIsCountedTheWayReflectionCountsIt()
    {
        // The decoder checked against the runtime's own view rather than against hand-written
        // blobs, which can only ever contain the cases already thought of. Every method of a large
        // real assembly goes through: generic and not, static and instance, varargs, every
        // calling convention a C# compiler emits.
        using var stream = File.OpenRead(typeof(object).Assembly.Location);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();

        var wrong = new List<string>();
        var checkedCount = 0;

        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            var blob = metadata.GetBlobBytes(method.Signature);

            // The true count, read by the framework's own signature decoder rather than inferred.
            // Parameter *rows* would only be a floor — they are emitted only for parameters that
            // need one — and a floor cannot catch over-counting, which a decoder that lost its
            // place in the blob does just as easily as under-counting.
            var reader = metadata.GetBlobReader(method.Signature);
            var header = reader.ReadSignatureHeader();
            if (header.IsGeneric)
                reader.ReadCompressedInteger();
            var expected = reader.ReadCompressedInteger();

            var counted = MethodSignature.ParameterCount(blob);
            if (expected >= 0x80)
            {
                // Beyond the one-byte form, which the decoder declines by design rather than
                // guessing at. Pinned as a refusal, not skipped.
                Assert.Equal(MethodSignature.Unknown, counted);
                continue;
            }

            checkedCount++;
            if (counted != expected)
                wrong.Add($"{metadata.GetString(method.Name)}: counted {counted}, declares {expected}");
        }

        Assert.True(checkedCount > 10_000, $"only {checkedCount} methods were checked");
        Assert.Empty(wrong);
    }

    [Fact]
    public void TheOverloadsThatMadeThisNecessaryAreToldApart()
    {
        // The concrete case: Assembly.LoadFrom takes a path in one form and a path with a hash in
        // another. Loading through the wrong one is how an injection fails while looking like the
        // assembly is at fault.
        var counts = typeof(Assembly)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "LoadFrom")
            .Select(m => m.GetParameters().Length)
            .ToHashSet();

        Assert.Contains(1, counts);
        // More than one form exists, which is exactly why the count is consulted at all.
        Assert.True(counts.Count > 1, "Assembly.LoadFrom no longer has overloads to tell apart");
    }
}
