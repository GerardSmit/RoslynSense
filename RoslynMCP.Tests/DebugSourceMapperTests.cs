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
/// The reverse of debug-time external source: a line inside a decompiled or fetched file mapped
/// back to the MethodDef token and IL offset it compiles from, which is what lets a breakpoint
/// set in such a file actually bind.
/// </summary>
public class DebugSourceMapperTests
{
    // === The decompiled lane, round-tripped through the decompiler's own sequence points ===

    [Fact]
    public async Task ALineOfDecompiledTextMapsBackToTheMethodItCameFrom()
    {
        // Forward: token + offset → decompiled file + line. Backward must return to the same
        // method — same data, read in the other direction.
        string assemblyPath = typeof(System.Diagnostics.Stopwatch).Assembly.Location;
        int token = MethodTokenOf(assemblyPath, "Stopwatch", "Restart");

        var resolved = await DecompiledSourceService.TryDecompileFrameAsync(
            assemblyPath, "System.Diagnostics.Stopwatch", token, ilOffset: 0);
        Assert.NotNull(resolved);
        var (filePath, line, _) = resolved!.Value;

        var target = await DebugSourceMapper.TryMapAsync(filePath, line);

        Assert.NotNull(target);
        Assert.Equal(token, target!.MethodToken);
        Assert.Equal("decompiled", target.Origin);
        Assert.True(target.Exact);
        Assert.Equal(line, target.Line);
        Assert.EndsWith("Stopwatch.Restart", target.MethodDisplayName);
    }

    [Fact]
    public async Task ALineOutsideTheDecompiledCacheMapsToNothing()
    {
        Assert.Null(await DebugSourceMapper.TryMapAsync(
            Path.Combine(Path.GetTempPath(), "NotOurs.cs"), 3));
    }

    // === The embedded lane, matched back to the PDB document the file was unpacked from ===

    [Fact]
    public async Task ALineOfAnEmbeddedSourceFileMapsBackThroughThePdb()
    {
        var (assemblyPath, sourcePath) = CompileProbe();
        int token = MethodTokenOf(assemblyPath, "Target", "Sum");

        // The forward resolution fetches the file into the cache and writes the sidecar the
        // reverse direction starts from.
        var resolved = await DebugFrameSource.TryResolveAsync(
            assemblyPath, token, ilOffset: 0, allowDecompile: false, CancellationToken.None);
        Assert.NotNull(resolved);
        Assert.Equal("embedded", resolved!.Origin);

        var target = await DebugSourceMapper.TryMapAsync(resolved.FilePath, resolved.Line);

        Assert.NotNull(target);
        Assert.Equal(token, target!.MethodToken);
        Assert.Equal("embedded", target.Origin);
        Assert.True(target.Exact);
        Assert.Equal(sourcePath, target.DocumentPath);
        Assert.EndsWith("Target.Sum", target.MethodDisplayName);
    }

    [Fact]
    public async Task ALineWithoutASequencePointSlidesDownToTheNextOne()
    {
        var (assemblyPath, _) = CompileProbe();
        int token = MethodTokenOf(assemblyPath, "Target", "Sum");
        var resolved = await DebugFrameSource.TryResolveAsync(
            assemblyPath, token, ilOffset: 0, allowDecompile: false, CancellationToken.None);
        Assert.NotNull(resolved);

        // Line 3 is the class declaration — no statement compiles from it, so the mapping must
        // slide down into the method body the way a breakpoint in real source does.
        var target = await DebugSourceMapper.TryMapAsync(resolved!.FilePath, 3);

        Assert.NotNull(target);
        Assert.Equal(token, target!.MethodToken);
        Assert.True(target.Line > 3);
    }

    // === The reference-source lane: no offsets exist, so the member's entry is the target ===

    [Fact]
    public async Task ALineOfAReferenceSourceFileMapsToTheEnclosingMethodsEntry()
    {
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

        string directory = Path.Combine(
            ExternalSourceCache.ReferenceSourceDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string filePath = Path.Combine(directory, "Target.cs");
        await File.WriteAllTextAsync(filePath, source);

        string assemblyPath = Path.Combine(directory, "Probe.dll");
        Compile(source, "Probe.cs", assemblyPath, embedSource: false);
        ExternalSourceProject.Ensure(
            new ExternalSourceResult(
                ExternalSourceKind.ReferenceSource, assemblyPath, filePath, [], Origin: null),
            "Probe.Target");

        // Line 7 is the return statement; the snapshot was never compiled, so the closest
        // honest target is the entry of the method holding the line — and the result says so.
        var target = await DebugSourceMapper.TryMapAsync(filePath, 7);

        Assert.NotNull(target);
        Assert.Equal(MethodTokenOf(assemblyPath, "Target", "Sum"), target!.MethodToken);
        Assert.Equal(0, target.IlOffset);
        Assert.False(target.Exact);
        Assert.Equal("reference source", target.Origin);
    }

    // === Helpers ===

    private static (string AssemblyPath, string SourcePath) CompileProbe()
    {
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
        string directory = Path.Combine(
            Path.GetTempPath(), "rmcp-sourcemap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "Target.cs");
        string assemblyPath = Path.Combine(directory, "Probe.dll");
        Compile(source, sourcePath, assemblyPath, embedSource: true);
        return (assemblyPath, sourcePath);
    }

    private static void Compile(
        string source, string sourcePath, string assemblyPath, bool embedSource)
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
            embeddedTexts: embedSource ? [EmbeddedText.FromSource(sourcePath, text)] : null);

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
}
