using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using RoslynMCP.Services.ExternalSource;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Debug-time external source: a stopped frame — module, method token, IL offset — resolved to
/// a file and the line the IP is executing, so stepping into a dependency shows real code.
/// </summary>
public class DebugFrameSourceTests
{
    // === Sequence point selection ===

    [Fact]
    public void TheStatementContainingTheOffsetIsPicked()
    {
        // Points at 0, 5, 12; an IP of 7 is inside the statement that starts at 5.
        var points = new[] { (0, false), (5, false), (12, false) };

        Assert.Equal(1, DebugFrameSource.PickSequencePoint(points, 7));
    }

    [Fact]
    public void AnIpStillInThePrologueFallsBackToTheFirstRealStatement()
    {
        // The first non-hidden point starts past the IP — a stop in the method prologue.
        var points = new[] { (4, false), (9, false) };

        Assert.Equal(0, DebugFrameSource.PickSequencePoint(points, 2));
    }

    [Fact]
    public void HiddenPointsAreNeverPicked()
    {
        // 0xFEEFEE points cover compiler plumbing; landing a reader on one shows nothing.
        var points = new[] { (0, true), (6, false) };

        Assert.Equal(1, DebugFrameSource.PickSequencePoint(points, 3));
    }

    [Fact]
    public void AMethodWithOnlyHiddenPointsYieldsNoPick()
    {
        Assert.Equal(-1, DebugFrameSource.PickSequencePoint([(0, true)], 0));
    }

    // === The embedded-source lane: exact line through the PDB's own sequence points ===

    [Fact]
    public async Task WhenThePdbCarriesTheSourceThenAFrameResolvesToTheExecutingLine()
    {
        // An assembly whose embedded PDB embeds its source is the whole chain minus the
        // network: sequence points map the IL offset, the document comes out of the PDB.
        string directory = Path.Combine(
            Path.GetTempPath(), "rmcp-framesource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "Target.cs");
        string assemblyPath = Path.Combine(directory, "Probe.dll");

        const string source = """
            namespace Probe;

            public static class Target
            {
                public static int Sum(int a, int b)
                {
                    return a + b;
                }
            }
            """;
        CompileWithEmbeddedSource(source, sourcePath, assemblyPath);

        int token = MethodTokenOf(assemblyPath, "Target", "Sum");
        var resolved = await DebugFrameSource.TryResolveAsync(
            assemblyPath, token, ilOffset: 0, allowDecompile: false, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("embedded", resolved!.Origin);
        // Offset 0 is the method's first sequence point: the opening brace or the return,
        // depending on how the compiler laid the prologue out. Both sit in the body.
        Assert.InRange(resolved.Line, 6, 7);
        Assert.Contains("return a + b;", await File.ReadAllTextAsync(resolved.FilePath));
    }

    // === The decompiled lane: exact line through the decompiler's sequence points ===

    [Fact]
    public async Task WhenAFrameIsDecompiledThenTheOffsetLandsInsideTheMethod()
    {
        string assemblyPath = typeof(System.Diagnostics.Stopwatch).Assembly.Location;
        int token = MethodTokenOf(assemblyPath, "Stopwatch", "Restart");

        var resolved = await DecompiledSourceService.TryDecompileFrameAsync(
            assemblyPath, "System.Diagnostics.Stopwatch", token, ilOffset: 0);

        Assert.NotNull(resolved);
        var (filePath, line, _) = resolved!.Value;
        Assert.True(File.Exists(filePath));

        // Offset 0 maps to the first statement, a line or two below the method header.
        string[] lines = await File.ReadAllLinesAsync(filePath);
        string window = string.Join('\n', lines[Math.Max(0, line - 4)..Math.Min(lines.Length, line + 1)]);
        Assert.Contains("Restart", window);
    }

    // === The reference-source lane's member locator ===

    [Fact]
    public async Task AnAccessorTokenLandsOnThePropertyItBelongsTo()
    {
        string filePath = WriteTempSource("""
            namespace Probe
            {
                public class Holder
                {
                    public int Count { get; set; }
                }
            }
            """);

        var position = await DebugFrameSource.MemberPositionAsync(
            filePath, "get_Count", parameterCount: 0, CancellationToken.None);

        Assert.NotNull(position);
        Assert.Equal((4, 19), position!.Value);
    }

    [Fact]
    public async Task AConstructorTokenLandsOnTheConstructorWithMatchingArity()
    {
        string filePath = WriteTempSource("""
            namespace Probe
            {
                public class Holder
                {
                    public Holder() { }
                    public Holder(int seed) { }
                }
            }
            """);

        var position = await DebugFrameSource.MemberPositionAsync(
            filePath, ".ctor", parameterCount: 1, CancellationToken.None);

        Assert.NotNull(position);
        Assert.Equal((5, 15), position!.Value);
    }

    [Fact]
    public async Task ACompilerGeneratedNameHasNoDeclarationToLandOn()
    {
        string filePath = WriteTempSource("public class C { }");

        var position = await DebugFrameSource.MemberPositionAsync(
            filePath, "<Main>b__0_0", parameterCount: 0, CancellationToken.None);

        Assert.Null(position);
    }

    // === Helpers ===

    private static void CompileWithEmbeddedSource(
        string source, string sourcePath, string assemblyPath)
    {
        var text = SourceText.From(source, Encoding.UTF8);
        var tree = CSharpSyntaxTree.ParseText(text, path: sourcePath);

        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(assemblyPath),
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = File.Create(assemblyPath);
        var result = compilation.Emit(
            peStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded),
            embeddedTexts: [EmbeddedText.FromSource(sourcePath, text)]);

        Assert.True(result.Success, string.Join('\n', result.Diagnostics));
    }

    private static int MethodTokenOf(string assemblyPath, string typeName, string methodName)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();

        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            if (metadata.GetString(type.Name) != typeName)
                continue;

            foreach (var methodHandle in type.GetMethods())
            {
                if (metadata.GetString(metadata.GetMethodDefinition(methodHandle).Name) == methodName)
                    return MetadataTokens.GetToken(methodHandle);
            }
        }

        Assert.Fail($"{typeName}.{methodName} not found in {assemblyPath}");
        return 0;
    }

    private static string WriteTempSource(string source)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "rmcp-membersource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string filePath = Path.Combine(directory, "Source.cs");
        File.WriteAllText(filePath, source);
        return filePath;
    }
}
