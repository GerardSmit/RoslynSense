using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(DebuggerCollection.Name)]
public class DebugVariableTraversalTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CapturedForeachObjectCanBeListedEvaluatedAndTraversed(bool coreClr, bool insideLambda)
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "debug-variables-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = """
            using System;
            using System.Collections.Generic;
            class Node {
                public Node Child { get; set; }
                public int Number { get; set; }
                public string Text { get; set; }
                public int Add(int amount) { return Number + amount; }
                public int Doubled { get { return Number * 2; } set { Number = value / 2; } }
            }
            class Program
            {
                static System.Threading.ManualResetEvent ready = new System.Threading.ManualResetEvent(false);
                static void Other()
                {
                    int otherLocal = 73;
                    ready.Set();
                    while (true) { System.Threading.Thread.Sleep(100); GC.KeepAlive(otherLocal); }
                }
                static void Main()
                {
                    new System.Threading.Thread(Other) { IsBackground = true }.Start();
                    ready.WaitOne();
                    Console.WriteLine("ready");
                    while (true) Run();
                }
                static void Run()
                {
                    var b = new List<Node> { new Node { Child = new Node { Number = 5 } }, new Node { Child = new Node { Number = 17, Text = "old" } } };
                    var keys = new Dictionary<string, int> { { "a.b c]", 23 }, { "quote\"key", 29 } };
                    foreach (var a in b)
                    {
                        Func<Node> capture = () => a;
                        GC.KeepAlive(capture); // BREAK
                    }
                    System.Threading.Thread.Sleep(10);
                }
            }
            """;
        if (insideLambda)
            source = source.Replace("Func<Node> capture = () => a;", "Func<Node> capture = () => {\n GC.KeepAlive(a); /* INNER */\n return a; };")
                .Replace("GC.KeepAlive(capture); // BREAK", "GC.KeepAlive(capture());")
                .Replace("/* INNER */", "// BREAK\n");
        var sourcePath = Path.Combine(directory, "Program.cs");
        var exe = Path.Combine(directory, coreClr ? "Target.dll" : "Target.exe");
        File.WriteAllText(sourcePath, source);
        var framework = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework64", "v4.0.30319");
        var references = coreClr
            ? ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            : new[] { Path.Combine(framework, "mscorlib.dll"), Path.Combine(framework, "System.dll"), Path.Combine(framework, "System.Core.dll") };
        var compilation = CSharpCompilation.Create("Target",
            [CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), path: sourcePath)],
            references.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Debug));
        using (var pe = File.Create(exe))
        using (var pdb = File.Create(Path.ChangeExtension(exe, ".pdb")))
        {
            var emit = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        }
        if (coreClr)
            File.WriteAllText(Path.Combine(directory, "Target.runtimeconfig.json"),
                System.Text.Json.JsonSerializer.Serialize(new { runtimeOptions = new {
                    tfm = $"net{Environment.Version.Major}.0",
                    framework = new { name = "Microsoft.NETCore.App", version = $"{Environment.Version.Major}.0.0" }
                } }));
        var start = new ProcessStartInfo(coreClr ? "dotnet" : exe)
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        if (coreClr) start.ArgumentList.Add(exe);
        using var target = Process.Start(start)!;
        var engine = new DebugSession(91);
        try
        {
            await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var stopped = new TaskCompletionSource();
            _ = Task.Run(async () =>
            {
                await foreach (var e in engine.Events.ReadAllAsync())
                {
                    if (e.Kind == DebugEventKind.Diagnostic && e.Message.Contains("condition", StringComparison.OrdinalIgnoreCase))
                        stopped.TrySetException(new InvalidOperationException(e.Message));
                    if (e.Kind == DebugEventKind.Breakpoint) stopped.TrySetResult();
                }
            });
            engine.Attach(target.Id,
                [new BreakpointSpec { FilePath = sourcePath, Condition = "a.Child.Add(1) == 18 && a.Child.Number > 10", Line = (uint)(source.Split('\n').ToList().FindIndex(l => l.Contains("// BREAK")) + 1) }],
                coreClr ? DebugRuntime.CoreClr : DebugRuntime.NetFramework);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(40));
            var locals = await engine.VariablesAsync(0);
            // The foreach enumerator lives in an IL slot the symbols never name; VS keeps such
            // compiler temporaries out of Locals, and so does this engine.
            Assert.DoesNotContain(locals, v => v.Type.Contains("Enumerator"));
            Assert.DoesNotContain(locals, v => System.Text.RegularExpressions.Regex.IsMatch(v.Name, @"^(local|arg)\d+$"));
            var item = Assert.Single(locals, v => v.Name == "a");
            Assert.NotEmpty(item.VariablesReference);
            var children = await engine.ExpandAsync(0, item.VariablesReference);
            var child = Assert.Single(children, v => v.Name == "Child");
            Assert.NotEmpty(child.VariablesReference);
            var members = await engine.ExpandAsync(0, child.VariablesReference);
            Assert.Equal("17", Assert.Single(members, v => v.Name == "Number").Value);
            var (ok, variable, error) = await engine.EvaluateVariableAsync(0, "a.Child");
            Assert.True(ok, error);
            Assert.NotNull(variable);
            Assert.NotEmpty(variable.VariablesReference);
            Assert.Equal("17", Assert.Single(await engine.ExpandAsync(0, variable.VariablesReference), v => v.Name == "Number").Value);
            var scalar = await engine.EvaluateVariableAsync(0, "a.Child.Number");
            Assert.True(scalar.Ok, scalar.Error);
            Assert.Equal("17", scalar.Variable!.Value);
            Assert.Empty(scalar.Variable.VariablesReference);
            async Task Value(string expression, string expected)
            {
                var result = await engine.EvaluateAsync(0, expression);
                Assert.True(result.Ok, result.Error);
                Assert.Equal(expected, result.Value);
            }
            await Value("a.Child.Number + 5 * 2", "27");
            await Value("(double)a.Child.Number / 2", "8.5");
            await Value("a.Child.Add(3)", "20");
            await Value("a.Child.Number > 10 && a.Child.Number == 17", "True");
            await Value("false && a.Child.Add(0) == 17", "False");
            if (!insideLambda)
            {
                await Value("keys[\"a.b c]\"]", "23");
                await Value("b.Where(x => x.Child.Number > 10).Count()", "1");
            }
            RoslynMCP.Debugger.StackFrame? otherFrame = null;
            foreach (var thread in await engine.ThreadsAsync())
            {
                otherFrame = (await engine.StackTraceAsync(thread.Id)).FirstOrDefault(f => f.Method.Contains("Other"));
                if (otherFrame is not null) break;
            }
            Assert.NotNull(otherFrame);
            Assert.True(otherFrame.Index >= 100_000);
            var otherLocals = await engine.VariablesAsync(otherFrame.Index);
            Assert.Equal("73", Assert.Single(otherLocals, v => v.Name == "otherLocal").Value);
            var otherValue = await engine.EvaluateAsync(otherFrame.Index, "otherLocal");
            Assert.True(otherValue.Ok, otherValue.Error);
            Assert.Equal("73", otherValue.Value);
            var otherWrite = await engine.SetVariableAsync(otherFrame.Index, "otherLocal", "74");
            Assert.True(otherWrite.Ok, otherWrite.Error);
            Assert.Equal("74", (await engine.EvaluateAsync(otherFrame.Index, "otherLocal")).Value);
            await Value("a.Child.Number", "17");
            var changed = await engine.SetVariableAsync(0, "a.Child.Text", "\"new value\"");
            Assert.True(changed.Ok, changed.Error);
            await Value("a.Child.Text", "\"new value\"");
            changed = await engine.SetVariableAsync(0, "a.Child.Doubled", "50");
            Assert.True(changed.Ok, changed.Error);
            await Value("a.Child.Number", "25");
            changed = await engine.SetVariableAsync(0, "a.Child", "new Node { Number = 31 }");
            Assert.True(changed.Ok, changed.Error);
            await Value("a.Child.Number", "31");
            changed = await engine.SetVariableAsync(0, "a.Child", "null");
            Assert.True(changed.Ok, changed.Error);
            await Value("a.Child", "null");
            changed = await engine.SetVariableAsync(0, "a.Child", "new Node { Number = 32 }");
            Assert.True(changed.Ok, changed.Error);
            await Value("a.Child.Number", "32");
        }
        finally
        {
            try { engine.Terminate(); } catch { }
            if (!target.HasExited) target.Kill(entireProcessTree: true);
            try { Directory.Delete(directory, true); } catch { }
        }
    }
}

