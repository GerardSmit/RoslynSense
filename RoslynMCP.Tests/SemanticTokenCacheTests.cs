using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public sealed class SemanticTokenCacheTests : IDisposable
{
    private readonly AdhocWorkspace _workspace = new(WorkspaceService.HostServices);
    private readonly string _session = "semantic-cache-" + Guid.NewGuid().ToString("N");
    private readonly LanguageRegistry _registry = LanguageRegistry.Current;

    public SemanticTokenCacheTests() => LanguageRegistry.Empty.Publish();

    public void Dispose()
    {
        SemanticTokensHandler.Forget(_session);
        SemanticTokensHandler.Forget(_session + "-other");
        _registry.Publish();
        _workspace.Dispose();
    }

    [Fact]
    public async Task RepeatedUnchangedDocumentIsClassifiedOnce()
    {
        var document = CreateDocument("class C { static int Value = 1; }");
        await document.Project.GetCompilationAsync();
        long before = SemanticTokensHandler.ClassificationComputations;

        var first = await ComputeAsync(document);
        var second = await ComputeAsync(document);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
        Assert.Equal(before + 1, SemanticTokensHandler.ClassificationComputations);
    }

    [Fact]
    public async Task EditingTheBufferReclassifiesAndMovesTokenSpans()
    {
        var document = CreateDocument("class C { int Value; }");
        await document.Project.GetCompilationAsync();
        var original = await ComputeAsync(document);
        var text = await document.GetTextAsync();
        var edited = document.WithText(text.Replace(new TextSpan(0, 0), "// inserted\n"));
        await edited.Project.GetCompilationAsync();
        long before = SemanticTokensHandler.ClassificationComputations;

        var changed = await ComputeAsync(edited);
        Assert.False(original.SequenceEqual(changed));
        Assert.Equal(before + 1, SemanticTokensHandler.ClassificationComputations);
        Assert.Equal(changed, await ComputeAsync(edited));
        Assert.Equal(before + 1, SemanticTokensHandler.ClassificationComputations);
    }

    [Fact]
    public async Task ReferencedProjectEditChangesClassificationInUneditedConsumer()
    {
        const string source = "class Consumer { Thing member; }";
        var document = CreateDocument(source);
        var libraryId = ProjectId.CreateNewId();
        var libraryDocumentId = DocumentId.CreateNewId(libraryId);
        var solution = document.Project.Solution
            .AddProject(libraryId, "Library", "Library", LanguageNames.CSharp)
            .WithProjectCompilationOptions(libraryId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(libraryId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument(libraryDocumentId, "Thing.cs", SourceText.From("public class Thing { }"))
            .AddProjectReference(document.Project.Id, new ProjectReference(libraryId));
        document = solution.GetDocument(document.Id)!;
        await document.Project.GetCompilationAsync();

        Assert.Equal("class", TokenAt(await ComputeAsync(document), source, "Thing"));
        var edited = solution.WithDocumentText(libraryDocumentId, SourceText.From("public struct Thing { }"))
            .GetDocument(document.Id)!;
        Assert.Same(await document.GetTextAsync(), await edited.GetTextAsync());
        await edited.Project.GetCompilationAsync();
        long before = SemanticTokensHandler.ClassificationComputations;

        Assert.Equal("struct", TokenAt(await ComputeAsync(edited), source, "Thing"));
        Assert.Equal(before + 1, SemanticTokensHandler.ClassificationComputations);
    }

    [Fact]
    public async Task ParseConfigurationChangeInvalidatesUnchangedText()
    {
        const string source = "#if FEATURE\nclass Active { }\n#else\nstruct Active { }\n#endif";
        var document = CreateDocument(source);
        await document.Project.GetCompilationAsync();
        var before = await ComputeAsync(document);
        var configured = document.Project.Solution.WithProjectParseOptions(document.Project.Id,
            new CSharpParseOptions(preprocessorSymbols: ["FEATURE"])).GetDocument(document.Id)!;
        await configured.Project.GetCompilationAsync();
        long computations = SemanticTokensHandler.ClassificationComputations;

        var after = await ComputeAsync(configured);
        Assert.False(before.SequenceEqual(after));
        Assert.Equal("class", TokenAt(after, source, "Active"));
        Assert.Equal(computations + 1, SemanticTokensHandler.ClassificationComputations);
    }

    [Fact]
    public async Task CompletingGeneratorsInvalidatesTheFrozenStartupResult()
    {
        const string source = "class Consumer { Generated member; }";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var document = WithGenerator(CreateDocument(source), new TestGeneratorReference(entered, release.Task));
        var compilation = Task.Run(() => document.Project.GetCompilationAsync());
        try
        {
            // The ordinary declarations are ready while generation is still in progress. This
            // is a valid startup freeze; a never-started tracker now falls back to a full bind
            // because its synthetic project state does not preserve all current declarations.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(document.Project.TryGetCompilation(out _));
            await ComputeAsync(document).WaitAsync(TimeSpan.FromSeconds(30));
            long computations = SemanticTokensHandler.ClassificationComputations;

            // Same original Document object: only its background compilation/generators finished.
            release.TrySetResult();
            await compilation.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("class", TokenAt(await ComputeAsync(document), source, "Generated"));
            Assert.Equal(computations + 1, SemanticTokensHandler.ClassificationComputations);
            await ComputeAsync(document);
            Assert.Equal(computations + 1, SemanticTokensHandler.ClassificationComputations);
        }
        finally
        {
            release.TrySetResult();
            await compilation.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task RegeneratingChangedInputUpdatesUneditedConsumers()
    {
        const string source = "class Consumer { Generated member; }";
        var document = WithGenerator(CreateDocument(source));
        await document.Project.GetCompilationAsync();
        Assert.Equal("class", TokenAt(await ComputeAsync(document), source, "Generated"));

        var input = document.Project.AdditionalDocumentIds.Single();
        var edited = document.Project.Solution.WithAdditionalDocumentText(input, SourceText.From("struct"))
            .GetDocument(document.Id)!;
        // The host runs generators in Balanced mode: edits intentionally retain generated
        // trees until save/build requests regeneration. Exercise that transition explicitly.
        Assert.True(_workspace.TryApplyChanges(edited.Project.Solution));
        await _workspace.ProcessUpdateSourceGeneratorRequestAsync(
            ImmutableSegmentedList.Create<(ProjectId?, bool)>((edited.Project.Id, true)), default);
        edited = _workspace.CurrentSolution.GetDocument(document.Id)!;
        var compilation = await edited.Project.GetCompilationAsync();
        Assert.Equal(TypeKind.Struct, compilation!.GetTypeByMetadataName("Generated")!.TypeKind);
        long computations = SemanticTokensHandler.ClassificationComputations;
        Assert.Equal("struct", TokenAt(await ComputeAsync(edited), source, "Generated"));
        Assert.Equal(computations + 1, SemanticTokensHandler.ClassificationComputations);
    }

    [Fact]
    public async Task EmbeddedSettingsAndRegistryChangesStillApplyOnClassificationCacheHits()
    {
        const string source = "class C { void M() {\n// lang=cachetest\nvar text = \"payload\"; } }";
        var pack = new MutableEmbeddedPack { TokenType = "field" };
        new LanguageRegistry([pack]).Publish();
        var document = CreateDocument(source);
        await document.Project.GetCompilationAsync();
        long before = SemanticTokensHandler.ClassificationComputations;
        Assert.Equal("field", TokenAt(await ComputeAsync(document), source, "payload"));

        pack.TokenType = "constant";
        Assert.Equal("constant", TokenAt(await ComputeAsync(document), source, "payload"));
        Assert.Equal(2, pack.Calls);

        new LanguageRegistry([new MutableEmbeddedPack { TokenType = "method" }]).Publish();
        Assert.Equal("method", TokenAt(await ComputeAsync(document), source, "payload"));
        Assert.Equal(before + 1, SemanticTokensHandler.ClassificationComputations);
    }

    [Fact]
    public async Task SessionsHaveIndependentCachesAndDisconnectReleasesTheirEntry()
    {
        var document = CreateDocument("class C { }");
        await document.Project.GetCompilationAsync();
        long before = SemanticTokensHandler.ClassificationComputations;
        var first = await ComputeAsync(document);
        var other = await SemanticTokensHandler.ComputeDocumentAsync(_session + "-other", document, null, default);
        Assert.Equal(first, other);
        Assert.Equal(before + 2, SemanticTokensHandler.ClassificationComputations);

        SemanticTokensHandler.Forget(_session);
        Assert.Equal(first, await ComputeAsync(document));
        Assert.Equal(before + 3, SemanticTokensHandler.ClassificationComputations);
    }

    private Task<int[]> ComputeAsync(Document document) =>
        SemanticTokensHandler.ComputeDocumentAsync(_session, document, window: null, default);

    private Document CreateDocument(string source) =>
        _workspace.AddProject("SemanticCache", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument("Consumer.cs", SourceText.From(source), filePath: "Consumer.cs");

    private static Document WithGenerator(Document document, AnalyzerReference? generator = null)
    {
        var project = document.Project.AddAnalyzerReference(generator ?? new TestGeneratorReference())
            .AddAdditionalDocument("kind.txt", SourceText.From("class")).Project;
        return project.GetDocument(document.Id)!;
    }

    private static string? TokenAt(int[] data, string source, string marker)
    {
        var text = SourceText.From(source);
        int offset = source.IndexOf(marker, StringComparison.Ordinal);
        var position = text.Lines.GetLinePosition(offset);
        int line = 0, character = 0;
        for (int i = 0; i < data.Length; i += 5)
        {
            character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            line += data[i];
            if (line == position.Line && character == position.Character)
                return SemanticTokensHandler.TokenTypes[data[i + 3]];
        }
        return null;
    }

    private sealed class TestGeneratorReference(TaskCompletionSource? entered = null, Task? release = null) : AnalyzerReference
    {
        private readonly ImmutableArray<ISourceGenerator> _generators = [new KindGenerator(entered, release).AsSourceGenerator()];
        public override string? FullPath => null;
        public override object Id => this;
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];
        public override ImmutableArray<ISourceGenerator> GetGenerators(string language) => _generators;
        public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages() => _generators;
    }

    private sealed class KindGenerator(TaskCompletionSource? entered = null, Task? release = null) : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var kind = context.AdditionalTextsProvider.Select(static (text, ct) => text.GetText(ct)!.ToString());
            context.RegisterSourceOutput(kind, (output, declarationKind) =>
            {
                entered?.TrySetResult();
                release?.Wait(output.CancellationToken);
                output.AddSource("Generated.g.cs", $"public {declarationKind} Generated {{ }}");
            });
        }
    }

    private sealed class MutableEmbeddedPack : ILanguagePack, IEmbeddedStringLanguage, IEmbeddedSemanticTokensProvider
    {
        public string TokenType { get; set; } = "field";
        public int Calls { get; private set; }
        public string Id => "cachetest";
        public string DisplayName => "Cache test";
        public ImmutableArray<string> FileExtensions => [];
        public LanguageCapabilities Capabilities => LanguageCapabilities.None;
        public ImmutableArray<string> WellKnownTypeNames => [];
        public ImmutableArray<SymbolKind> InterestingSymbolKinds => [];
        public bool IsProjectionPath(string? filePath) => false;
        public ImmutableArray<string> StringSyntaxIdentifiers => ["cachetest"];

        public Task<IReadOnlyList<EmbeddedToken>> SemanticTokensAsync(EmbeddedStringContext context, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<EmbeddedToken>>(
                [new(new TextSpan(context.Token.Span.Start + 1, context.Token.Span.Length - 2), TokenType)]);
        }
    }
}
