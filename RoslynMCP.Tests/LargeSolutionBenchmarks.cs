using System.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

namespace RoslynMCP.Tests;

/// <summary>
/// Opt-in gate for anything built on <see cref="LargeSolutionFixture"/>, in the same shape as
/// <see cref="FrameworkHotReloadFactAttribute"/>.
/// </summary>
/// <remarks>
/// A test that generates a multi-project solution, restores it and drives a real out-of-process
/// server has no business running inside an ordinary <c>dotnet test</c> — it is minutes of NuGet
/// and MSBuild for a question no unit-test loop is asking — but it still has to live in the suite
/// so it keeps compiling and can be run deliberately. Both <see cref="LargeSolutionBenchmarks"/>
/// and <see cref="LargeSolutionStressTests"/> sit behind this, so neither one costs a developer or
/// a per-minute build agent anything until somebody asks for it.
/// </remarks>
public sealed class RoslynSenseBenchFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "ROSLYNSENSE_BENCH";

    public RoslynSenseBenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
            Skip = $"Set {EnvironmentVariable}=1 to run; this generates and restores whole " +
                "solutions and drives the real --lsp server against them, which takes minutes.";
    }
}

/// <summary>
/// Measures what RoslynSense actually costs against <see cref="LargeSolutionFixture"/> at three
/// project counts, and prints the numbers rather than asserting a budget against them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LargeSolutionStressTests"/> answers "does it stay responsive"; this answers "how
/// much does each thing cost, and does that cost grow linearly with the solution". The two are
/// deliberately separate — a budget assertion tells you pass/fail, not where the time goes, and a
/// benchmark that also has to keep a hard-coded budget from going stale is a worse benchmark.
/// </para>
/// <para>
/// One test, all three sizes, sequentially — the same reasoning <see cref="LargeSolutionStressTests"/>
/// gives for staying a single long test: splitting by size would still pay a fresh restore and a
/// fresh population of server processes per size, and gains nothing since the sizes are never run
/// in parallel (each spawns several <c>dotnet</c> processes of its own and they would only
/// contend with each other on the same machine).
/// </para>
/// <para>
/// Every "cold" number — <c>initialize</c> and the three solution-tree calls, before any
/// workspace load — comes from its own freshly spawned, freshly connected server process rather
/// than from the one long-lived session the rest of the measurements share. That is not
/// incidental: a tree call made second in the same session is not cold, it is warm with different
/// words, and the whole point of reporting cold and warm separately is to keep that distinction
/// real. <see cref="ColdHandshakeRepeatCount"/> processes are spawned per size for exactly this,
/// which is also what makes a min/median honest — three independent process starts, not the same
/// process asked three times.
/// </para>
/// </remarks>
public class LargeSolutionBenchmarks
{
    private readonly ITestOutputHelper _output;

    public LargeSolutionBenchmarks(ITestOutputHelper output) => _output = output;

    /// <summary>How many independent server processes back the cold <c>initialize</c> and
    /// solution-tree numbers. Three rather than one: a single sample cannot distinguish "this
    /// machine had a slow moment" from "this is what cold costs", and rather than one process
    /// asked three times (which would not be cold the second and third time) three processes are
    /// spawned, each asked once.</summary>
    private const int ColdHandshakeRepeatCount = 3;

    /// <summary>How many times each warm, side-effect-free call is repeated inside the one
    /// long-lived session once everything of interest has loaded.</summary>
    private const int WarmRepeatCount = 5;

    /// <summary>ProjectCount paired with a proportionally scaled ConsumerProjectCount, so the
    /// gRPC-consumer population grows with the rest of the solution instead of staying fixed
    /// while everything around it triples.</summary>
    private static readonly (int ProjectCount, int ConsumerProjectCount)[] s_sizes =
    [
        (10, 4),
        (30, 10),
        (60, 20),
    ];

    [RoslynSenseBenchFact]
    public async Task MeasuresRealCostAcrossSolutionSizes()
    {
        string exePath = typeof(LspProxy).Assembly.Location;
        var results = new List<SizeMetrics>();

        foreach (var (projectCount, consumerProjectCount) in s_sizes)
        {
            _output.WriteLine(
                $"==== ProjectCount={projectCount} ConsumerProjectCount={consumerProjectCount} ====");
            var metrics = await MeasureSizeAsync(exePath, projectCount, consumerProjectCount);
            results.Add(metrics);
        }

        LogMarkdownTable(results);
    }

