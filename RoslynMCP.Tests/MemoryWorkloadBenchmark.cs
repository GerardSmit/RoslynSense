using System.Diagnostics;
using System.Text.Json;
using RoslynMCP.Lsp;
using RoslynMCP.Services.Memory;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

namespace RoslynMCP.Tests;

/// <summary>Drives an isolated host. All edits are LSP buffers; the source tree is never written.</summary>
[Collection(SharedState.Name)]
public class MemoryWorkloadBenchmark(ITestOutputHelper output)
{
    [RoslynSenseBenchFact]
    public async Task RepeatedEditorWorkload()
    {
        var solution = Environment.GetEnvironmentVariable("ROSLYNSENSE_MEMORY_SOLUTION") ?? FixturePaths.AspxProjectFile;
        var directory = Path.GetDirectoryName(solution)!;
        var source = Environment.GetEnvironmentVariable("ROSLYNSENSE_MEMORY_SOURCE")
            ?? Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .First(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
        var markup = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p) is ".aspx" or ".ascx" or ".master")
            .Order(StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
        string resultDirectory = Environment.GetEnvironmentVariable("ROSLYNSENSE_MEMORY_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "roslynsense-memory-benchmark", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(resultDirectory);
        string binary = Environment.GetEnvironmentVariable("ROSLYNSENSE_MEMORY_BINARY") ?? typeof(LspProxy).Assembly.Location;
        int cycles = int.TryParse(Environment.GetEnvironmentVariable("ROSLYNSENSE_MEMORY_CYCLES"), out var count) ? count : 30;

        for (int repeat = 0; repeat < 3; repeat++)
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            };
            foreach (string arg in new[] { binary, "--lsp", "--solution", solution }) psi.ArgumentList.Add(arg);
            psi.Environment["ROSLYNMCP_SHARED_HOST"] = "0";
            using var process = Process.Start(psi)!;
            var errors = process.StandardError.ReadToEndAsync();
            using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(process.StandardInput.BaseStream,
                process.StandardOutput.BaseStream, new SystemTextJsonFormatter()));
            rpc.StartListening();
            var samples = new List<object>();
            var timings = new List<object>();
            bool memorySupported = true;
            try
            {
                await Request("initialize", new { processId = Environment.ProcessId, rootUri = new Uri(directory).AbsoluteUri,
                    capabilities = new { textDocument = new { diagnostic = new { dynamicRegistration = false } } } });
                await rpc.NotifyAsync("initialized");
                await Capture("cold");
                string sourceText = await File.ReadAllTextAsync(source);
                await Open(source, sourceText, "csharp");
                foreach (string page in markup)
                {
                    await Open(page, await File.ReadAllTextAsync(page), "webforms");
                    await Request("textDocument/hover", new { textDocument = Doc(page), position = new { line = 0, character = 5 } });
                    await Close(page);
                }
                await Capture("visited-markup");
                object[] previous = [];
                for (int cycle = 0; cycle < cycles; cycle++)
                {
                    // A method-body edit followed periodically by a declaration change.
                    string probe = $"class MemoryWorkloadProbe {{ int Value{cycle / 5}() => System.Math.Abs({cycle}); }}";
                    string text = sourceText + "\n" + probe + "\n";
                    int probeLine = sourceText.Count(c => c == '\n') + 1;
                    var hoverPosition = new { line = probeLine, character = probe.IndexOf("Value", StringComparison.Ordinal) + 1 };
                    var completionPosition = new { line = probeLine, character = probe.IndexOf("Abs", StringComparison.Ordinal) };
                    await rpc.NotifyWithParameterObjectAsync("textDocument/didChange", new
                    { textDocument = new { uri = new Uri(source).AbsoluteUri, version = cycle + 2 }, contentChanges = new[] { new { text } } });
                    await Request("textDocument/hover", new { textDocument = Doc(source), position = hoverPosition });
                    await Request("textDocument/completion", new { textDocument = Doc(source), position = completionPosition });
                    await Request("textDocument/codeAction", new { textDocument = Doc(source), range = new
                    { start = new { line = probeLine, character = 6 }, end = new { line = probeLine, character = 25 } }, context = new { diagnostics = Array.Empty<object>() } });
                    if (markup.Length > 0)
                    {
                        string page = markup[cycle % markup.Length];
                        await Open(page, await File.ReadAllTextAsync(page) + $"\n<!-- memory cycle {cycle} -->", "webforms");
                        await Request("textDocument/diagnostic", new { textDocument = Doc(page) });
                        await Close(page);
                    }
                    var diagnostics = await Request("workspace/diagnostic", new { previousResultIds = previous });
                    if (diagnostics.TryGetProperty("items", out var items))
                        previous = items.EnumerateArray().Where(i => i.TryGetProperty("resultId", out _))
                            .Select(i => (object)new { uri = i.GetProperty("uri").GetString(), value = i.GetProperty("resultId").GetString() }).ToArray();
                    if (cycle % 10 == 0)
                        await Request("textDocument/references", new { textDocument = Doc(source), position = hoverPosition, context = new { includeDeclaration = true } });
                    if (cycle % 10 == 0)
                        await Request("textDocument/rename", new { textDocument = Doc(source), position = hoverPosition, newName = "RenamedProbe" });
                    await Capture("cycle:" + cycle);
                }
                await Close(source);
                await Capture("closed");
                // Long enough for both recent semantic caches and resolve menus to expire naturally.
                for (int i = 0; i < 5; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    await Capture("idle:" + (i + 1) * 30);
                }
                await rpc.InvokeAsync<object?>("shutdown").WaitAsync(TimeSpan.FromSeconds(20));
                await rpc.NotifyAsync("exit");
            }
            finally
            {
                await File.WriteAllTextAsync(Path.Combine(resultDirectory, $"run-{repeat}.json"),
                    JsonSerializer.Serialize(new { WorkloadVersion = 2, binary, solution, source, MarkupFiles = markup.Length, cycles, samples, timings }));
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
                await File.WriteAllTextAsync(Path.Combine(resultDirectory, $"run-{repeat}.stderr.txt"), await errors);
                output.WriteLine($"Run {repeat}: {resultDirectory}");
            }

