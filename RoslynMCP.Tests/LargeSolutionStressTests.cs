using System.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

namespace RoslynMCP.Tests;

/// <summary>
/// Drives the real <c>roslyn-sense --lsp</c> process against a large generated solution and
/// asserts it stays answerable the whole time it loads.
/// </summary>
/// <remarks>
/// <para>
/// The regression this guards against showed up only at scale, on an 87-project solution: the
/// Solution Explorer stayed empty, expanding a node spun forever, and a wall of phantom CS0012
/// errors appeared as the user scrolled a <c>.proto</c>. Two root causes, both about a request that
/// was never asked for pulling in far more than it needed: <c>ProtoReferenceService</c> eagerly
/// opened every project that consumes a shared contracts project the moment a code lens resolved —
/// which happens on every scroll — and the Solution Explorer tree blocked on MSBuild project
/// evaluation instead of answering from the <c>.sln</c> parse alone. Neither reproduces on the
/// small fixtures the rest of the suite uses: they are small enough that eager loading finishes
/// before anyone would notice, and that is exactly the gap this test closes.
/// </para>
/// <para>
/// One long, sequential test rather than several short ones: the six things being checked are
/// stages of a single session — cold, then expanded, then a scroll, then a real edit, then a
/// deliberate search — and splitting them apart would mean paying for a fresh 25-project fixture
/// and a fresh server process per stage, which is what makes this kind of test slow in the first
/// place. <see cref="SandboxProbeTests"/> and <see cref="LspEndToEndTests"/> follow the same
/// out-of-process, one-test-per-scenario shape for the same reason.
/// </para>
/// </remarks>
public class LargeSolutionStressTests
{
    private readonly ITestOutputHelper _output;

    public LargeSolutionStressTests(ITestOutputHelper output) => _output = output;

    // Deliberately smaller than the 87-project solution that surfaced the regression: large enough
    // that "opened every consumer instead of one" and "evaluated every project instead of one" both
    // show up as an unmistakable latency cliff rather than a rounding error, small enough that the
    // MSBuild restore and evaluation the fixture itself needs do not make the suite glacial.
    // ConsumerProjectCount stays well under ProjectCount so "who consumes the contract" is a real
    // filter rather than the whole solution by another name.
    private const int FixtureProjectCount = 25;
    private const int FixtureConsumerProjectCount = 8;

    /// <summary>
    /// The budget every solutionTree request is held to. Generous next to what a parse-only answer
    /// should cost — single-digit milliseconds — but tight enough that "spins forever", the actual
    /// user complaint, cannot pass by accident.
    /// </summary>
    private static readonly TimeSpan TreeBudget = TimeSpan.FromSeconds(5);

    /// <remarks>
    /// Opt-in, behind the same gate as <see cref="LargeSolutionBenchmarks"/>. Everything this needs
    /// to be a meaningful test — twenty-five generated projects, a NuGet restore over them and a
    /// real out-of-process server — is exactly what an ordinary <c>dotnet test</c> must not pay
    /// for, and the cross-project find-usages stage alone is the better part of a minute. Correct
    /// but slow tests are how a suite stops being run at all, on a developer's machine and on a
    /// build that is charged by the minute.
    /// </remarks>
    [RoslynSenseBenchFact]
    public async Task LargeSolutionStaysResponsiveAndNeverPreloadsIncidentally()
    {
        var solution = LargeSolutionFixture.Create(new LargeSolutionOptions(
            ProjectCount: FixtureProjectCount, ConsumerProjectCount: FixtureConsumerProjectCount));

        // Restored before the server is started, and reported on its own line.
        //
        // This test used to hand the server a solution that had never been restored, so the first
        // request that needed a project also paid for NuGet — and every stage timing below carried
        // a share of it. That is not a measurement of RoslynSense: a cold package cache, a slow
        // feed or an offline machine would all show up as "the editor is slow", and an improvement
        // to the server would be invisible underneath the variance. It also made this test
        // disagree with LargeSolutionBenchmarks, which has always restored first.
        //
        // The restore is still worth timing — it is real latency a user on a fresh clone pays —
        // which is why it is measured and printed rather than hidden. It is simply not attributed
        // to the server.
        double restoreMs = await RestoreAsync(solution.SolutionPath);
        _output.WriteLine(
            $"[0] dotnet restore (environment cost, excluded from the stage budgets below): " +
            $"{restoreMs:F0} ms");

        string exePath = typeof(LspProxy).Assembly.Location;

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = solution.Directory,
        };
        psi.ArgumentList.Add(exePath);
        psi.ArgumentList.Add("--lsp");
        psi.ArgumentList.Add("--solution");
        psi.ArgumentList.Add(solution.SolutionPath);
        psi.Environment["ROSLYNMCP_SHARED_HOST"] = "0";

