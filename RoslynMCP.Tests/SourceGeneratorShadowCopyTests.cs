using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// End-to-end tests for the workspace-driven source-generator shadow-copy path.
/// <para>
/// The <see cref="ShadowCopyAnalyzerAssemblyLoader"/> rebinds every non-NuGet
/// <see cref="Microsoft.CodeAnalysis.Diagnostics.AnalyzerFileReference"/> on a loaded
/// solution to a temp-copy load context so that <c>dotnet build</c> can overwrite the
/// project-output source-generator DLL while the MCP workspace still holds it. These
/// tests verify both halves of that contract: (1) the original generator DLL is not
/// locked while the workspace is open and the generator has run, and (2) a fresh
/// <c>dotnet build</c> of the generator project succeeds while the consumer workspace
/// is open (the canonical user-reported repro).
/// </para>
/// <para>
/// A generator rebuild while the consumer is open is handled in place: the watcher event
/// swaps the affected projects' <c>AnalyzerFileReference</c>s to a fresh shadow copy served
/// from a fresh collectible ALC, and the cached workspace itself survives. Eviction remains
/// only as a fallback when the swap fails.
/// </para>
/// </summary>
[Collection(SharedState.Name)]
public class SourceGeneratorShadowCopyTests
{
    [Fact]
    public async Task WhenConsumerWorkspaceOpenAndGeneratorHasRunThenOriginalDllIsNotLocked()
    {
        await WorkspaceService.EvictAllAsync();
        try
        {
            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SourceGenConsumerProjectFile);

            // Force the generator pipeline to actually execute, mirroring what tools
            // like ListSourceGeneratedFiles do on real user projects. The bug reported
            // by the user surfaced after navigation tools had already triggered SG runs.
            var compilation = await project.GetCompilationAsync();
            Assert.NotNull(compilation);
            var generatedDocs = await project.GetSourceGeneratedDocumentsAsync();
            Assert.NotEmpty(generatedDocs);

            // Drop orphaned PEReader instances from any pre-rebind AnalyzerFileReference
            // objects so this test isolates the lock state of the currently-bound refs.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Critical assertion: the original (non-shadow) generator DLL is openable
            // with FileShare.None — i.e. nothing in our process holds an exclusive lock
            // on it. Without the shadow-copy rebind in WorkspaceService this throws
            // IOException ("file in use by another process").
            Assert.True(File.Exists(FixturePaths.SourceGenGeneratorDll),
                $"Generator DLL missing: {FixturePaths.SourceGenGeneratorDll}");

            using var fs = new FileStream(
                FixturePaths.SourceGenGeneratorDll,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            Assert.True(fs.Length > 0);
        }
        finally
        {
            await WorkspaceService.EvictAllAsync();
        }
    }

    [Fact]
    public async Task WhenConsumerWorkspaceOpenThenDotnetBuildOfGeneratorSucceeds()
    {
        await WorkspaceService.EvictAllAsync();
        try
        {
            // Open the consumer and force the generator to load (the failure mode in the
            // user's bug report appeared only after SGs had run, not just after project open).
            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SourceGenConsumerProjectFile);
            await project.GetCompilationAsync();
            await project.GetSourceGeneratedDocumentsAsync();

            // The canonical user repro: invoke `dotnet build` against the generator project
            // while the consumer workspace is open and the generator has already executed.
            // Without the shadow-copy fix this fails with MSB3027 / MSB3021 — MSBuild can't
            // overwrite the locked bin\Debug\netstandard2.0\Generator.dll.
            string buildOutput = await RunDotnetBuildAsync(FixturePaths.SourceGenGeneratorProjectFile);
            Assert.Contains("Build succeeded", buildOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MSB3027", buildOutput);
            Assert.DoesNotContain("MSB3021", buildOutput);
            Assert.DoesNotContain("being used by another process", buildOutput);
        }
        finally
        {
            await WorkspaceService.EvictAllAsync();
        }
    }

