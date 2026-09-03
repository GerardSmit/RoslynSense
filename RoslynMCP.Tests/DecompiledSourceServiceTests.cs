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

    /// <summary>
    /// The temp root is shared by every RoslynSense process on the machine, so the startup sweep
    /// has to tell a crash leftover from an editor session that is still reading its copies.
    /// </summary>
    [Fact]
    public void WhenATempDirectoryNamesALiveProcessThenItIsNotOrphaned()
    {
        Assert.True(DecompiledSourceService.IsClaimedByALiveProcess(
            $"{Environment.ProcessId}-0123456789abcdef"));

        // No process has this id: it is above the range Windows hands out at all.
        Assert.False(DecompiledSourceService.IsClaimedByALiveProcess(
            $"{int.MaxValue}-0123456789abcdef"));

        // Written by a build that named its directories after nothing but a GUID: it names no
        // owner, so it is left where it is rather than deleted out from under one.
        Assert.True(DecompiledSourceService.IsClaimedByALiveProcess("0123456789abcdef"));
    }

    /// <summary>
    /// What the sweep does with the two: the leftover goes, and the live session's copies — which
    /// it could not have deleted anyway, being mapped — are left where they are rather than
    /// half-emptied and reported as a failure.
    /// </summary>
    [Fact]
    public void WhenSweepingThenOnlyTheUnclaimedDirectoriesGo()
    {
        string root = Path.Combine(Path.GetTempPath(), "roslyn-sense-sweep", Guid.NewGuid().ToString("N"));

        string live = Path.Combine(root, $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        string dead = Path.Combine(root, $"{int.MaxValue}-{Guid.NewGuid():N}");
        string legacy = Path.Combine(root, Guid.NewGuid().ToString("N"));

        try
        {
            foreach (string directory in new[] { live, dead, legacy })
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "Copied.dll"), "not really");
            }

            DecompiledSourceService.CleanupOrphanedTempDirs(root);

            Assert.True(Directory.Exists(live));
            Assert.False(Directory.Exists(dead));
            Assert.True(Directory.Exists(legacy));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
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
    public async Task WhenATypeIsDecompiledToFileThenTheFileExistsAndThePositionPointsAtIt()
    {
        // The Search Everywhere metadata hits resolve through this: the same physical file F12
        // lands on, with the declaration's position so the editor opens on the type.
        string assemblyPath = typeof(System.Diagnostics.Stopwatch).Assembly.Location;

        var resolved = await DecompiledSourceService.TryDecompileTypeToFileAsync(
            assemblyPath, "System.Diagnostics.Stopwatch");

        Assert.NotNull(resolved);
        var (filePath, line, character) = resolved!.Value;
        Assert.EndsWith("Decompiled.cs", filePath);
        Assert.True(File.Exists(filePath));

        string declarationLine = (await File.ReadAllLinesAsync(filePath))[line];
        Assert.Equal("Stopwatch", declarationLine.Substring(character, "Stopwatch".Length));
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
