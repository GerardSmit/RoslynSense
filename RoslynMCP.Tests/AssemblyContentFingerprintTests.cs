using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// What the analyzer rebuild watcher compares: an image's content, not the build that made it.
/// </summary>
/// <remarks>
/// The reported symptom was a stall after every build of a solution with a project-referenced
/// analyzer. The editor hands the extension a lower-case drive letter, so builds started from it
/// embedded <c>d:\...</c> as the PDB path while builds from anywhere else embedded <c>D:\...</c>;
/// a deterministic compiler derives the module id, the PDB id and the timestamp from that path,
/// and hashing whole files made every build a "rebuild" of an analyzer nobody had touched.
/// </remarks>
public class AssemblyContentFingerprintTests
{
    [Fact]
    public void TheSameSourcesBuiltFromDifferentlyCasedPathsFingerprintTheSame()
    {
        byte[] lower = Emit("class C { }", @"d:\build\Analyzer.pdb");
        byte[] upper = Emit("class C { }", @"D:\build\Analyzer.pdb");

        // The premise: the images really do differ, and only by what the path drags along.
        Assert.NotEqual(lower, upper);
        Assert.Equal(lower.Length, upper.Length);

        Assert.Equal(Fingerprint(lower), Fingerprint(upper));
    }

    [Fact]
    public void AChangedSourceStillFingerprintsDifferently()
    {
        byte[] before = Emit("class C { }", @"D:\build\Analyzer.pdb");
        byte[] after = Emit("class C { int Field; }", @"D:\build\Analyzer.pdb");

        // The other half: masking must not reach anything a compilation would observe.
        Assert.NotEqual(Fingerprint(before), Fingerprint(after));
    }

    [Fact]
    public void MaskedRangesCoverOnlyTheVolatileFields()
    {
        byte[] image = Emit("class C { }", @"D:\build\Analyzer.pdb");
        using var stream = new MemoryStream(image);

        var ranges = AssemblyContentFingerprint.VolatileRanges(stream);

        // Timestamp, checksum, the debug table, its entries' data, and the module id: a handful
        // of small ranges, not the image. A masked set that grew past this would be hiding real
        // content behind the fingerprint.
        Assert.InRange(ranges.Count, 4, 12);
        Assert.InRange(ranges.Sum(r => r.Length), 16, 512);
        Assert.Contains(ranges, r => r.Length == 16);
    }

    [Fact]
    public void AFileThatIsNotAnImageIsHashedAsItIs()
    {
        byte[] bytes = "not a portable executable"u8.ToArray();
        using var stream = new MemoryStream(bytes);

        Assert.Empty(AssemblyContentFingerprint.VolatileRanges(stream));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), Fingerprint(bytes));
    }

    private static string Fingerprint(byte[] image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new MemoryStream(image);
        AssemblyContentFingerprint.Append(hash, stream);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>A deterministic build of <paramref name="source"/> that records <paramref name="pdbPath"/>.</summary>
    private static byte[] Emit(string source, string pdbPath)
    {
        var compilation = CSharpCompilation.Create(
            "Analyzer",
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));

        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var result = compilation.Emit(
            pe, pdb,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: pdbPath));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return pe.ToArray();
    }
}