    // ---- One size, start to finish -------------------------------------------------------------

    private async Task<SizeMetrics> MeasureSizeAsync(string exePath, int projectCount, int consumerProjectCount)
    {
        using var solution = LargeSolutionFixture.Create(
            new LargeSolutionOptions(ProjectCount: projectCount, ConsumerProjectCount: consumerProjectCount));

        double restoreMs = await RunRestoreAsync(solution.SolutionPath);
        _output.WriteLine($"restore: {restoreMs:F0} ms ({solution.ProjectPaths.Count} projects)");

        // Not the Contracts project: expanding it is a different (and cheaper) shape of node
        // than an ordinary project, and the point here is the common case.
        string plainProjectPath = solution.ProjectPaths.FirstOrDefault(p =>
            !string.Equals(p, solution.ContractsProjectPath, StringComparison.OrdinalIgnoreCase))
            ?? solution.ProjectPaths[0];

        var initSamples = new List<double>();
        var rootsSamples = new List<double>();
        var solutionExpandSamples = new List<double>();
        var projectExpandSamples = new List<double>();

        for (int i = 0; i < ColdHandshakeRepeatCount; i++)
        {
            var sample = await RunColdHandshakeSampleAsync(exePath, solution, plainProjectPath);
            initSamples.Add(sample.InitMs);
            rootsSamples.Add(sample.RootsMs);
            solutionExpandSamples.Add(sample.SolutionExpandMs);
            projectExpandSamples.Add(sample.ProjectExpandMs);
            _output.WriteLine(
                $"  cold sample {i}: init={sample.InitMs:F0}ms roots={sample.RootsMs:F0}ms " +
                $"expandSolution={sample.SolutionExpandMs:F0}ms expandProject={sample.ProjectExpandMs:F0}ms");
        }

        var workflow = await RunFullWorkflowAsync(exePath, solution, plainProjectPath);

        var (initMin, initMedian) = MinMedian(initSamples);
        var (rootsMin, rootsMedian) = MinMedian(rootsSamples);
        var (solExpMin, solExpMedian) = MinMedian(solutionExpandSamples);
        var (projExpMin, projExpMedian) = MinMedian(projectExpandSamples);
        var (warmRootsMin, warmRootsMedian) = MinMedian(workflow.WarmRootsMs);
        var (warmSolExpMin, warmSolExpMedian) = MinMedian(workflow.WarmSolutionExpandMs);
        var (warmProjExpMin, warmProjExpMedian) = MinMedian(workflow.WarmProjectExpandMs);
        var (warmDocSymMin, warmDocSymMedian) = MinMedian(workflow.WarmDocumentSymbolMs);
        var (warmLensMin, warmLensMedian) = MinMedian(workflow.WarmCodeLensMs);

        var metrics = new SizeMetrics
        {
            ProjectCount = projectCount,
            ConsumerProjectCount = consumerProjectCount,
            RestoreMs = restoreMs,
            InitMinMs = initMin,
            InitMedianMs = initMedian,
            RootsMinMs = rootsMin,
            RootsMedianMs = rootsMedian,
            SolutionExpandMinMs = solExpMin,
            SolutionExpandMedianMs = solExpMedian,
            ProjectExpandMinMs = projExpMin,
            ProjectExpandMedianMs = projExpMedian,
            FirstLoadMs = workflow.FirstLoadMs,
            IncrementalLoadCountAfterFirstLoad = workflow.LoadCountAfterFirstLoad,
            CodeLensMs = workflow.CodeLensMs,
            CodeLensCount = workflow.LensCount,
            IncrementalLoadDeltaAfterCodeLens = workflow.LoadDeltaAfterCodeLens,
            ReferencesMs = workflow.ReferencesMs,
            ReferencesCount = workflow.ReferencesCount,
            IncrementalLoadDeltaAfterReferences = workflow.LoadDeltaAfterReferences,
            HoverMs = workflow.HoverMs,
            DefinitionMs = workflow.DefinitionMs,
            WarmRootsMinMs = warmRootsMin,
            WarmRootsMedianMs = warmRootsMedian,
            WarmSolutionExpandMinMs = warmSolExpMin,
            WarmSolutionExpandMedianMs = warmSolExpMedian,
            WarmProjectExpandMinMs = warmProjExpMin,
            WarmProjectExpandMedianMs = warmProjExpMedian,
            WarmDocumentSymbolMinMs = warmDocSymMin,
            WarmDocumentSymbolMedianMs = warmDocSymMedian,
            WarmCodeLensMinMs = warmLensMin,
            WarmCodeLensMedianMs = warmLensMedian,
            PeakWorkingSetBytes = workflow.PeakWorkingSetBytes,
        };

        _output.WriteLine(
            $"  firstLoad={metrics.FirstLoadMs:F0}ms loadCount={metrics.IncrementalLoadCountAfterFirstLoad} " +
            $"codeLens={metrics.CodeLensMs:F0}ms(delta={metrics.IncrementalLoadDeltaAfterCodeLens}) " +
            $"references={metrics.ReferencesMs:F0}ms(count={metrics.ReferencesCount}," +
            $"delta={metrics.IncrementalLoadDeltaAfterReferences}) hover={metrics.HoverMs:F0}ms " +
            $"definition={metrics.DefinitionMs:F0}ms peakWs={metrics.PeakWorkingSetBytes / 1024.0 / 1024.0:F0}MB");

        return metrics;
    }

