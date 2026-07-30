using System.Diagnostics;
using RoslynMCP.Lsp.Protocol;
using StreamJsonRpc;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Spawns the real <c>roslyn-sense --lsp</c> process (as an editor would) and drives it over
/// stdio: initialize → didOpen with UNSAVED buffer text → definition against that buffer.
/// ROSLYNMCP_SHARED_HOST=0 forces the in-process fallback so the test leaves no daemon behind;
/// the daemon path shares the exact same LspSessionHost (covered by LspTransportTests).
/// </summary>
public class LspEndToEndTests
{
    [Fact]
    public async Task LspProcessServesDefinitionAgainstUnsavedBufferText()
    {
        string exePath = typeof(RoslynMCP.Lsp.LspProxy).Assembly.Location;

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = FixturePaths.SampleProjectDir,
        };
        psi.ArgumentList.Add(exePath);
        psi.ArgumentList.Add("--lsp");
        psi.Environment["ROSLYNMCP_SHARED_HOST"] = "0";

        using var process = Process.Start(psi)!;
        _ = process.StandardError.ReadToEndAsync(); // drain

        try
        {
            using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                process.StandardInput.BaseStream, process.StandardOutput.BaseStream,
                new SystemTextJsonFormatter()));
            rpc.StartListening();

            var init = await rpc.InvokeWithParameterObjectAsync<InitializeResult>(
                "initialize", new { processId = Environment.ProcessId, rootUri = (string?)null })
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(init.Capabilities.DefinitionProvider);
            await rpc.NotifyAsync("initialized");

            // Open Calculator.cs with EXTRA unsaved content: a Multiply method plus a call to
            // it. Definition on the call must resolve inside the buffer — proving the server
            // analyzes editor state, not the file on disk.
            string path = FixturePaths.CalculatorFile;
            string uri = new Uri(path).AbsoluteUri;
            string diskText = await File.ReadAllTextAsync(path);
            string bufferText = diskText.Replace(
                "public int Add(int a, int b) => a + b;",
                "public int Add(int a, int b) => a + b;\n\n" +
                "    public int Multiply(int a, int b) => a * b;\n\n" +
                "    public int Twice(int a) => Multiply(a, 2);");
            Assert.NotEqual(diskText, bufferText);

            await rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new
            {
                textDocument = new { uri, languageId = "csharp", version = 1, text = bufferText },
            });

            // Position of the `Multiply(a, 2)` call in the buffer.
            int callIndex = bufferText.IndexOf("Multiply(a, 2)", StringComparison.Ordinal);
            var (line, character) = OffsetToPosition(bufferText, callIndex);

            var locations = await rpc.InvokeWithParameterObjectAsync<Location[]>(
                "textDocument/definition",
                new { textDocument = new { uri }, position = new { line, character } })
                .WaitAsync(TimeSpan.FromMinutes(3)); // first request loads the workspace

            var location = Assert.Single(locations);
            int declIndex = bufferText.IndexOf("public int Multiply", StringComparison.Ordinal);
            var (declLine, _) = OffsetToPosition(bufferText, declIndex);
            Assert.Equal(declLine, location.Range.Start.Line);

            await rpc.InvokeAsync<object?>("shutdown");
            await rpc.NotifyAsync("exit");
        }
        finally
        {
            if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
                process.Kill(entireProcessTree: true);
        }
    }

    private static (int Line, int Character) OffsetToPosition(string text, int offset)
    {
        int line = 0, lineStart = 0;
        for (int i = 0; i < offset; i++)
        {
            if (text[i] == '\n') { line++; lineStart = i + 1; }
        }
        return (line, offset - lineStart);
    }
}