    [Fact]
    public async Task WhenGeneratorIsAlreadyBuiltThenOpeningTheConsumerBuildsNothingAndReopenHitsTheCache()
    {
        await WorkspaceService.EvictAllAsync();
        try
        {
            // An already-built generator is the steady state of every open after the first:
            // the pre-load build must recognise it and stay silent, because the DLL write a
            // redundant build produces is a rebuild event that used to evict the freshly
            // cached workspace — the "my cache is useless" bug.
            await RunDotnetBuildAsync(FixturePaths.SourceGenGeneratorProjectFile);
            var stamp = File.GetLastWriteTimeUtc(FixturePaths.SourceGenGeneratorDll);

            var (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SourceGenConsumerProjectFile);
            await project.GetCompilationAsync();

            Assert.Equal(stamp, File.GetLastWriteTimeUtc(FixturePaths.SourceGenGeneratorDll));

            var (reopened, _) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SourceGenConsumerProjectFile);
            Assert.Same(workspace, reopened);
        }
        finally
        {
            await WorkspaceService.EvictAllAsync();
        }
    }

    [Fact]
    public async Task WhenTheGeneratorIsRebuiltThenOnlyItsAnalyzerReferenceMovesAndTheWorkspaceSurvives()
    {
        // A private copy of the fixture: the test edits generator source and rebuilds, and
        // the shared fixture must stay pristine for every other class in the collection.
        string tempDir = Path.Combine(Path.GetTempPath(), $"SourceGenRefresh_{Guid.NewGuid():N}");
        CopyDirectory(FixturePaths.SourceGenFixtureDir, tempDir);
        string generatorProject = Path.Combine(tempDir, "Generator", "Generator.csproj");
        string consumerProject = Path.Combine(tempDir, "Consumer", "Consumer.csproj");

        try
        {
            await RunDotnetBuildAsync(generatorProject);

            var (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(consumerProject);
            await project.GetCompilationAsync();
            await project.GetSourceGeneratedDocumentsAsync();

            string oldShadowPath = GeneratorReferencePath(workspace, project.Id);

            // A real generator change — the generated payload moves from V1 to V2, so the only
            // way the final assertion can pass is the rebuilt assembly actually executing (a
            // reference swap that still serves the old ALC would keep emitting V1). The
            // directory watcher's debounce plus fingerprint check stand between the DLL write
            // and the refresh, so the new reference is polled for rather than awaited.
            string generatorSource = Path.Combine(tempDir, "Generator", "HelloGenerator.cs");
            File.WriteAllText(generatorSource, File.ReadAllText(generatorSource).Replace("V1", "V2"));
            await RunDotnetBuildAsync(generatorProject);

            string newShadowPath = oldShadowPath;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                newShadowPath = GeneratorReferencePath(workspace, project.Id);
                if (!string.Equals(newShadowPath, oldShadowPath, StringComparison.OrdinalIgnoreCase))
                    break;
                await Task.Delay(250);
            }

            Assert.NotEqual(oldShadowPath, newShadowPath);
            Assert.True(File.Exists(newShadowPath),
                $"refreshed analyzer reference points at a missing file: {newShadowPath}");

            // The rebuilt generator must actually run — through a fresh collectible ALC, and
            // past the compilation tracker's identity-matched instance reuse (the enqueue of
            // forceRegeneration is batched, so the new output is polled for too).
            string generatedText = "";
            while (DateTime.UtcNow < deadline)
            {
                var refreshedProject = workspace.CurrentSolution.GetProject(project.Id);
                Assert.NotNull(refreshedProject);
                var generatedDocs = (await refreshedProject.GetSourceGeneratedDocumentsAsync()).ToList();
                var generatedDoc = Assert.Single(generatedDocs);
                generatedText = (await generatedDoc.GetTextAsync()).ToString();
                if (generatedText.Contains("V2"))
                    break;
                await Task.Delay(250);
            }
            Assert.Contains("V2", generatedText);

            // The point of the in-place swap: the rebuild cost one analyzer reference,
            // not the whole cached workspace. Before the fix this Assert.Same failed —
            // the rebuild evicted the entry and re-open paid a full MSBuild load.
            var (reopened, _) = await WorkspaceService.GetOrOpenProjectAsync(consumerProject);
            Assert.Same(workspace, reopened);
        }
        finally
        {
            await WorkspaceService.EvictAllAsync();
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task WhenTwoGeneratorsAreUnbuiltThenOneColdOpenBuildsBothAndNothingInvalidatesAfterward()
    {
        // The multi-generator open-order guarantee: every unbuilt generator is built before
        // the workspace load starts, and the rebuild watchers only arm afterward (with a
        // fingerprint baseline) — so N pre-load builds produce zero invalidation events, not
        // N-1 wasted refreshes of a workspace that just loaded.
        string tempDir = CreateTwoGeneratorFixture();
        string consumerProject = Path.Combine(tempDir, "Consumer", "Consumer.csproj");

        try
        {
            var (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(consumerProject);
            await project.GetCompilationAsync();
            Assert.True(File.Exists(Path.Combine(tempDir, "Generator", "bin", "Debug", "netstandard2.0", "Generator.dll")),
                "Generator.dll missing: the pre-load build skipped or failed generator 1");
            Assert.True(File.Exists(Path.Combine(tempDir, "Generator2", "bin", "Debug", "netstandard2.0", "Generator2.dll")),
                "Generator2.dll missing: the pre-load build skipped or failed generator 2");

            // One doc per generator. A generator that silently drops to zero output (the
            // failure mode when its assembly cannot load) shows up here as a missing doc
            // plus CS0103s on the consumer code that uses its output.
            var generatedDocs = (await project.GetSourceGeneratedDocumentsAsync()).ToList();
            var compilation = await project.GetCompilationAsync();
            Assert.True(generatedDocs.Count == 2,
                $"expected one generated doc per generator, got [{string.Join(", ", generatedDocs.Select(d => d.FilePath))}]; " +
                $"diagnostics: [{string.Join("; ", compilation!.GetDiagnostics().Where(d => d.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning).Take(5))}]");

            string path1 = GeneratorReferencePath(workspace, project.Id);
            string path2 = GeneratorReferencePath(workspace, project.Id, "Generator2.dll");

            // Outwait the rebuild watcher's quiet window: had the pre-load builds been seen
            // as rebuilds, the refresh would have moved these references by now.
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.Equal(path1, GeneratorReferencePath(workspace, project.Id));
            Assert.Equal(path2, GeneratorReferencePath(workspace, project.Id, "Generator2.dll"));

            var (reopened, _) = await WorkspaceService.GetOrOpenProjectAsync(consumerProject);
            Assert.Same(workspace, reopened);
        }
        finally
        {
            await WorkspaceService.EvictAllAsync();
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task WhenBothGeneratorsAreRebuiltThenBothReferencesMoveAndTheWorkspaceStillSurvives()
    {
        string tempDir = CreateTwoGeneratorFixture();
        string consumerProject = Path.Combine(tempDir, "Consumer", "Consumer.csproj");
        string gen1Project = Path.Combine(tempDir, "Generator", "Generator.csproj");
        string gen2Project = Path.Combine(tempDir, "Generator2", "Generator2.csproj");

        try
        {
            await RunDotnetBuildAsync(gen1Project);
            await RunDotnetBuildAsync(gen2Project);

            var (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(consumerProject);
            await project.GetCompilationAsync();
            await project.GetSourceGeneratedDocumentsAsync();

            string oldPath1 = GeneratorReferencePath(workspace, project.Id);
            string oldPath2 = GeneratorReferencePath(workspace, project.Id, "Generator2.dll");

            // Each output directory fires its own refresh; two rebuilt generators must cost
            // two in-place reference swaps and still zero workspace reloads.
            File.AppendAllText(Path.Combine(tempDir, "Generator", "HelloGenerator.cs"), "\n// rebuilt\n");
            File.AppendAllText(Path.Combine(tempDir, "Generator2", "HelloGenerator.cs"), "\n// rebuilt\n");
            await RunDotnetBuildAsync(gen1Project);
            await RunDotnetBuildAsync(gen2Project);

            string newPath1 = oldPath1;
            string newPath2 = oldPath2;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                newPath1 = GeneratorReferencePath(workspace, project.Id);
                newPath2 = GeneratorReferencePath(workspace, project.Id, "Generator2.dll");
                if (!string.Equals(newPath1, oldPath1, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(newPath2, oldPath2, StringComparison.OrdinalIgnoreCase))
                    break;
                await Task.Delay(250);
            }

            Assert.NotEqual(oldPath1, newPath1);
            Assert.NotEqual(oldPath2, newPath2);
            Assert.True(File.Exists(newPath1), $"refreshed reference points at a missing file: {newPath1}");
            Assert.True(File.Exists(newPath2), $"refreshed reference points at a missing file: {newPath2}");

            var (reopened, _) = await WorkspaceService.GetOrOpenProjectAsync(consumerProject);
            Assert.Same(workspace, reopened);
        }
        finally
        {
            await WorkspaceService.EvictAllAsync();
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The consumer's analyzer reference to the named generator DLL, as currently bound in
    /// <paramref name="workspace"/> — a shadow path after the rebind, which is exactly what
    /// the refresh is expected to move.
    /// </summary>
    private static string GeneratorReferencePath(
        Microsoft.CodeAnalysis.Workspace workspace,
        Microsoft.CodeAnalysis.ProjectId projectId,
        string dllFileName = "Generator.dll")
    {
        var project = workspace.CurrentSolution.GetProject(projectId);
        Assert.NotNull(project);
        var reference = project.AnalyzerReferences
            .OfType<Microsoft.CodeAnalysis.Diagnostics.AnalyzerFileReference>()
            .SingleOrDefault(r => string.Equals(
                Path.GetFileName(r.FullPath), dllFileName, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(reference);
        return reference.FullPath;
    }

    /// <summary>
    /// A private fixture copy whose consumer references <b>two</b> generator projects: the
    /// stock <c>Generator</c> plus a <c>Generator2</c> cloned from it (namespaces renamed so
    /// the two generated sources don't collide in the consumer's compilation).
    /// </summary>
    private static string CreateTwoGeneratorFixture()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"SourceGenTwo_{Guid.NewGuid():N}");
        CopyDirectory(FixturePaths.SourceGenFixtureDir, tempDir);

        string gen2Dir = Path.Combine(tempDir, "Generator2");
        CopyDirectory(Path.Combine(tempDir, "Generator"), gen2Dir);
        File.Move(
            Path.Combine(gen2Dir, "Generator.csproj"),
            Path.Combine(gen2Dir, "Generator2.csproj"));
        string gen2Source = Path.Combine(gen2Dir, "HelloGenerator.cs");
        File.WriteAllText(gen2Source, File.ReadAllText(gen2Source).Replace("HelloGen", "HelloGen2"));

        string consumerProject = Path.Combine(tempDir, "Consumer", "Consumer.csproj");
        File.WriteAllText(consumerProject, File.ReadAllText(consumerProject).Replace(
            "</ItemGroup>",
            "  <ProjectReference Include=\"..\\Generator2\\Generator2.csproj\"\n" +
            "                    OutputItemType=\"Analyzer\"\n" +
            "                    ReferenceOutputAssembly=\"false\" />\n" +
            "  </ItemGroup>"));

        return tempDir;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            string dirName = Path.GetFileName(dir);
            if (dirName is "obj" or "bin") continue;
            CopyDirectory(dir, Path.Combine(destDir, dirName));
        }
    }

    /// <summary>
    /// Invokes <c>dotnet build</c> in a child process and returns its stdout. Fails fast
    /// with the stdout dump on a non-zero exit code so test output explains the failure.
    /// </summary>
    private static async Task<string> RunDotnetBuildAsync(string projectPath)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{projectPath}\" --configuration Debug --nologo",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(projectPath),
            },
        };
        process.StartInfo.Environment["MSBUILDTERMINALLOGGER"] = "off";

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"dotnet build failed with exit code {process.ExitCode}\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");

        return stdout;
    }
}