    // ---- Phase A: repeated cold-process samples for initialize + cold solution tree -------------

    private sealed record ColdSample(double InitMs, double RootsMs, double SolutionExpandMs, double ProjectExpandMs);

    /// <summary>
    /// One fresh server, asked exactly the four things that have to happen before any workspace
    /// load to still mean "cold": <c>initialize</c>, the tree roots, expanding the solution node,
    /// expanding one project node. Nothing here ever opens a file, so nothing here ever loads a
    /// Roslyn project — nothing in this method could push the numbers into "warm" territory.
    /// </summary>
    private static async Task<ColdSample> RunColdHandshakeSampleAsync(
        string exePath, LargeSolution solution, string plainProjectPath)
    {
        var process = StartServer(exePath, solution);
        try
        {
            using var rpc = Connect(process);
            double initMs = await TimedInitializeAsync(rpc, solution);

            var (rootsMs, roots) = await TimedTreeAsync(rpc, nodeId: null);
            Assert.NotEmpty(roots);

            string solutionNodeId = $"solution:{solution.SolutionPath}";
            var (solutionExpandMs, _) = await TimedTreeAsync(rpc, solutionNodeId);

            var (projectExpandMs, _) = await TimedTreeAsync(rpc, $"project:{plainProjectPath}");

            await ShutdownAsync(rpc, process);
            return new ColdSample(initMs, rootsMs, solutionExpandMs, projectExpandMs);
        }
        finally
        {
            KillIfStillRunning(process);
        }
    }

    // ---- Phase B: one long-lived session for everything that has to happen in sequence ----------

    private sealed record FullWorkflowResult(
        double FirstLoadMs,
        int LoadCountAfterFirstLoad,
        double CodeLensMs,
        int LensCount,
        int LoadDeltaAfterCodeLens,
        double ReferencesMs,
        int ReferencesCount,
        int LoadDeltaAfterReferences,
        double HoverMs,
        double DefinitionMs,
        List<double> WarmRootsMs,
        List<double> WarmSolutionExpandMs,
        List<double> WarmProjectExpandMs,
        List<double> WarmDocumentSymbolMs,
        List<double> WarmCodeLensMs,
        long PeakWorkingSetBytes);