        Process? process = null;
        try
        {
            process = Process.Start(psi)!;
            _ = DrainServerLog(process, _output);

            using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                process.StandardInput.BaseStream, process.StandardOutput.BaseStream,
                new SystemTextJsonFormatter()));
            rpc.StartListening();

            await rpc.InvokeWithParameterObjectAsync<InitializeResult>("initialize", new
            {
                processId = Environment.ProcessId,
                rootUri = new Uri(solution.Directory).AbsoluteUri,
                initializationOptions = new { roslynSense = new { languages = new { proto = true } } },
            }).WaitAsync(TimeSpan.FromSeconds(30));
            await rpc.NotifyAsync("initialized");

            string solutionNodeId = $"solution:{solution.SolutionPath}";

            // ---- 1: cold, before anything is loaded ------------------------------------------
            // The root has to come from parsing the .sln alone: a solution this size evaluated up
            // front is the "empty Explorer that never finishes" bug reported directly.
            var (rootsElapsed, roots) = await TimedTreeAsync(rpc, nodeId: null);
            Assert.True(roots.Length == 1,
                $"solutionTree roots returned {roots.Length} node(s), not exactly one. The root " +
                "must be the solution itself, computed from the .sln parse alone — a flat list of " +
                "every project is what an Explorer that fell back to eager enumeration showed " +
                "instead.");
            Assert.Equal(SolutionNodeKind.Solution, roots[0].Kind);
            Assert.True(rootsElapsed < TreeBudget,
                $"Cold solutionTree roots took {rootsElapsed.TotalMilliseconds:F0} ms, over the " +
                $"{TreeBudget.TotalSeconds:F0}s budget: the root must not evaluate a single " +
                "project to draw itself.");
            _output.WriteLine($"[1] cold roots: {rootsElapsed.TotalMilliseconds:F0} ms");

            // ---- 2: expanding the solution -----------------------------------------------------
            var (solutionElapsed, solutionChildren) = await TimedTreeAsync(rpc, solutionNodeId);
            Assert.Contains(solutionChildren, n =>
                n.Kind is SolutionNodeKind.Project or SolutionNodeKind.RunnableProject
                    or SolutionNodeKind.SolutionFolder);
            Assert.True(solutionElapsed < TreeBudget,
                $"Expanding the solution node took {solutionElapsed.TotalMilliseconds:F0} ms, over " +
                $"the {TreeBudget.TotalSeconds:F0}s budget, for {FixtureProjectCount} projects.");
            _output.WriteLine($"[2] expand solution: {solutionElapsed.TotalMilliseconds:F0} ms");

            // ---- 3: expanding a project, still cold — the whole point of the regression --------
            // A known project path from the fixture rather than one discovered by walking the tree
            // (which might nest under a solution folder): the id convention is fixed regardless,
            // and what is under test is the project node's own latency, not tree discovery.
            string plainProjectPath = solution.ProjectPaths.FirstOrDefault(p =>
                !string.Equals(p, solution.ContractsProjectPath, StringComparison.OrdinalIgnoreCase))
                ?? solution.ProjectPaths[0];

            var (projectElapsed, projectChildren) = await TimedTreeAsync(rpc, $"project:{plainProjectPath}");
            Assert.Contains(projectChildren, n =>
                n.Kind is SolutionNodeKind.Dependencies or SolutionNodeKind.DependenciesNetFx);
            Assert.True(projectElapsed < TreeBudget,
                $"Expanding a project node took {projectElapsed.TotalMilliseconds:F0} ms, over the " +
                $"{TreeBudget.TotalSeconds:F0}s budget. This has to hold before the workspace has " +
                "been loaded at all — the tree reads MSBuild's evaluated item model directly, never " +
                "a Roslyn Solution — which is what used to spin forever expanding a project on the " +
                "87-project solution.");
            _output.WriteLine($"[3] expand project (cold): {projectElapsed.TotalMilliseconds:F0} ms");

            // ---- 4: resolving every lens on widgets.proto must not load the solution -----------
            var lensWatch = Stopwatch.StartNew();
            string protoUri = LspConverters.PathToUri(solution.WidgetsProtoPath);
            var lenses = await rpc.InvokeWithParameterObjectAsync<CodeLens[]>(
                "textDocument/codeLens", new CodeLensParams(new TextDocumentIdentifier(protoUri)))
                .WaitAsync(TimeSpan.FromMinutes(3)); // first request opens the contracts project

            Assert.NotEmpty(lenses);
            foreach (var lens in lenses)
            {
                await rpc.InvokeWithParameterObjectAsync<CodeLens>("codeLens/resolve", lens)
                    .WaitAsync(TimeSpan.FromSeconds(30));
            }
            lensWatch.Stop();

            var countersAfterLenses = await rpc.InvokeAsync<DiagnosticsCounters>(
                "roslynSense/diagnosticsCounters").WaitAsync(TimeSpan.FromSeconds(10));

            // Zero, not merely small: resolving a lens is not a gesture the user chose to wait on —
            // it fires on every scroll — and the fix is that it searches only what is already in
            // the workspace (the one contracts project opened to answer the lens at all) instead of
            // starting the FixtureConsumerProjectCount-project sweep that used to fire from it.
            Assert.True(countersAfterLenses.IncrementalLoadCount == 0,
                $"IncrementalLoadCount was {countersAfterLenses.IncrementalLoadCount} after " +
                $"resolving all {lenses.Length} lenses on widgets.proto; a code lens resolve must " +
                "never pull another project into the workspace, which is exactly the mechanism a " +
                "scroll used to load an entire solution through.");
            _output.WriteLine(
                $"[4] codeLens list+resolve x{lenses.Length}: {lensWatch.ElapsedMilliseconds} ms, " +
                $"IncrementalLoadCount={countersAfterLenses.IncrementalLoadCount}");

            // ---- 5: the tree keeps answering while a real edit loads a project in the background
            string consumerFile = solution.ConsumerFiles[0];
            await rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new DidOpenTextDocumentParams(
                new TextDocumentItem(
                    LspConverters.PathToUri(consumerFile), "csharp", 1,
                    await File.ReadAllTextAsync(consumerFile))));

            var (afterOpenElapsed, _) = await TimedTreeAsync(rpc, solutionNodeId);
            Assert.True(afterOpenElapsed < TreeBudget,
                $"solutionTree took {afterOpenElapsed.TotalMilliseconds:F0} ms right after " +
                $"didOpen, over the {TreeBudget.TotalSeconds:F0}s budget: opening a file starts " +
                "loading its project in the background for diagnostics, and the tree must not " +
                "block on that load finishing.");
            _output.WriteLine($"[5] solutionTree right after didOpen: {afterOpenElapsed.TotalMilliseconds:F0} ms");

            // ---- 6: the deliberate gesture still finds the cross-project usage ------------------
            string protoText = await File.ReadAllTextAsync(solution.WidgetsProtoPath);
            int nameOffset = protoText.IndexOf("rpc GetWidgetsById", StringComparison.Ordinal);
            Assert.True(nameOffset >= 0, "'rpc GetWidgetsById' is not in widgets.proto");
            nameOffset += "rpc ".Length;
            var linePosition = SourceText.From(protoText).Lines.GetLinePosition(nameOffset);
            var position = new Position(linePosition.Line, linePosition.Character);

            var consumerFileNames = solution.ConsumerFiles
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Production waits as long as it takes for every consumer to load before answering —
            // ProtoReferenceService.ExplicitSearchBudget is Timeout.InfiniteTimeSpan for exactly
            // this gesture — so one call is complete rather than a first, partial answer. The
            // ceiling below only guards against a genuine hang.
            var referencesWatch = Stopwatch.StartNew();
            var referenceLocations = await rpc.InvokeWithParameterObjectAsync<Location[]>(
                "textDocument/references",
                new ReferenceParams(
                    new TextDocumentIdentifier(protoUri), position, new ReferenceContext(IncludeDeclaration: true)))
                .WaitAsync(TimeSpan.FromMinutes(5));
            referencesWatch.Stop();

            Assert.True(
                referenceLocations.Any(l =>
                    consumerFileNames.Contains(Path.GetFileName(LspConverters.UriToPath(l.Uri)))),
                "textDocument/references on 'rpc GetWidgetsById' found no location in any consumer " +
                "file: the deliberate find-usages gesture must still reach across projects even " +
                "though the incidental paths (hover, code lens) it shares code with no longer " +
                "preload them.");
            _output.WriteLine(
                $"[6] textDocument/references (waits for full consumer load): " +
                $"{referencesWatch.ElapsedMilliseconds} ms, {referenceLocations.Length} location(s)");

            // Read last, so every request above has had its chance to push it higher. Peak rather
            // than current: what matters for an editor sharing a machine with a build and a browser
            // is the high-water mark a solution load reaches, not what is left after the GC has had
            // a quiet moment.
            process.Refresh();
            _output.WriteLine(
                $"[7] peak working set: {process.PeakWorkingSet64 / 1024.0 / 1024.0:F0} MB " +
                $"for {FixtureProjectCount + FixtureConsumerProjectCount + 1} projects " +
                "(9 of them loaded into the workspace)");

            await rpc.InvokeAsync<object?>("shutdown");
            await rpc.NotifyAsync("exit");
        }
        finally
        {
            if (process is not null)
            {
                if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
                    process.Kill(entireProcessTree: true);
                process.Dispose();
            }

            solution.Dispose();
        }
    }

    /// <summary>
    /// Restores the generated solution, so that nothing the server is timed on is waiting on NuGet.
    /// One call for the whole solution, which is what writes <c>project.assets.json</c> for every
    /// project it lists.
    /// </summary>
    private static async Task<double> RestoreAsync(string solutionPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("restore");
        psi.ArgumentList.Add(solutionPath);

        // The same environment the server gives its own restores, and for the same reason.
        // MSBuild's node reuse keeps worker processes alive between invocations and reconnects to
        // them by named pipe; when one of those is dead or wedged — which a test run that was
        // cancelled, or a machine that has been building all day, reliably produces — the connect
        // waits out MSBUILDNODECONNECTIONTIMEOUT before falling back. That default is 900 s, and it
        // turned a two-second restore in this fixture into a fifteen-minute one, three runs in a
        // row, with nothing in the output to say why.
        BuildProcessHelper.ConfigureMsBuildEnvironment(psi);

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        watch.Stop();

        // Both streams and the exit code, because a silent restore failure would surface much later
        // as a wall of unresolved-reference errors from the server and look like a server defect.
        Assert.True(process.ExitCode == 0,
            $"dotnet restore failed (exit {process.ExitCode}) for {solutionPath}.\n" +
            $"STDOUT:\n{await stdout}\nSTDERR:\n{await stderr}");

        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Reads the server's stderr line by line, stamping each with the milliseconds since the
    /// process started, and echoes it into the test output.
    /// </summary>
    /// <remarks>
    /// The previous <c>ReadToEndAsync</c> drain kept the pipe from filling and threw everything
    /// away, which is why a 33-second run could only ever be reported as six stage totals with no
    /// account of where the seconds went. Stamped lines turn the server's own load log — restore,
    /// project open, post-open pipeline — into the breakdown, at the cost of nothing this test
    /// measures: it is a background read on a pipe the server writes to regardless.
    /// </remarks>
    private static Task DrainServerLog(Process process, ITestOutputHelper output)
    {
        var watch = Stopwatch.StartNew();
        return Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
            {
                // The test may already have finished when a late line arrives; xUnit throws on
                // writing to output after that, and a drain thread is not worth failing a run for.
                try { output.WriteLine($"    server +{watch.ElapsedMilliseconds,6} ms | {line}"); }
                catch { return; }
            }
        });
    }

    private static async Task<(TimeSpan Elapsed, SolutionTreeNode[] Nodes)> TimedTreeAsync(
        JsonRpc rpc, string? nodeId)
    {
        var watch = Stopwatch.StartNew();

        // The ceiling here only guards against a genuine hang so the suite fails with a clear
        // message instead of never finishing; the 5-second responsiveness budget is asserted
        // separately, against the measured elapsed time.
        var nodes = await rpc.InvokeWithParameterObjectAsync<SolutionTreeNode[]>(
            "roslynSense/solutionTree", new SolutionTreeParams(NodeId: nodeId))
            .WaitAsync(TimeSpan.FromSeconds(60));

        return (watch.Elapsed, nodes);
    }
}
