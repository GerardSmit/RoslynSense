using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class DecompiledSourceServiceTests
{
    [Fact]
    public void WhenManifestFileNameProvidedThenIsGeneratedProjectPathReturnsTrue()
    {
        Assert.True(DecompiledSourceService.IsGeneratedProjectPath(
            Path.Combine("some", "dir", DecompiledSourceService.ManifestFileName)));
    }

    [Fact]
    public void WhenRegularCsprojProvidedThenIsGeneratedProjectPathReturnsFalse()
    {
        Assert.False(DecompiledSourceService.IsGeneratedProjectPath("MyProject.csproj"));
    }

    [Fact]
    public void WhenFileInNonExistentDirectoryThenTryGetGeneratedProjectPathReturnsNull()
    {
        var result = DecompiledSourceService.TryGetGeneratedProjectPath(
            Path.Combine("Z:", "nonexistent", "file.cs"));

        Assert.Null(result);
    }

    [Fact]
    public void WhenEmptyDirectoryThenTryGetGeneratedProjectPathReturnsNull()
    {
        var result = DecompiledSourceService.TryGetGeneratedProjectPath("file.cs");

        Assert.Null(result);
    }

    [Fact]
    public void WhenFileInRealDirectoryWithoutManifestThenTryGetGeneratedProjectPathReturnsNull()
    {
        // Use a known directory that doesn't have a manifest
        var result = DecompiledSourceService.TryGetGeneratedProjectPath(
            FixturePaths.CalculatorFile);

        Assert.Null(result);
    }

    [Fact]
    public async Task WhenDecompiledProjectOpenedThenTargetAssemblyIsNotLockedOnDisk()
    {
        // Regression: CreateMetadataReferences used MetadataReference.CreateFromFile, which
        // memory-maps the DLL and holds a file lock for the cached AdhocWorkspace's lifetime.
        // When the target lives in a project's bin/ output, that blocked the user's rebuild.
        string dir = Path.Combine(
            Path.GetTempPath(), "rmcp-decompiled-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Copy a real assembly to act as the decompile target (simulates a bin/ output).
            string targetDll = Path.Combine(dir, "Target.Sample.dll");
            File.Copy(typeof(DecompiledSourceServiceTests).Assembly.Location, targetDll);

            string sourceFile = Path.Combine(dir, "Decompiled.cs");
            await File.WriteAllTextAsync(sourceFile, "namespace Decompiled; public class C { }");

            string manifestPath = Path.Combine(dir, DecompiledSourceService.ManifestFileName);
            await File.WriteAllTextAsync(manifestPath,
                $$"""
                {
                    "AssemblyPath": {{System.Text.Json.JsonSerializer.Serialize(targetDll)}},
                    "SourceFilePath": {{System.Text.Json.JsonSerializer.Serialize(sourceFile)}},
                    "TypeReflectionName": "Decompiled.C"
                }
                """);

            var (workspace, _, tempDir) = await DecompiledSourceService.OpenProjectAsync(manifestPath);
            try
            {
                // The target + co-located DLLs are referenced from a temp copy, not the original.
                Assert.NotNull(tempDir);
                Assert.True(Directory.Exists(tempDir));
                Assert.True(File.Exists(Path.Combine(tempDir!, "Target.Sample.dll")));

                // While the workspace is alive, the original DLL must be writable/deletable.
                // An exclusive open throws IOException if anything still holds the file.
                using (new FileStream(targetDll, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // No exception → not locked.
                }
            }
            finally
            {
                workspace.Dispose();
                // Mirror what CachedWorkspaceEntry.Dispose does in production.
                if (tempDir is not null)
                    DecompiledSourceService.TryDeleteTempDir(tempDir);
            }

            // After disposal the temp copies are gone.
            Assert.False(Directory.Exists(tempDir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
