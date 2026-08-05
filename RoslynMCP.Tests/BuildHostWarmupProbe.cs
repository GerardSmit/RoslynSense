using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynMCP.Services;
using Xunit;
using Xunit.Abstractions;

namespace RoslynMCP.Tests;

/// <summary>
/// Where a BuildHost's start-up cost actually lands: connecting to the process, or the first
/// project it is asked to evaluate.
/// </summary>
/// <remarks>
/// The distinction decides what "warm" has to mean. If connecting is the cost, spawning the
/// process early is enough. If the first evaluation is the cost — MSBuild discovering toolsets and
/// SDK resolvers, then parsing the SDK's several hundred .props and .targets into the collection's
/// ProjectRootElementCache — then a host that has only been connected is not warm at all, and every
/// shard still pays a full initialisation inside the first request that reaches it.
/// </remarks>
public class BuildHostWarmupProbe(ITestOutputHelper output)
{
    [RoslynSenseBenchFact]
    public async Task WhereTheStartupCostLands()
    {
        WorkspaceService.EnsureRegistered();

        string dir = Path.Combine(Path.GetTempPath(), $"warmup-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            // Four separate trivial projects, so each load is a project this host has never seen.
            var paths = new string[4];
            for (int i = 0; i < paths.Length; i++)
            {
                paths[i] = Path.Combine(dir, $"P{i}.csproj");
                await File.WriteAllTextAsync(paths[i], """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net10.0</TargetFramework>
                      </PropertyGroup>
                    </Project>
                    """);
                await File.WriteAllTextAsync(Path.Combine(dir, $"C{i}.cs"), $"public class C{i} {{ }}");
            }

            var properties = ImmutableDictionary<string, string>.Empty
                .Add("DesignTimeBuild", "true")
                .Add("AlwaysUseNETSdkDefaults", "true");

            using var workspace = WorkspaceService.CreateWorkspace(TextWriter.Null);

            var watch = Stopwatch.StartNew();
            await using var manager = new BuildHostProcessManager(
                knownCommandLineParserLanguages: [LanguageNames.CSharp, LanguageNames.VisualBasic],
                globalMSBuildProperties: properties);
            long ctor = watch.ElapsedMilliseconds;

            await manager.GetBuildHostWithFallbackAsync(paths[0], CancellationToken.None);
            long connect = watch.ElapsedMilliseconds - ctor;

            var loads = new long[paths.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                long before = watch.ElapsedMilliseconds;

                var loader = new MSBuildProjectLoader(workspace, properties);
                var provider = new BuildHostProjectFileInfoProvider(
                    manager, loader.ProjectFileExtensionRegistry, loader.Reporter, progress: null);
                var infos = await loader.LoadInfosAsync(
                    [paths[i]], provider, ProjectMap.Create(workspace.CurrentSolution),
                    progress: null, CancellationToken.None);

                loads[i] = watch.ElapsedMilliseconds - before;
                Assert.NotEmpty(infos);
            }

            output.WriteLine($"manager ctor      : {ctor,6} ms");
            output.WriteLine($"connect to host   : {connect,6} ms");
            for (int i = 0; i < loads.Length; i++)
                output.WriteLine($"load P{i} (distinct): {loads[i],6} ms");

            output.WriteLine("");
            output.WriteLine(loads[0] > loads[1] * 2
                ? $"=> The FIRST EVALUATION carries the cost ({loads[0]} ms vs {loads[1]} ms). Connecting a "
                  + "host does not warm it; it has to be made to evaluate something."
                : $"=> Connecting carries the cost; evaluations are flat ({loads[0]} vs {loads[1]} ms).");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
