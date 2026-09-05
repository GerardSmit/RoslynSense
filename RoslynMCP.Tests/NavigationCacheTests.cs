using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Completion;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using CompletionList = RoslynMCP.Lsp.Protocol.CompletionList;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public sealed class NavigationCacheTests
{
    private const string ModelV1 = """
        namespace LayeredApp.Warehouse;
        public class Product
        {
            public int VersionOne { get; set; }
        }
        """;

    private const string ModelV2 = """
        namespace LayeredApp.Warehouse;
        // A declaration edit in another project also changes navigation's destination line.
        public class Product
        {
            public string VersionTwo { get; set; } = "";
        }
        """;

    private const string Consumer = """
        using LayeredApp.Warehouse;
        namespace LayeredApp.Storefront;
        public class Startup
        {
            public object Read(Product product)
            {
                _ = product.VersionOne;
                _ = product.VersionTwo;
                return product.;
            }
        }
        """;

    [Fact]
    public async Task SwitchingBetweenOpenFilesReusesTheOriginalDocumentAndCompilation()
    {
        string session = "navigation-switch-" + Guid.NewGuid().ToString("N");
        string[] paths = [FixturePaths.CalculatorFile, FixturePaths.ResultFile];
        try
        {
            foreach (string path in paths)
                OpenDocumentStore.Open(session, path, SourceText.From(await File.ReadAllTextAsync(path)), 1);
            foreach (string path in paths)
                await WorkspaceService.ReconcileOpenBufferAsync(path);

            var first = await ResolveAsync(paths[0]);
            var compilation = await first.Project.GetCompilationAsync();
            var model = await first.GetSemanticModelAsync();
            Assert.NotNull(compilation);
            Assert.NotNull(model);

            var other = await ResolveAsync(paths[1]);
            Assert.NotEqual(first.Id, other.Id);
            await other.GetSemanticModelAsync();
            var back = await ResolveAsync(paths[0]);

            Assert.Same(first, back);
            Assert.Same(compilation, await back.Project.GetCompilationAsync());
            Assert.Same(model, await back.GetSemanticModelAsync());
        }
        finally
        {
            await CloseAsync(session, paths);
        }
    }

    [Fact]
    public async Task ReferencedPropertyEditsRefreshCompletionAndDefinitionWithoutEditingTheConsumer()
    {
        string session = "navigation-version-" + Guid.NewGuid().ToString("N");
        string modelPath = FixturePaths.LayeredAppWarehouseModuleFile;
        string consumerPath = FixturePaths.LayeredAppStartupFile;
        var consumerText = SourceText.From(Consumer);
        using var binding = WorkspaceService.BindSolutionForTesting(FixturePaths.LayeredAppSolutionFile);
        try
        {
            OpenDocumentStore.Open(session, modelPath, SourceText.From(ModelV1), 1);
            OpenDocumentStore.Open(session, consumerPath, consumerText, 1);
            await ResolveAsync(consumerPath);
            // Loading schedules reconciliation of buffers opened before their project existed.
            // Establish the initial warm snapshot after that reconciliation, as a settled editor
            // has it, so this test isolates the later declaration edit from initial load races.
            await WorkspaceService.ReconcileOpenBufferAsync(modelPath);
            await WorkspaceService.ReconcileOpenBufferAsync(consumerPath);
            var before = await ResolveAsync(consumerPath);
            var compilation = await before.Project.GetCompilationAsync();
            Assert.NotNull(compilation?.GetTypeByMetadataName("LayeredApp.Warehouse.Product"));
            var initial = await CompleteAsync(consumerPath, consumerText);
            Assert.True(initial.Items.Any(i => i.Label == "VersionOne"),
                "Initial completion labels: " + string.Join(", ", initial.Items.Select(i => i.Label))
                + "; compiler errors: " + string.Join("; ", compilation!.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)));

            Assert.NotNull(OpenDocumentStore.Change(modelPath, 2, _ => SourceText.From(ModelV2)));
            await WorkspaceService.ReconcileOpenBufferAsync(modelPath);
            // No consumer edit and no full bind first: completion itself must see the declaration
            // change, even when its previous warm compilation exists in the frozen tracker.
            var changed = await CompleteAsync(consumerPath, consumerText);
            Assert.Contains(changed.Items, i => i.Label == "VersionTwo");
            Assert.DoesNotContain(changed.Items, i => i.Label == "VersionOne");
            await AssertDefinitionAsync(consumerPath, consumerText, "VersionTwo", modelPath, ModelV2);
            Assert.Empty(await NavigationHandlers.DefinitionAsync(
                At(consumerPath, consumerText, "VersionOne"), false, default, LanguageSession.Empty));

            // A late v1 notification may not put the old property back into the workspace.
            Assert.Null(OpenDocumentStore.Change(modelPath, 1, _ => SourceText.From(ModelV1)));
            Assert.Contains((await CompleteAsync(consumerPath, consumerText)).Items, i => i.Label == "VersionTwo");
            Assert.True(OpenDocumentStore.TryGet(consumerPath, out var unchanged));
            Assert.Same(consumerText, unchanged);

            // Reverting the declaration is another real version, not a hit on either old result.
            Assert.NotNull(OpenDocumentStore.Change(modelPath, 3, _ => SourceText.From(ModelV1)));
            await WorkspaceService.ReconcileOpenBufferAsync(modelPath);
            var reverted = await CompleteAsync(consumerPath, consumerText);
            Assert.Contains(reverted.Items, i => i.Label == "VersionOne");
            Assert.DoesNotContain(reverted.Items, i => i.Label == "VersionTwo");
            await AssertDefinitionAsync(consumerPath, consumerText, "VersionOne", modelPath, ModelV1);
        }
        finally
        {
            await DrainExpandedAsync();
            await CloseAsync(session, modelPath, consumerPath);
        }
    }

    [Fact]
    public async Task RenamingALibraryPropertyIncludesAnUnloadedConsumerAndUsesUnsavedRanges()
    {
        string session = "navigation-rename-" + Guid.NewGuid().ToString("N");
        string modelPath = FixturePaths.LayeredAppWarehouseModuleFile;
        string consumerPath = FixturePaths.LayeredAppStartupFile;
        string modelSource = "// unsaved declaration header\n" + ModelV1;
        string consumerSource = "// unsaved caller header\n// another line\n" + Consumer;
        using var binding = WorkspaceService.BindSolutionForTesting(FixturePaths.LayeredAppSolutionFile);
        await WorkspaceService.EvictProjectForTests(FixturePaths.LayeredAppStorefrontProjectFile);
        try
        {
            OpenDocumentStore.Open(session, modelPath, SourceText.From(modelSource), 1);
            OpenDocumentStore.Open(session, consumerPath, SourceText.From(consumerSource), 1);
            var document = await ResolveAsync(modelPath);
            Assert.DoesNotContain(document.Project.Solution.Projects,
                p => string.Equals(p.FilePath, FixturePaths.LayeredAppStorefrontProjectFile, StringComparison.OrdinalIgnoreCase));

            var position = At(modelPath, SourceText.From(modelSource), "VersionOne");
            var edit = await RenameHandler.RenameAsync(
                new RenameParams(position.TextDocument, position.Position, "RenamedVersion"), default, LanguageSession.Empty);
            Assert.NotNull(edit);
            Assert.NotNull(edit!.Changes);
            foreach (var (path, source) in new[] { (modelPath, modelSource), (consumerPath, consumerSource) })
            {
                Assert.True(edit.Changes!.TryGetValue(LspConverters.PathToUri(path), out var changes),
                    "Rename omitted " + Path.GetFileName(path));
                var text = SourceText.From(source);
                var change = Assert.Single(changes!);
                var span = TextSpan.FromBounds(LspConverters.ToOffset(text, change.Range.Start),
                    LspConverters.ToOffset(text, change.Range.End));
                Assert.Equal("VersionOne", text.ToString(span));
                Assert.Equal("RenamedVersion", change.NewText);
                Assert.Equal(source.Replace("VersionOne", "RenamedVersion", StringComparison.Ordinal),
                    text.WithChanges(new TextChange(span, change.NewText)).ToString());
            }
            // A rename response is an edit proposal: neither the buffers nor disk were changed.
            Assert.True(OpenDocumentStore.TryGet(modelPath, out var current));
            Assert.Equal(modelSource, current.ToString());
            Assert.DoesNotContain("RenamedVersion", await File.ReadAllTextAsync(modelPath));
        }
        finally
        {
            await CloseAsync(session, modelPath, consumerPath);
            await WorkspaceService.EvictProjectForTests(FixturePaths.LayeredAppStorefrontProjectFile);
        }
    }

    [Fact]
    public async Task RenamingDoesNotRetargetTheCaretAfterTheInitiatingBufferChanges()
    {
        const string original = "namespace SampleProject; public class Calculator { public int First => 1; public int Other => 2; }";
        const string changed = "namespace SampleProject; public class Calculator { public int Other => 1; public int First => 2; }";
        string session = "navigation-rename-race-" + Guid.NewGuid().ToString("N");
        string path = FixturePaths.CalculatorFile;
        var gate = new PausingRenameProvider();
        Task<WorkspaceEdit?>? rename = null;
        try
        {
            OpenDocumentStore.Open(session, path, SourceText.From(original), 1);
            var at = At(path, SourceText.From(original), "First");
            rename = RenameHandler.RenameAsync(new RenameParams(at.TextDocument, at.Position, "Renamed"),
                default, new LanguageSession([gate]));
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Both names have the same length: the original position now binds successfully to
            // Other. Rebinding without checking the initiating source would rename that property.
            Assert.NotNull(OpenDocumentStore.Change(path, 2, _ => SourceText.From(changed)));
            await WorkspaceService.ReconcileOpenBufferAsync(path);
            gate.Release.TrySetResult();
            Assert.Null(await rename.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            gate.Release.TrySetResult();
            try
            {
                if (rename is not null)
                    await rename.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally { await CloseAsync(session, path); }
        }
    }

    private sealed class PausingRenameProvider : ILanguagePack, ISymbolFreeRenameProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "pausing-rename";
        public string DisplayName => "Pausing rename";
        public ImmutableArray<string> FileExtensions => [];
        public LanguageCapabilities Capabilities => LanguageCapabilities.None;
        public ImmutableArray<string> WellKnownTypeNames => [];
        public ImmutableArray<SymbolKind> InterestingSymbolKinds => [];
        public bool IsProjectionPath(string? filePath) => false;
        public Task<PrepareRenameResult?> PrepareAsync(string filePath, int offset, CancellationToken ct) =>
            Task.FromResult<PrepareRenameResult?>(null);

        public async Task<WorkspaceEdit?> RenameAsync(
            string filePath, int offset, string newName, Project? project, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return null;
        }
    }

    [Fact]
    public async Task RenamingDoesNotRetargetAnInheritedPropertyAfterItsDeclarationChanges()
    {
        const string caller = "namespace SampleProject; public class Caller { public int Read(Derived value) => value.Name; }";
        const string original = "namespace SampleProject; public class Base { public int Name => 1; } public class Derived : Base { public new int Name => 2; }";
        const string changed = "namespace SampleProject; public class Base { public int Name => 1; } public class Derived : Base { }";
        string session = "navigation-rename-declaration-" + Guid.NewGuid().ToString("N");
        string path = FixturePaths.CalculatorFile;
        string declarationPath = FixturePaths.ResultFile;
        var gate = new PausingRenameProvider();
        Task<WorkspaceEdit?>? rename = null;
        try
        {
            OpenDocumentStore.Open(session, path, SourceText.From(caller), 1);
            OpenDocumentStore.Open(session, declarationPath, SourceText.From(original), 1);
            var at = At(path, SourceText.From(caller), "Name");
            var initialDefinition = Assert.Single(await NavigationHandlers.DefinitionAsync(
                at, false, default, LanguageSession.Empty));
            Assert.Equal(LspConverters.PathToUri(declarationPath), initialDefinition.Uri, ignoreCase: true);
            int expectedOffset = original.LastIndexOf("Name", StringComparison.Ordinal);
            Assert.Equal(new Position(0, expectedOffset), initialDefinition.Range.Start);
            rename = RenameHandler.RenameAsync(new RenameParams(at.TextDocument, at.Position, "Renamed"),
                default, new LanguageSession([gate]));
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.NotNull(OpenDocumentStore.Change(declarationPath, 2, _ => SourceText.From(changed)));
            await WorkspaceService.ReconcileOpenBufferAsync(declarationPath);
            gate.Release.TrySetResult();
            Assert.Null(await rename.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            gate.Release.TrySetResult();
            try
            {
                if (rename is not null)
                    await rename.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally { await CloseAsync(session, path, declarationPath); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingImportCompletionCannotCrossDependencyOrParseOptionSnapshots(bool changeParseOptions)
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var dependency = workspace.AddProject("Contracts", LanguageNames.CSharp);
        var declaration = workspace.AddDocument(dependency.Id, "Model.cs", SourceText.From("public class OldName {}"));
        var consumer = workspace.AddProject("Consumer", LanguageNames.CSharp);
        Assert.True(workspace.TryApplyChanges(workspace.CurrentSolution.AddProjectReference(
            consumer.Id, new ProjectReference(dependency.Id))));
        var document = workspace.AddDocument(consumer.Id, "Use.cs", SourceText.From("class C { object M() => new ; }"));
        var text = await document.GetTextAsync();
        int caret = text.ToString().IndexOf("new ;", StringComparison.Ordinal) + 4;
        var options = Microsoft.CodeAnalysis.Completion.CompletionOptions.Default with
        {
            ShowItemsFromUnimportedNamespaces = true,
            ExpandedCompletionBehavior = ExpandedCompletionMode.ExpandedItemsOnly,
        };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ExpandedCompletionPass.Reset();
        ExpandedCompletionPass.Gate = gate.Task;
        Task? first = null;
        Task? second = null;
        try
        {
            var service = CompletionService.GetService(document)!;
            first = ExpandedCompletionPass.Start(service, document, text, caret, caret, options, CompletionTrigger.Invoke);
            Assert.Same(first, ExpandedCompletionPass.Start(service, document, text, caret, caret, options, CompletionTrigger.Invoke));

            var changedSolution = changeParseOptions
                ? document.Project.Solution.WithProjectParseOptions(document.Project.Id,
                    ((CSharpParseOptions)document.Project.ParseOptions!).WithPreprocessorSymbols("NEW_API"))
                : document.Project.Solution.WithDocumentText(declaration.Id, SourceText.From("public class NewName {}"));
            var changedDocument = changedSolution.GetDocument(document.Id)!;
            Assert.Equal(text.ToString(), (await changedDocument.GetTextAsync()).ToString());
            second = ExpandedCompletionPass.Start(service, changedDocument, text, caret, caret, options, CompletionTrigger.Invoke);
            Assert.NotSame(first, second);
        }
        finally
        {
            gate.TrySetResult();
            await Task.WhenAll(new[] { first, second }.OfType<Task>());
            ExpandedCompletionPass.Gate = Task.CompletedTask;
            ExpandedCompletionPass.Reset();
        }
    }

    private static async Task<Document> ResolveAsync(string path)
    {
        var document = await LspDocumentResolver.ResolveAsync(path, default);
        Assert.NotNull(document);
        return document!;
    }

    private static TextDocumentPositionParams At(string path, SourceText text, string anchor, int after = 0)
    {
        int index = text.ToString().IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(index >= 0);
        var position = text.Lines.GetLinePosition(index + after);
        return new(new TextDocumentIdentifier(LspConverters.PathToUri(path)), new Position(position.Line, position.Character));
    }

    private static Task<CompletionList> CompleteAsync(string path, SourceText text)
    {
        var at = At(path, text, "return product.", "return product.".Length);
        return CompletionHandler.CompletionAsync(new CompletionParams(at.TextDocument, at.Position), new LspResolveCache(), default);
    }

    private static async Task AssertDefinitionAsync(string path, SourceText text, string name, string targetPath, string targetSource)
    {
        var location = Assert.Single(await NavigationHandlers.DefinitionAsync(
            At(path, text, name), false, default, LanguageSession.Empty));
        Assert.Equal(LspConverters.PathToUri(targetPath), location.Uri, ignoreCase: true);
        Assert.Equal(At(targetPath, SourceText.From(targetSource), name).Position, location.Range.Start);
    }

    private static async Task DrainExpandedAsync()
    {
        if (ExpandedCompletionPass.Pending is { } pending)
            await pending;
        ExpandedCompletionPass.Reset();
    }

    private static async Task CloseAsync(string session, params string[] paths)
    {
        foreach (string path in paths)
            OpenDocumentStore.Close(session, path);
        foreach (string path in paths)
            await WorkspaceService.ReconcileOpenBufferAsync(path);
    }
}
