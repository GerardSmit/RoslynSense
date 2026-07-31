using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using RoslynMCP.Services;
using RoslynMCP.Services.Debugging;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Edit-and-Continue against a live .NET Framework process, end to end.
/// </summary>
/// <remarks>
/// <para>
/// Everything else about hot reload is checked in isolation — the launch environment, the module
/// identity, the agent's wire protocol. The one link none of that covers is the important one:
/// whether the desktop CLR actually accepts a delta that Roslyn emitted. That is a claim about two
/// pieces of software written a decade apart, and the only way to know is to watch a running
/// process change its answer.
/// </para>
/// <para>
/// The target is compiled by Roslyn rather than by <c>csc.exe</c> so the baseline is the very
/// <see cref="Compilation"/> the delta is computed from; a baseline recovered from a foreign build
/// would make a failure ambiguous between "the runtime refused it" and "the metadata did not line
/// up". It is <em>launched</em> rather than attached to because the EnC JIT flags are set as each
/// module loads — attaching to an already-running process is too late for its own main module.
/// </para>
/// <para>
/// <strong>What running this established:</strong> the desktop CLR does not validate the delta.
/// The first run faulted inside <c>ICorDebugModule2::ApplyChanges</c> with an access violation and
/// killed the test host outright — no HRESULT, no managed exception. That is why
/// <c>InProcessDebugEngine.ApplyDeltaAsync</c> now refuses, and why this test is opt-in: a crashing
/// test does not fail, it aborts the entire run.
/// </para>
/// <para>
/// Set <c>ROSLYNSENSE_TEST_FX_HOTRELOAD=1</c> to run it. Doing so can still take the host down —
/// that is the point of keeping it.
/// </para>
/// </remarks>
[Collection(DebuggerCollection.Name)]
public class FrameworkHotReloadTests
{
    private const string TypeName = "FxHotReload.Program";

    /// <summary>Writes what <c>Compute</c> returns to a file, forever, so the change is observable
    /// from outside without reading the debuggee's console.</summary>
    private const string BaselineSource = """
        using System;
        using System.IO;

        namespace FxHotReload
        {
            public static class Program
            {
                public static int Compute(int input)
                {
                    return input * 2;
                }

                public static void Main(string[] args)
                {
                    for (int i = 0; i < 100000; i++)
                    {
                        File.AppendAllText(args[0], Compute(3) + Environment.NewLine);
                        System.Threading.Thread.Sleep(50);
                    }
                }
            }
        }
        """;

    private static readonly string EditedSource = BaselineSource.Replace("input * 2", "input * 10");

    [FrameworkHotReloadFact]
    public async Task ARoslynDeltaIsAcceptedByTheDesktopClrAndChangesALiveProcess()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"fx-hotreload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string exe = Path.Combine(directory, "FxHotReload.exe");
        string log = Path.Combine(directory, "values.txt");

        var backend = new IcorDebugBackend();
        try
        {
            var baselineCompilation = Compile(BaselineSource, "FxHotReload");
            var emit = baselineCompilation.Emit(exe, Path.ChangeExtension(exe, ".pdb"));
            Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));

            string launched = await backend.LaunchAsync(exe, [log], null, directory);
            Assert.DoesNotContain("Error:", launched);

            // Launch suspends the target; nothing runs until it is resumed. The resume is not
            // awaited because there is no breakpoint to stop at — it would sit until its timeout.
            _ = backend.ContinueAsync();

            Assert.True(await WaitForValueAsync(log, "6"),
                "The target never produced its baseline value; the delta test would prove nothing.");

            // --- the edit ---

            var editedCompilation = baselineCompilation.ReplaceSyntaxTree(
                baselineCompilation.SyntaxTrees.Single(),
                Parse(EditedSource));

            var baseline = EmitBaseline.CreateInitialBaseline(
                baselineCompilation,
                ModuleMetadata.CreateFromFile(exe),
                _ => default,
                _ => default,
                true);

            var edits = ImmutableArray.Create(new SemanticEdit(
                SemanticEditKind.Update,
                MethodSymbol(baselineCompilation),
                MethodSymbol(editedCompilation),
                syntaxMap: null,
                preserveLocalVariables: false));

            using var metadata = new MemoryStream();
            using var il = new MemoryStream();
            using var pdb = new MemoryStream();

            var difference = editedCompilation.EmitDifference(
                baseline, edits, _ => false, metadata, il, pdb, CancellationToken.None);
            Assert.True(difference.Success, string.Join("\n", difference.Diagnostics));

            // --- the apply, through the same path HotReloadService uses ---

            var (ok, error) = await backend.ApplyDeltaAsync(
                "FxHotReload", metadata.ToArray(), il.ToArray());

            Assert.True(ok, $"ICorDebugModule2::ApplyChanges refused the delta: {error}");

            Assert.True(await WaitForValueAsync(log, "30"),
                "The delta was accepted but the process kept returning the old value.");
        }
        finally
        {
            backend.Stop();
            backend.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    /// <summary>The encoding is not decoration: emitting debug information for a tree without one
    /// is an error, and no PDB means no EnC.</summary>
    private static SyntaxTree Parse(string source) => CSharpSyntaxTree.ParseText(
        Microsoft.CodeAnalysis.Text.SourceText.From(source, System.Text.Encoding.UTF8),
        path: "Program.cs");

    private static IMethodSymbol MethodSymbol(Compilation compilation) =>
        (IMethodSymbol)compilation.GetTypeByMetadataName(TypeName)!.GetMembers("Compute").Single();

    /// <summary>
    /// Builds a .NET Framework executable in this process.
    /// </summary>
    /// <remarks>
    /// Referencing the framework's own <c>mscorlib</c> rather than a reference assembly keeps the
    /// dependency to what is installed with the runtime being debugged.
    /// </remarks>
    private static CSharpCompilation Compile(string source, string assemblyName)
    {
        string frameworkDirectory = FrameworkDirectory()
            ?? throw new InvalidOperationException("No .NET Framework installation was found.");

        var references = new[] { "mscorlib.dll", "System.dll", "System.Core.dll" }
            .Select(name => MetadataReference.CreateFromFile(Path.Combine(frameworkDirectory, name)))
            .ToArray();

        return CSharpCompilation.Create(
            assemblyName,
            [Parse(source)],
            references,
            new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                optimizationLevel: OptimizationLevel.Debug,
                deterministic: true));
    }

    internal static string? FrameworkDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        // Framework64 first: this host is 64-bit, and ICorDebug cannot cross that boundary without
        // the matching worker.
        return new[] { "Framework64", "Framework" }
            .Select(flavour => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", flavour, "v4.0.30319"))
            .FirstOrDefault(directory => File.Exists(Path.Combine(directory, "mscorlib.dll")));
    }

    /// <summary>Waits for the target to write a given value, which is the only honest signal that
    /// the running code changed.</summary>
    private static async Task<bool> WaitForValueAsync(string log, string value)
    {
        for (int attempt = 0; attempt < 150; attempt++)
        {
            try
            {
                if (File.Exists(log))
                {
                    var lines = File.ReadLines(log).ToList();
                    // The last line, not any line: after the apply the file still holds the old
                    // values, so "contains" would pass before anything changed.
                    if (lines.Count > 0 && lines[^1].Trim() == value)
                        return true;
                }
            }
            catch (IOException)
            {
                // The target appends while this reads.
            }

            await Task.Delay(100);
        }

        return false;
    }
}
