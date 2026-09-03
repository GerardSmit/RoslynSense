using System.Security.Cryptography;
using System.Text;
using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Matching a source file on this machine to the path a build recorded: the suffix ranking that
/// proposes a candidate, and the checksum that confirms it.
/// </summary>
public class SourceResolutionTests
{
    private static readonly Guid Sha256Id = new("8829d00f-11b8-4213-878b-770e8597ac16");
    private static readonly Guid Sha1Id = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");

    // === Suffix ranking ===

    [Fact]
    public void ADeeperSharedTailOutranksAShallowerOne()
    {
        // Two documents named Program.cs; the one that also agrees on its folder is the candidate
        // worth hashing first.
        const string local = @"D:\work\App\Services\Program.cs";

        int deep = SourcePaths.SharedSuffixLength("/_/App/Services/Program.cs", local);
        int shallow = SourcePaths.SharedSuffixLength("/_/Other/Program.cs", local);

        Assert.True(deep > shallow, $"expected {deep} > {shallow}");
    }

    [Fact]
    public void TheTwoPlatformsSeparatorsCountAsTheSameCharacter()
    {
        // A container build writes forward slashes for the same directories this machine spells
        // with backslashes; treating them as different would score every such pair at zero.
        Assert.Equal(
            "/App/Services/Program.cs".Length,
            SourcePaths.SharedSuffixLength("/src/App/Services/Program.cs", @"D:\work\App\Services\Program.cs"));
    }

    [Fact]
    public void HalfAFileNameIsNotEvidence()
    {
        // "oBar.cs" and "Bar.cs" share five characters and nothing else. Counting them would rank a
        // completely unrelated file above one that shares no tail at all.
        Assert.Equal(0, SourcePaths.SharedSuffixLength("/src/Quux/oBar.cs", @"D:\work\App\Bar.cs"));
    }

    [Fact]
    public void APathConsumedEntirelyCountsToItsStart()
    {
        // A relative document path is a whole tail: there is no earlier segment for the boundary to
        // fall in, so trimming back to one would throw the match away.
        Assert.Equal(
            "App/Program.cs".Length,
            SourcePaths.SharedSuffixLength("App/Program.cs", @"D:\work\App\Program.cs"));
    }

    // === Checksum confirmation ===

    [Fact]
    public void AFileThatHashesToWhatTheBuildRecordedMatches()
    {
        var file = Write("class C { }\n");
        try
        {
            Assert.True(SourceChecksum.Matches(file, Sha256Id, SHA256.HashData(File.ReadAllBytes(file))));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ADifferentFileOfTheSameNameDoesNotMatch()
    {
        // This is the whole point of hashing: two copies of Program.cs from different checkouts are
        // indistinguishable by path and must not be treated as the same file.
        var file = Write("class C { }\n");
        try
        {
            Assert.False(SourceChecksum.Matches(
                file, Sha256Id, SHA256.HashData(Encoding.UTF8.GetBytes("class D { }\n"))));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("class C { }\n", "class C { }\r\n")]
    [InlineData("class C { }\r\n", "class C { }\n")]
    public void NormalisedLineEndingsStillMatch(string onDisk, string asBuilt)
    {
        // A checkout that normalises line endings changes every hash without changing a single
        // character of the program. Rejecting these would mean rejecting nearly every working copy.
        var file = Write(onDisk);
        try
        {
            Assert.True(SourceChecksum.Matches(
                file, Sha256Id, SHA256.HashData(Encoding.UTF8.GetBytes(asBuilt))));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AByteOrderMarkOnOneSideStillMatches()
    {
        var file = Write("class C { }\n");
        try
        {
            var withBom = Encoding.UTF8.GetPreamble().Concat(File.ReadAllBytes(file)).ToArray();
            Assert.True(SourceChecksum.Matches(file, Sha1Id, SHA1.HashData(withBom)));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void APdbThatRecordedNoChecksumConfirmsNothing()
    {
        // Unknown has to read as "not confirmed": the answer is used to accept a match, never to
        // reject one, so treating an absent hash as agreement would bind against any same-named file.
        var file = Write("class C { }\n");
        try
        {
            Assert.False(SourceChecksum.Matches(file, Sha256Id, null));
            Assert.False(SourceChecksum.Matches(file, Sha256Id, []));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(32)]
    public void AnUnnamedAlgorithmIsRecognisedByTheLengthOfItsHash(int length)
    {
        // Load-bearing, not a nicety. A .NET Framework module's documents come from diasymreader,
        // whose algorithm id cannot be read without crashing the process (see DocumentsOf), so
        // every checksum from that reader arrives unnamed. If the length did not identify the
        // algorithm, breakpoints in every Framework module would silently stop binding by
        // checksum and fall back to matching on file name alone.
        var file = Write("class C { }\n");
        try
        {
            var content = File.ReadAllBytes(file);
            byte[] hash = length switch
            {
                16 => MD5.HashData(content),
                20 => SHA1.HashData(content),
                _ => SHA256.HashData(content),
            };

            Assert.Equal(length, hash.Length);
            Assert.True(SourceChecksum.Matches(file, Guid.Empty, hash));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AFileThatIsNotThereConfirmsNothing()
    {
        Assert.False(SourceChecksum.Matches(
            Path.Combine(Path.GetTempPath(), $"roslyn-sense-missing-{Guid.NewGuid():N}.cs"),
            Sha256Id,
            SHA256.HashData([1, 2, 3])));
    }

    private static string Write(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"roslyn-sense-src-{Guid.NewGuid():N}.cs");
        // Written as bytes so the line endings in the test are the line endings on disk.
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }
}
