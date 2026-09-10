using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Proto.Core;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public class ProtoRenameTests
{
    [Fact]
    public async Task RenameUsesUnsavedSchemaAndConsumerText()
    {
        string path = FixturePaths.ProtoSolutionWidgetsProtoFile;
        string caller = FixturePaths.ProtoClientCallerFile;
        var schema = SourceText.From("// unsaved schema heading\n" + File.ReadAllText(path));
        var source = SourceText.From("// unsaved caller heading\n\n" + File.ReadAllText(caller));
        string session = Guid.NewGuid().ToString("N");
        try
        {
            OpenDocumentStore.Open(session, path, schema, 7);
            OpenDocumentStore.Open(session, caller, source, 12);
            var line = schema.Lines.GetLinePosition(schema.ToString().IndexOf("string label", StringComparison.Ordinal) + 8);
            var edit = await ProtoRenameHandler.RenameAsync(new RenameParams(new(LspConverters.PathToUri(path)),
                new(line.Line, line.Character), "caption"), default);
            Assert.NotNull(edit);
            string ApplyBuffer(string file, SourceText text) => text.WithChanges(edit.Changes[LspConverters.PathToUri(file)]
                .Select(e => new TextChange(LspConverters.ToTextSpan(text, e.Range), e.NewText))).ToString();
            Assert.Contains("string caption = 2", ApplyBuffer(path, schema));
            Assert.Contains("widget.Caption", ApplyBuffer(caller, source));
            Assert.StartsWith("// unsaved caller heading", ApplyBuffer(caller, source));
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
            OpenDocumentStore.Close(session, caller);
            await WorkspaceService.ReconcileOpenBufferAsync(caller);
        }
    }

    [Theory]
    [InlineData("enum Channel", 7, "Transport", "Transport.ChannelBeta")]
    [InlineData("CHANNEL_BETA", 3, "CHANNEL_RELEASE", "Channel.Release")]
    public async Task EnumRenameUpdatesImportedSchemasAndGeneratedValueNames(string needle, int offset, string name, string expected)
    {
        var edit = await Rename(FixturePaths.CommonTypesProtoFile, needle, offset, name);
        Assert.Contains(expected, Apply(FixturePaths.WidgetClientCallerFile, edit));
        await VerifyRegenerationAsync(edit, FixturePaths.ProtoProjectFile);
    }

    [Theory]
    [InlineData("oneof image", 8, "picture", "widget.PictureCase", "Widget.PictureOneofCase.ImageUrl")]
    [InlineData("string image_url", 10, "image_uri", "widget.ImageUri", "Widget.ImageOneofCase.ImageUri")]
    [InlineData("message Placement", 10, "Position", "Widget.Types.Position", "widget.Placement")]
    [InlineData("message Note", 10, "Memo", "DescribeNote(Memo note)", "note.Note")]
    public async Task RenameFollowsGeneratedNamingRules(string needle, int offset, string name, string first, string second)
    {
        var edit = await Rename(FixturePaths.WidgetTypesProtoFile, needle, offset, name);
        string client = Apply(FixturePaths.WidgetClientCallerFile, edit);
        Assert.Contains(first, client);
        Assert.Contains(second, client);
        Assert.DoesNotContain(edit.Changes.Keys, uri => uri.Contains("/Generated/", StringComparison.OrdinalIgnoreCase));
        await VerifyRegenerationAsync(edit, FixturePaths.ProtoProjectFile);
    }

    [Theory]
    [InlineData("1invalid")]
    [InlineData("id")]
    public async Task InvalidOrDuplicateFieldNamesAreRejected(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Rename(
            FixturePaths.ProtoSolutionWidgetsProtoFile, "string label", 8, name));
    }

    private static async Task<WorkspaceEdit> Rename(string path, string needle, int offset, string name)
    {
        var source = SourceText.From(File.ReadAllText(path));
        int start = source.ToString().IndexOf(needle, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var line = source.Lines.GetLinePosition(start + offset);
        var edit = await ProtoRenameHandler.RenameAsync(new RenameParams(
            new(LspConverters.PathToUri(path)), new(line.Line, line.Character), name), default);
        Assert.NotNull(edit);
        return edit;
    }

    [Theory]
    [InlineData("string label", 7, "caption", "widget.Caption", "Caption = request.Label")]
    [InlineData("rpc GetWidgetsById", 5, "FetchWidgets", "_client.FetchWidgetsAsync(request)", "FetchWidgets(GetWidgetsByIdRequest")]
    [InlineData("rpc WatchWidgets", 5, "StreamWidgets", "_client.StreamWidgets(request", "StreamWidgets(WatchWidgetsRequest")]
    [InlineData("service WidgetService", 10, "Inventory", "Inventory.InventoryClient", "Inventory.InventoryBase")]
    [InlineData("message Widget {", 10, "Item", "Item widget", "new Item")]
    public async Task SchemaRenameUpdatesConsumersAcrossProjects(string needle, int offset, string name,
        string clientExpected, string serverExpected)
    {
        string path = FixturePaths.ProtoSolutionWidgetsProtoFile;
        var source = SourceText.From(File.ReadAllText(path));
        var line = source.Lines.GetLinePosition(source.ToString().IndexOf(needle, StringComparison.Ordinal) + offset);
        var edit = await ProtoRenameHandler.RenameAsync(new RenameParams(
            new(LspConverters.PathToUri(path)), new(line.Line, line.Character), name), default);
        Assert.NotNull(edit);
        Assert.Contains(LspConverters.PathToUri(path), edit.Changes.Keys);
        Assert.DoesNotContain(edit.Changes.Keys, uri => uri.Contains("/Generated/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(edit.Changes.Keys, uri => uri.Contains("/Unrelated/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(clientExpected, Apply(FixturePaths.ProtoClientCallerFile, edit));
        Assert.Contains(serverExpected, Apply(FixturePaths.ProtoServerServiceFile, edit));
        await VerifyRegenerationAsync(edit, FixturePaths.ProtoContractsProjectFile);
    }

    private static async Task VerifyRegenerationAsync(WorkspaceEdit edit, string projectPath)
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(projectPath);
        var index = await ProtoGeneratedIndex.GetAsync(project, default);
        string package = typeof(ProtoRenameTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "GrpcToolsPath").Value!;
        string platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macosx" : "linux";
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 when OperatingSystem.IsLinux() => "arm64",
            _ => "x64",
        };
        string tools = Path.Combine(package, "tools", platform + "_" + arch);
        string extension = OperatingSystem.IsWindows() ? ".exe" : "";
        string directory = Path.Combine(Path.GetTempPath(), "RoslynSense-proto-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string root = Path.GetDirectoryName(projectPath)!;
            foreach (string path in index.ProtoFiles)
            {
                string relative = Path.GetRelativePath(root, path);
                Assert.False(relative.StartsWith("..", StringComparison.Ordinal));
                string copy = Path.Combine(directory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                await File.WriteAllTextAsync(copy, Apply(path, edit));
            }
            var solution = project.Solution;
            foreach (var candidate in solution.Projects)
            foreach (var document in candidate.Documents)
            {
                if (document.FilePath is { } path && edit.Changes.ContainsKey(LspConverters.PathToUri(path)))
                    solution = solution.WithDocumentText(document.Id, SourceText.From(Apply(path, edit)));
            }
            foreach (string path in index.CompiledProtoFiles)
            {
                string relative = Path.GetRelativePath(root, path);
                string output = Path.Combine(directory, "Generated", Path.GetDirectoryName(relative)!);
                Directory.CreateDirectory(output);
                var start = new ProcessStartInfo(Path.Combine(tools, "protoc" + extension))
                {
                    WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true,
                };
                foreach (string argument in new[] { "-I" + directory, "-I" + Path.Combine(package, "build", "native", "include"),
                    "--csharp_out=" + output, "--grpc_out=" + output,
                    "--plugin=protoc-gen-grpc=" + Path.Combine(tools, "grpc_csharp_plugin" + extension), relative.Replace('\\', '/') })
                    start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var error = process.StandardError.ReadToEndAsync();
                var stdout = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(process.ExitCode == 0, await error + await stdout);
                foreach (var document in index.DocumentsFor(path))
                {
                    string generatedPath = Path.Combine(output, Path.GetFileName(document.FilePath)!);
                    Assert.True(File.Exists(generatedPath), generatedPath);
                    solution = solution.WithDocumentText(document.Id, SourceText.From(await File.ReadAllTextAsync(generatedPath)));
                }
            }
            var affected = solution.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject(project.Id)
                .Append(project.Id);
            foreach (var id in affected)
            {
                var compilation = await solution.GetProject(id)!.GetCompilationAsync();
                var errors = compilation!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
                Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(d => d.ToString())));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static string Apply(string path, WorkspaceEdit edit)
    {
        var text = SourceText.From(File.ReadAllText(path));
        return edit.Changes.TryGetValue(LspConverters.PathToUri(path), out var edits)
            ? text.WithChanges(edits.Select(e => new TextChange(LspConverters.ToTextSpan(text, e.Range), e.NewText))).ToString()
            : text.ToString();
    }
}