    /// <summary>
    /// The rest of the measurements, all inside one session, in the order the workspace actually
    /// grows into: the first real load (a consumer file's <c>didOpen</c> + <c>documentSymbol</c>),
    /// an incidental gesture that must not grow it further (<c>codeLens</c> + resolve on the
    /// <c>.proto</c>), the one deliberate gesture that is allowed to grow it
    /// (<c>textDocument/references</c> on the RPC name), a hover and a definition on the
    /// generated-client call site, and finally warm repeats of the tree and the two side-effect-free
    /// calls now that the workspace is as loaded as this session ever gets it.
    /// </summary>
    private async Task<FullWorkflowResult> RunFullWorkflowAsync(
        string exePath, LargeSolution solution, string plainProjectPath)
    {
        var process = StartServer(exePath, solution);
        try
        {
            using var rpc = Connect(process);
            await TimedInitializeAsync(rpc, solution); // timing already covered by the cold samples

            string solutionNodeId = $"solution:{solution.SolutionPath}";
            string projectNodeId = $"project:{plainProjectPath}";

            // ---- 3 & 4: the first real workspace load ------------------------------------------
            string consumerFile = solution.ConsumerFiles[0];
            string consumerUri = LspConverters.PathToUri(consumerFile);
            string consumerText = await File.ReadAllTextAsync(consumerFile);

            int loadCountBeforeFirstLoad = await ReadIncrementalLoadCountAsync(rpc);

            var firstLoadSw = Stopwatch.StartNew();
            await rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new DidOpenTextDocumentParams(
                new TextDocumentItem(consumerUri, "csharp", 1, consumerText)));
            var firstSymbols = await rpc.InvokeWithParameterObjectAsync<DocumentSymbol[]>(
                "textDocument/documentSymbol", new DocumentSymbolParams(new TextDocumentIdentifier(consumerUri)))
                .WaitAsync(TimeSpan.FromMinutes(3)); // this call is the one that loads the workspace
            firstLoadSw.Stop();
            Assert.NotEmpty(firstSymbols);

            int loadCountAfterFirstLoad = await ReadIncrementalLoadCountAsync(rpc);

            // ---- 5: codeLens + resolve every lens on widgets.proto, cold ------------------------
            string protoUri = LspConverters.PathToUri(solution.WidgetsProtoPath);
            var (codeLensMs, lensCount, loadDeltaAfterCodeLens) =
                await TimedCodeLensBatchAsync(rpc, protoUri, loadCountAfterFirstLoad);
            Assert.True(lensCount > 0, "textDocument/codeLens returned no lenses for widgets.proto.");

            // ---- 6: references, the one gesture that is allowed to grow the workspace ----------
            int loadCountBeforeReferences = loadCountAfterFirstLoad + loadDeltaAfterCodeLens;
            var (referencesMs, referencesCount, loadDeltaAfterReferences) =
                await TimedReferencesAsync(rpc, solution, protoUri, loadCountBeforeReferences);

            // ---- 7: hover + definition on a generated-client call site -------------------------
            int callIndex = consumerText.IndexOf("GetWidgetsByIdAsync", StringComparison.Ordinal);
            Assert.True(callIndex >= 0, "GetWidgetsByIdAsync is not in the consumer caller file.");
            var callPosition = OffsetToPosition(consumerText, callIndex);
            var callParams = new TextDocumentPositionParams(new TextDocumentIdentifier(consumerUri), callPosition);

            var hoverSw = Stopwatch.StartNew();
            await rpc.InvokeWithParameterObjectAsync<Hover?>("textDocument/hover", callParams)
                .WaitAsync(TimeSpan.FromSeconds(30));
            hoverSw.Stop();

            var definitionSw = Stopwatch.StartNew();
            await rpc.InvokeWithParameterObjectAsync<Location[]>("textDocument/definition", callParams)
                .WaitAsync(TimeSpan.FromSeconds(30));
            definitionSw.Stop();

            // ---- 8: warm repeats of 2, 3 and 5, now that everything above has loaded -----------
            var warmRoots = new List<double>();
            var warmSolutionExpand = new List<double>();
            var warmProjectExpand = new List<double>();
            for (int i = 0; i < WarmRepeatCount; i++)
            {
                warmRoots.Add((await TimedTreeAsync(rpc, nodeId: null)).Ms);
                warmSolutionExpand.Add((await TimedTreeAsync(rpc, solutionNodeId)).Ms);
                warmProjectExpand.Add((await TimedTreeAsync(rpc, projectNodeId)).Ms);
            }

            var warmDocumentSymbol = new List<double>();
            for (int i = 0; i < WarmRepeatCount; i++)
            {
                var sw = Stopwatch.StartNew();
                await rpc.InvokeWithParameterObjectAsync<DocumentSymbol[]>(
                    "textDocument/documentSymbol", new DocumentSymbolParams(new TextDocumentIdentifier(consumerUri)))
                    .WaitAsync(TimeSpan.FromSeconds(30));
                sw.Stop();
                warmDocumentSymbol.Add(sw.Elapsed.TotalMilliseconds);
            }

            var warmCodeLens = new List<double>();
            int loadCountBeforeWarmLens = loadCountBeforeReferences + loadDeltaAfterReferences;
            for (int i = 0; i < WarmRepeatCount; i++)
            {
                var (ms, _, _) = await TimedCodeLensBatchAsync(rpc, protoUri, loadCountBeforeWarmLens);
                warmCodeLens.Add(ms);
            }

            // ---- 9: peak working set, read last so every request above has had its chance to
            // push it higher ----------------------------------------------------------------------
            process.Refresh();
            long peakWorkingSet = process.PeakWorkingSet64;

            await ShutdownAsync(rpc, process);

            return new FullWorkflowResult(
                firstLoadSw.Elapsed.TotalMilliseconds, loadCountAfterFirstLoad,
                codeLensMs, lensCount, loadDeltaAfterCodeLens,
                referencesMs, referencesCount, loadDeltaAfterReferences,
                hoverSw.Elapsed.TotalMilliseconds, definitionSw.Elapsed.TotalMilliseconds,
                warmRoots, warmSolutionExpand, warmProjectExpand, warmDocumentSymbol, warmCodeLens,
                peakWorkingSet);
        }
        finally
        {
            KillIfStillRunning(process);
        }
    }

    private static async Task<(double Ms, int LensCount, int LoadDelta)> TimedCodeLensBatchAsync(
        JsonRpc rpc, string protoUri, int loadCountBefore)
    {
        var sw = Stopwatch.StartNew();
        var lenses = await rpc.InvokeWithParameterObjectAsync<CodeLens[]>(
            "textDocument/codeLens", new CodeLensParams(new TextDocumentIdentifier(protoUri)))
            .WaitAsync(TimeSpan.FromMinutes(3)); // first call opens the Contracts project

        foreach (var lens in lenses)
        {
            await rpc.InvokeWithParameterObjectAsync<CodeLens>("codeLens/resolve", lens)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        sw.Stop();

        int loadCountAfter = await ReadIncrementalLoadCountAsync(rpc);
        return (sw.Elapsed.TotalMilliseconds, lenses.Length, loadCountAfter - loadCountBefore);
    }

    private static async Task<(double Ms, int Count, int LoadDelta)> TimedReferencesAsync(
        JsonRpc rpc, LargeSolution solution, string protoUri, int loadCountBefore)
    {
        string protoText = await File.ReadAllTextAsync(solution.WidgetsProtoPath);
        int nameOffset = protoText.IndexOf("rpc GetWidgetsById", StringComparison.Ordinal);
        Assert.True(nameOffset >= 0, "'rpc GetWidgetsById' is not in widgets.proto");
        nameOffset += "rpc ".Length;
        var linePosition = SourceText.From(protoText).Lines.GetLinePosition(nameOffset);
        var position = new Position(linePosition.Line, linePosition.Character);

        // ExplicitSearchBudget on the deliberate find-usages path is Timeout.InfiniteTimeSpan as of
        // this benchmark: the call waits for every consumer project to load, sequentially, rather
        // than answering early with whatever happened to be ready. That is the cost this call
        // exists to measure, so the wrapper timeout here is a generous ceiling against a genuine
        // hang, not a budget the call is expected to approach.
        var sw = Stopwatch.StartNew();
        var locations = await rpc.InvokeWithParameterObjectAsync<Location[]>(
            "textDocument/references",
            new ReferenceParams(
                new TextDocumentIdentifier(protoUri), position, new ReferenceContext(IncludeDeclaration: true)))
            .WaitAsync(TimeSpan.FromMinutes(15));
        sw.Stop();

        int loadCountAfter = await ReadIncrementalLoadCountAsync(rpc);
        return (sw.Elapsed.TotalMilliseconds, locations.Length, loadCountAfter - loadCountBefore);
    }

    // ---- Process / RPC plumbing, copied from LspEndToEndTests's spawn-and-handshake shape --------

    private static Process StartServer(string exePath, LargeSolution solution)
    {
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

        var process = Process.Start(psi)!;
        _ = process.StandardError.ReadToEndAsync(); // drain
        return process;
    }

    private static JsonRpc Connect(Process process)
    {
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
            process.StandardInput.BaseStream, process.StandardOutput.BaseStream,
            new SystemTextJsonFormatter()));
        rpc.StartListening();
        return rpc;
    }

    private static async Task<double> TimedInitializeAsync(JsonRpc rpc, LargeSolution solution)
    {
        var sw = Stopwatch.StartNew();
        await rpc.InvokeWithParameterObjectAsync<InitializeResult>("initialize", new
        {
            processId = Environment.ProcessId,
            rootUri = new Uri(solution.Directory).AbsoluteUri,
            initializationOptions = new { roslynSense = new { languages = new { proto = true } } },
        }).WaitAsync(TimeSpan.FromSeconds(30));
        sw.Stop();
        await rpc.NotifyAsync("initialized");
        return sw.Elapsed.TotalMilliseconds;
    }

    private static async Task<(double Ms, SolutionTreeNode[] Nodes)> TimedTreeAsync(JsonRpc rpc, string? nodeId)
    {
        var sw = Stopwatch.StartNew();
        var nodes = await rpc.InvokeWithParameterObjectAsync<SolutionTreeNode[]>(
            "roslynSense/solutionTree", new SolutionTreeParams(NodeId: nodeId))
            .WaitAsync(TimeSpan.FromSeconds(60));
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, nodes);
    }

    private static async Task<int> ReadIncrementalLoadCountAsync(JsonRpc rpc) =>
        (await rpc.InvokeAsync<DiagnosticsCounters>("roslynSense/diagnosticsCounters")
            .WaitAsync(TimeSpan.FromSeconds(10))).IncrementalLoadCount;

    private static async Task ShutdownAsync(JsonRpc rpc, Process process)
    {
        try
        {
            await rpc.InvokeAsync<object?>("shutdown").WaitAsync(TimeSpan.FromSeconds(10));
            await rpc.NotifyAsync("exit");
        }
        catch
        {
            // A benchmark run that already has its numbers should not fail on a shutdown
            // hiccup; KillIfStillRunning below is what actually guarantees cleanup.
        }
    }

    private static void KillIfStillRunning(Process process)
    {
        try
        {
            if (!process.HasExited && !process.WaitForExit(TimeSpan.FromSeconds(15)))
                process.Kill(entireProcessTree: true);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static Position OffsetToPosition(string text, int offset)
    {
        int line = 0, lineStart = 0;
        for (int i = 0; i < offset; i++)
        {
            if (text[i] == '\n') { line++; lineStart = i + 1; }
        }
        return new Position(line, offset - lineStart);
    }

    private static (double Min, double Median) MinMedian(List<double> samplesMs)
    {
        var sorted = samplesMs.OrderBy(x => x).ToList();
        double min = sorted[0];
        double median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
        return (min, median);
    }

    // ---- Results ----------------------------------------------------------------------------

    private sealed class SizeMetrics
    {
        public int ProjectCount;
        public int ConsumerProjectCount;
        public double RestoreMs;

        public double InitMinMs, InitMedianMs;
        public double RootsMinMs, RootsMedianMs;
        public double SolutionExpandMinMs, SolutionExpandMedianMs;
        public double ProjectExpandMinMs, ProjectExpandMedianMs;

        public double FirstLoadMs;
        public int IncrementalLoadCountAfterFirstLoad;

        public double CodeLensMs;
        public int CodeLensCount;
        public int IncrementalLoadDeltaAfterCodeLens;

        public double ReferencesMs;
        public int ReferencesCount;
        public int IncrementalLoadDeltaAfterReferences;

        public double HoverMs;
        public double DefinitionMs;

        public double WarmRootsMinMs, WarmRootsMedianMs;
        public double WarmSolutionExpandMinMs, WarmSolutionExpandMedianMs;
        public double WarmProjectExpandMinMs, WarmProjectExpandMedianMs;
        public double WarmDocumentSymbolMinMs, WarmDocumentSymbolMedianMs;
        public double WarmCodeLensMinMs, WarmCodeLensMedianMs;

        public long PeakWorkingSetBytes;
    }

    /// <summary>Prints one Markdown table, sizes as columns, meant to be pasted straight into
    /// the report rather than re-typed by hand.</summary>
    private void LogMarkdownTable(List<SizeMetrics> results)
    {
        _output.WriteLine("");
        _output.WriteLine("---- Markdown table ----");
        _output.WriteLine(
            "| Metric | " + string.Join(" | ", results.Select(r => $"{r.ProjectCount}+{r.ConsumerProjectCount}")) +
            " |");
        _output.WriteLine("|---|" + string.Concat(results.Select(_ => "---|")));

        void Row(string label, Func<SizeMetrics, string> fmt) =>
            _output.WriteLine($"| {label} | " + string.Join(" | ", results.Select(fmt)) + " |");

        Row("restore (ms)", r => $"{r.RestoreMs:F0}");
        Row($"initialize, cold, n={ColdHandshakeRepeatCount} (min/median ms)",
            r => $"{r.InitMinMs:F0} / {r.InitMedianMs:F0}");
        Row($"tree roots, cold, n={ColdHandshakeRepeatCount} (min/median ms)",
            r => $"{r.RootsMinMs:F0} / {r.RootsMedianMs:F0}");
        Row($"expand solution, cold, n={ColdHandshakeRepeatCount} (min/median ms)",
            r => $"{r.SolutionExpandMinMs:F0} / {r.SolutionExpandMedianMs:F0}");
        Row($"expand project, cold, n={ColdHandshakeRepeatCount} (min/median ms)",
            r => $"{r.ProjectExpandMinMs:F0} / {r.ProjectExpandMedianMs:F0}");
        Row("first load: didOpen+documentSymbol (ms)", r => $"{r.FirstLoadMs:F0}");
        Row("IncrementalLoadCount after first load", r => $"{r.IncrementalLoadCountAfterFirstLoad}");
        Row("codeLens batch, cold (ms)", r => $"{r.CodeLensMs:F0}");
        Row("codeLens lens count", r => $"{r.CodeLensCount}");
        Row("IncrementalLoadCount delta after codeLens", r => $"{r.IncrementalLoadDeltaAfterCodeLens}");
        Row("references (ms)", r => $"{r.ReferencesMs:F0}");
        Row("references result count", r => $"{r.ReferencesCount}");
        Row("IncrementalLoadCount delta after references", r => $"{r.IncrementalLoadDeltaAfterReferences}");
        Row("hover (ms)", r => $"{r.HoverMs:F0}");
        Row("definition (ms)", r => $"{r.DefinitionMs:F0}");
        Row($"tree roots, warm, n={WarmRepeatCount} (min/median ms)",
            r => $"{r.WarmRootsMinMs:F0} / {r.WarmRootsMedianMs:F0}");
        Row($"expand solution, warm, n={WarmRepeatCount} (min/median ms)",
            r => $"{r.WarmSolutionExpandMinMs:F0} / {r.WarmSolutionExpandMedianMs:F0}");
        Row($"expand project, warm, n={WarmRepeatCount} (min/median ms)",
            r => $"{r.WarmProjectExpandMinMs:F0} / {r.WarmProjectExpandMedianMs:F0}");
        Row($"documentSymbol, warm, n={WarmRepeatCount} (min/median ms)",
            r => $"{r.WarmDocumentSymbolMinMs:F0} / {r.WarmDocumentSymbolMedianMs:F0}");
        Row($"codeLens batch, warm, n={WarmRepeatCount} (min/median ms)",
            r => $"{r.WarmCodeLensMinMs:F0} / {r.WarmCodeLensMedianMs:F0}");
        Row("peak working set (MB)", r => $"{r.PeakWorkingSetBytes / 1024.0 / 1024.0:F0}");
    }

    // ---- Restore, timed and reported separately from every server-side measurement ---------------

    /// <summary>
    /// Runs before any of the server is ever touched. Restore is MSBuild/NuGet cost, not
    /// RoslynSense cost — folding it into a server measurement would make every other number in
    /// this file look worse than the server actually is, for a reason that has nothing to do with
    /// the server.
    /// </summary>
    private static async Task<double> RunRestoreAsync(string solutionPath)
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

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi)!;
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        sw.Stop();

        if (process.ExitCode != 0)
        {
            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            throw new InvalidOperationException(
                $"dotnet restore failed (exit {process.ExitCode}) for {solutionPath}.\n" +
                $"STDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }

        return sw.Elapsed.TotalMilliseconds;
    }
}
