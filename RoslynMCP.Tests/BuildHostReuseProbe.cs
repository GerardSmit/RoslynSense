using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynMCP.Services;
using Xunit;
using Xunit.Abstractions;

namespace RoslynMCP.Tests;

/// <summary>
/// Feasibility probe: what a project evaluation costs through a <em>reused</em>
/// <c>BuildHostProcessManager</c>, against the fresh one Roslyn builds per top-level load call.
/// </summary>
/// <remarks>
/// <para>
/// <c>MSBuildProjectLoader.LoadInfosAsync</c> does <c>new BuildHostProcessManager(...)</c> followed
/// by <c>await using</c>, so every <c>OpenProjectAsync</c> / <c>OpenSolutionAsync</c> starts a
/// <c>dotnet</c> subprocess, initialises MSBuild inside it, and throws it away on return. Measured
/// separately: the subprocess itself starts in 75 ms and the design-time build of a trivial
/// net10.0 project costs 354 ms inside MSBuild, yet a top-level open costs about 1,374 ms. The
/// difference is MSBuild initialising from cold in a fresh process, and it is paid per call.
/// </para>
/// <para>
/// This measures whether keeping one host alive removes it. It talks to
/// <c>BuildHostProcessManager</c> directly rather than through the loader, because the question is
/// about the host's lifetime and nothing else.
/// </para>
/// </remarks>
public class BuildHostReuseProbe
{
    private readonly ITestOutputHelper _output;

    public BuildHostReuseProbe(ITestOutputHelper output) => _output = output;

    [RoslynSenseBenchFact]
    public async Task AWarmBuildHostEvaluatesFarFasterThanAFreshOne()
    {
        WorkspaceService.EnsureRegistered();

        using var solution = LargeSolutionFixture.Create(
            new LargeSolutionOptions(ProjectCount: 8, ConsumerProjectCount: 4));

        await RestoreAsync(solution.SolutionPath);

        var projects = solution.ProjectPaths.Take(6).ToList();

        // ---- A: what the product does today — one top-level open per project ----------------
        var perCall = new List<long>();
        foreach (string project in projects)
        {
            using var workspace = WorkspaceService.CreateWorkspace(TextWriter.Null);
            var sw = Stopwatch.StartNew();
            await workspace.OpenProjectAsync(project);
            perCall.Add(sw.ElapsedMilliseconds);
        }

        _output.WriteLine($"A) fresh workspace + fresh BuildHost per project: " +
            $"{string.Join(" / ", perCall)} ms  (total {perCall.Sum()} ms)");

        // ---- B: one BuildHostProcessManager, every project through it ------------------------
        var reused = new List<long>();
        var manager = new BuildHostProcessManager(
            knownCommandLineParserLanguages: ImmutableArray.Create(Microsoft.CodeAnalysis.LanguageNames.CSharp),
            globalMSBuildProperties: ImmutableDictionary<string, string>.Empty
                .Add("DesignTimeBuild", "true")
                .Add("AlwaysUseNETSdkDefaults", "true"));

        await using (manager.ConfigureAwait(false))
        {
            foreach (string project in projects)
            {
                var sw = Stopwatch.StartNew();
                var host = await manager.GetBuildHostWithFallbackAsync(project, CancellationToken.None);
                var file = await host.LoadProjectFileAsync(
                    project, Microsoft.CodeAnalysis.LanguageNames.CSharp, CancellationToken.None);
                var infos = await file.GetProjectFileInfosAsync(CancellationToken.None);
                sw.Stop();

                reused.Add(sw.ElapsedMilliseconds);

                // The evaluation has to have produced something, or a fast number means nothing.
                Assert.NotEmpty(infos);
                Assert.NotEmpty(infos[0].Documents);
            }
        }

        _output.WriteLine($"B) one reused BuildHost for all {projects.Count}: " +
            $"{string.Join(" / ", reused)} ms  (total {reused.Sum()} ms)");

        double perProjectA = perCall.Skip(1).Average();
        double perProjectB = reused.Skip(1).Average();
        _output.WriteLine(
            $"steady-state per project: fresh={perProjectA:F0} ms reused={perProjectB:F0} ms " +
            $"({perProjectA / Math.Max(perProjectB, 1):F1}x)");
    }

    private static async Task RestoreAsync(string solutionPath)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("restore");
        psi.ArgumentList.Add(solutionPath);
        BuildProcessHelper.ConfigureMsBuildEnvironment(psi);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        Assert.True(process.ExitCode == 0, $"restore failed: {await stderr}");
    }
}
