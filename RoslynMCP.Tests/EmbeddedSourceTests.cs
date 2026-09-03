using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services.ExternalSource;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Source carried inside a PDB — the one case where a dependency's real source needs no network
/// at all.
/// </summary>
public class EmbeddedSourceTests
{
    [Fact]
    public void WhenTheBlobIsUncompressedThenTheSourceFollowsTheFormatWord()
    {
        byte[] source = Encoding.UTF8.GetBytes("class Order { }");
        byte[] blob = [.. new byte[4], .. source];

        Assert.Equal(source, EmbeddedSourceReader.Decode(blob));
    }

    [Fact]
    public void WhenTheBlobIsCompressedThenTheFormatWordIsTheUncompressedSize()
    {
        byte[] source = Encoding.UTF8.GetBytes(new string('x', 4096));

        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionMode.Compress, leaveOpen: true))
            deflate.Write(source);

        byte[] size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, source.Length);
        byte[] blob = [.. size, .. compressed.ToArray()];

        Assert.Equal(source, EmbeddedSourceReader.Decode(blob));
    }

    [Fact]
    public void WhenTheBlobIsTruncatedThenNothingIsDecoded()
    {
        Assert.Null(EmbeddedSourceReader.Decode([1, 2]));
    }

    [Fact]
    public void WhenTheFormatWordIsNegativeThenNothingIsDecoded()
    {
        byte[] blob = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(blob, -1);

        Assert.Null(EmbeddedSourceReader.Decode(blob));
    }

    [Fact]
    public void WhenAnAssemblyEmbedsItsSourceThenItIsReadBackWithoutTheNetwork()
    {
        const string code = """
            namespace Widgets;

            public class Order
            {
                public int Total() => 42;
            }
            """;

        using var peStream = Emit(code, "EmbeddedSourceTests.Order.cs");
        using var peReader = new PEReader(peStream);

        var entry = peReader.ReadDebugDirectory()
            .Single(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

        using var provider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
        var pdb = provider.GetMetadataReader();

        var document = pdb.Documents.Single();
        byte[]? source = EmbeddedSourceReader.TryRead(pdb, document);

        Assert.NotNull(source);
        Assert.Contains("public int Total() => 42;", Encoding.UTF8.GetString(source!));
    }

    /// <summary>Compiles a snippet into a PE that carries its own portable PDB and its source.</summary>
    private static MemoryStream Emit(string code, string fileName)
    {
        var text = SourceText.From(code, Encoding.UTF8, SourceHashAlgorithm.Sha256);
        var tree = CSharpSyntaxTree.ParseText(text, path: fileName);

        var compilation = CSharpCompilation.Create(
            "EmbeddedSourceTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var pe = new MemoryStream();

        // Embedded means the PDB goes inside the PE, so there is no separate stream to pass.
        var result = compilation.Emit(
            pe,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded),
            embeddedTexts: [EmbeddedText.FromSource(fileName, text)]);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        pe.Position = 0;
        return pe;
    }
}