            async Task<JsonElement> Request(string method, object parameters)
            {
                var watch = Stopwatch.StartNew();
                var result = await rpc.InvokeWithParameterObjectAsync<JsonElement>(method, parameters).WaitAsync(TimeSpan.FromMinutes(3));
                timings.Add(new { method, Milliseconds = watch.Elapsed.TotalMilliseconds });
                return result;
            }
            async Task Capture(string stage)
            {
                JsonElement? server = null;
                if (memorySupported)
                {
                    try { server = await rpc.InvokeAsync<JsonElement>("roslynSense/memory").WaitAsync(TimeSpan.FromSeconds(30)); }
                    catch (RemoteMethodNotFoundException) { memorySupported = false; }
                }
                process.Refresh();
                samples.Add(new { stage, TimestampUtc = DateTime.UtcNow, Pid = process.Id,
                    process.PrivateMemorySize64, process.WorkingSet64, Children = ChildProcessMemory.Capture(process.Id), server });
                await File.WriteAllTextAsync(Path.Combine(resultDirectory, $"run-{repeat}.progress.json"),
                    JsonSerializer.Serialize(new { stage, process.Id, process.PrivateMemorySize64, Samples = samples.Count }));
            }
            Task Open(string path, string text, string languageId) => rpc.NotifyWithParameterObjectAsync("textDocument/didOpen",
                new { textDocument = new { uri = new Uri(path).AbsoluteUri, languageId, version = 1, text } });
            Task Close(string path) => rpc.NotifyWithParameterObjectAsync("textDocument/didClose", new { textDocument = Doc(path) });
        }
    }
    private static object Doc(string path) => new { uri = new Uri(path).AbsoluteUri };
}
