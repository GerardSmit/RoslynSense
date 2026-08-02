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
/// <b>Not</b> covered: live re-execution of a rebuilt generator inside the same MCP
/// process. Roslyn's internal generator host loads analyzer assemblies into the default,
/// non-collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>; once the V1
/// generator assembly is loaded there it cannot be replaced with a V2 build without
/// process restart. The MCP server's existing watcher-driven workspace eviction still
/// re-binds with a fresh shadow copy, which is correct for the file-lock fix, but the
/// in-process generator output stays pinned to whatever was first loaded.
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
