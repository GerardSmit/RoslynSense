using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Services;
using Xunit;
using Xunit.Abstractions;

namespace RoslynMCP.Tests;

/// <summary>
/// Where one proto code lens actually spends its time.
/// </summary>
/// <remarks>
/// The stage that dominates the 25-project benchmark is seventeen lens resolves on one
/// <c>.proto</c>, and "about 180 ms each" is not a number anything can be done with. This splits a
/// resolve into the parts that could each plausibly own it — reaching the view, building the symbol
/// set (which for a service or an rpc includes a solution-wide hierarchy walk), and the reference
/// sweep itself — so the next change is aimed at the part that is actually large rather than at the
/// part that looks expensive.
/// </remarks>
public class ProtoLensCostProbe(ITestOutputHelper output)
{
    [RoslynSenseBenchFact]
    public async Task WhereALensResolveSpendsItsTime()
    {
        string proto = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "ProtoSolution", "Contracts", "widgets", "widgets.proto"));

        Assert.True(File.Exists(proto), $"fixture missing: {proto}");

        // Warm everything once, so the numbers below are steady state rather than first-touch.
        var warm = await ProtoWorkspace.GetAsync(proto, CancellationToken.None);
        Assert.NotNull(warm);
        Assert.NotNull(warm!.Project);

        var totals = new Dictionary<string, long>();
        void Add(string phase, long ms) => totals[phase] = totals.GetValueOrDefault(phase) + ms;

        int lenses = 0;
        var watch = new Stopwatch();

        foreach (var declaration in warm.Parse.AllDeclarations)
        {
            if (declaration.Kind is not (ProtoDeclarationKind.Service or ProtoDeclarationKind.Rpc
                or ProtoDeclarationKind.Message or ProtoDeclarationKind.Field
                or ProtoDeclarationKind.Enum or ProtoDeclarationKind.EnumValue))
            {
                continue;
            }

            lenses++;

            watch.Restart();
            var view = await ProtoWorkspace.GetAsync(proto, CancellationToken.None);
            Add("view", watch.ElapsedMilliseconds);

            var project = view!.Project!;

            watch.Restart();
            var scope = await ProtoReferenceService.SearchScopeForTestsAsync(project, CancellationToken.None);
            Add("scope", watch.ElapsedMilliseconds);

            watch.Restart();
            var symbols = await ProtoReferenceService.SymbolSetForAsync(
                declaration, fallback: null, view.Index, project, CancellationToken.None);
            Add("symbolset", watch.ElapsedMilliseconds);

            watch.Restart();
            foreach (var symbol in symbols)
                await SymbolFinder.FindReferencesAsync(symbol, scope, CancellationToken.None);
            Add("sweep", watch.ElapsedMilliseconds);

            totals["symbols"] = totals.GetValueOrDefault("symbols") + symbols.Length;
        }

        output.WriteLine($"{lenses} lenses, {totals.GetValueOrDefault("symbols")} symbols in total");
        foreach (var phase in new[] { "view", "scope", "symbolset", "sweep" })
            output.WriteLine($"  {phase,-10}: {totals.GetValueOrDefault(phase),6} ms");
    }
}
