using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public sealed class FrozenSemanticsFreshnessTests
{
    private const string Model = "public class Product { public int VersionOne { get; set; } public int Compute() => 1; }";
    private const string Consumer = "public class Consumer { public object Read(Product product) => product; }";

    [Fact]
    public async Task NeverCompiledProjectUsesCurrentSnapshotAndKeepsSiblingDeclarations()
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var (solution, _, consumerId) = CreateSolution(workspace, referencedProject: false);
        var document = solution.GetDocument(consumerId)!;
        Assert.False(solution.CompilationState.TryGetCompilationTracker(document.Project.Id, out _));

        var selected = await document.FreezeAsync(default);

        Assert.Same(document, selected);
        var compilation = await selected.Project.GetCompilationAsync();
        Assert.NotEmpty(compilation!.GetTypeByMetadataName("Product")!.GetMembers("VersionOne"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeclarationChangesRemainVisibleWhenFreezingAnotherDocument(
        bool referencedProject, bool newerConsumerDeclaration)
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var (solution, modelId, consumerId) = CreateSolution(workspace, referencedProject);
        await solution.GetDocument(consumerId)!.Project.GetCompilationAsync();

        var changed = solution.WithDocumentText(modelId, SourceText.From(Model.Replace("VersionOne", "VersionTwo")));
        if (newerConsumerDeclaration)
        {
            // An aggregate MAX semantic version would see only this newer stamp in both
            // snapshots, hiding the older declaration edit in the other document/project.
            changed = changed.WithDocumentText(consumerId, SourceText.From(Consumer.Replace("class Consumer", "class NewConsumer")));
        }

        var document = changed.GetDocument(consumerId)!;
        Assert.False(document.Project.TryGetCompilation(out _));
        var selected = await document.FreezeAsync(default);
        var compilation = await selected.Project.GetCompilationAsync();
        var product = compilation!.GetTypeByMetadataName("Product");
        Assert.NotNull(product);
        Assert.NotEmpty(product!.GetMembers("VersionTwo"));
        Assert.Empty(product.GetMembers("VersionOne"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyOnlyEditsInOtherDocumentsKeepTheFrozenPath(bool referencedProject)
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var (solution, modelId, consumerId) = CreateSolution(workspace, referencedProject);
        await solution.GetDocument(consumerId)!.Project.GetCompilationAsync();
        var model = solution.GetDocument(modelId)!;
        var initialDeclarationVersion = await model.GetTopLevelChangeTextVersionAsync(default);
        var changed = solution.WithDocumentText(modelId, SourceText.From(Model.Replace("=> 1", "=> 2")));
        Assert.Equal(initialDeclarationVersion, await changed.GetDocument(modelId)!.GetTopLevelChangeTextVersionAsync(default));

        var document = changed.GetDocument(consumerId)!;
        Assert.False(document.Project.TryGetCompilation(out _));
        var selected = await document.FreezeAsync(default);

        Assert.NotSame(document, selected);
        Assert.False(document.Project.TryGetCompilation(out _));
        Assert.Contains("=> 1", (await selected.Project.Solution.GetDocument(modelId)!.GetTextAsync()).ToString());
        Assert.Contains("=> 2", (await document.Project.Solution.GetDocument(modelId)!.GetTextAsync()).ToString());
    }

    [Fact]
    public async Task EditingTheRequestedMethodBodyUsesItsLatestTextWithoutAFullBind()
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var (solution, _, consumerId) = CreateSolution(workspace, referencedProject: false);
        await solution.GetDocument(consumerId)!.Project.GetCompilationAsync();
        string changedSource = Consumer.Replace("=> product", "=> product.VersionOne");
        var document = solution.WithDocumentText(consumerId, SourceText.From(changedSource)).GetDocument(consumerId)!;

        var selected = await document.FreezeAsync(default);

        Assert.NotSame(document, selected);
        Assert.False(document.Project.TryGetCompilation(out _));
        Assert.Equal(changedSource, (await selected.GetTextAsync()).ToString());
    }

    [Fact]
    public async Task ChangedParseOptionsCannotKeepDeclarationsFromTheOldConfiguration()
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var (solution, modelId, consumerId) = CreateSolution(workspace, referencedProject: false);
        solution = solution.WithDocumentText(modelId, SourceText.From("""
            public class Product {
            #if NEW_API
                public int VersionTwo { get; set; }
            #else
                public int VersionOne { get; set; }
            #endif
            }
            """));
        await solution.GetDocument(consumerId)!.Project.GetCompilationAsync();
        var project = solution.GetProject(consumerId.ProjectId)!;
        var changed = solution.WithProjectParseOptions(project.Id,
            ((CSharpParseOptions)project.ParseOptions!).WithPreprocessorSymbols("NEW_API"));

        var selected = await changed.GetDocument(consumerId)!.FreezeAsync(default);
        var compilation = await selected.Project.GetCompilationAsync();
        var product = compilation!.GetTypeByMetadataName("Product");
        Assert.NotEmpty(product!.GetMembers("VersionTwo"));
        Assert.Empty(product.GetMembers("VersionOne"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ChangedGeneratorInputsRemainCurrentWhenFreezingAnUneditedConsumer(
        bool referencedProject, bool analyzerConfiguration)
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var (solution, modelId, consumerId) = CreateSolution(workspace, referencedProject);
        var inputId = DocumentId.CreateNewId(modelId.ProjectId);
        solution = solution.RemoveDocument(modelId)
            .AddAnalyzerReference(modelId.ProjectId, new ProductGeneratorReference(analyzerConfiguration));
        solution = analyzerConfiguration
            ? solution.AddAnalyzerConfigDocument(inputId, ".globalconfig", GeneratorConfiguration("VersionOne"),
                filePath: Path.Combine(Path.GetTempPath(), "frozen-generator", ".globalconfig"))
            : solution.AddAdditionalDocument(inputId, "member.txt", SourceText.From("VersionOne"));
        var before = await solution.GetDocument(consumerId)!.Project.GetCompilationAsync();
        Assert.Single(before!.GetTypeByMetadataName("Product")!.GetMembers("VersionOne"));

        var changed = analyzerConfiguration
            ? solution.WithAnalyzerConfigDocumentText(inputId, GeneratorConfiguration("VersionTwo"))
            : solution.WithAdditionalDocumentText(inputId, SourceText.From("VersionTwo"));
        var document = changed.GetDocument(consumerId)!;
        Assert.False(document.Project.TryGetCompilation(out _));
        Assert.Same(await solution.GetDocument(consumerId)!.GetSyntaxTreeAsync(), await document.GetSyntaxTreeAsync());

        var selected = await document.FreezeAsync(default);

        // No C# declaration was edited, but freezing the previous generator inputs would
        // silently restore the old API when save/build next requests generation.
        Assert.Same(document, selected);
        var selectedInput = analyzerConfiguration
            ? selected.Project.Solution.GetAnalyzerConfigDocument(inputId)
            : selected.Project.Solution.GetAdditionalDocument(inputId);
        Assert.Contains("VersionTwo", (await selectedInput!.GetTextAsync()).ToString());

        // Balanced generator execution waits for this explicit save/build transition.
        Assert.True(workspace.TryApplyChanges(selected.Project.Solution));
        await workspace.ProcessUpdateSourceGeneratorRequestAsync(
            ImmutableSegmentedList.Create<(ProjectId?, bool)>((modelId.ProjectId, true)), default);
        var after = await workspace.CurrentSolution.GetDocument(consumerId)!.Project.GetCompilationAsync();
        var product = after!.GetTypeByMetadataName("Product")!;
        Assert.Single(product.GetMembers("VersionTwo"));
        Assert.Empty(product.GetMembers("VersionOne"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransitiveDependencyDocumentChangesCannotLeaveStaleDeclarations(bool removeDocument)
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var (solution, modelId, consumerId) = CreateSolution(workspace, referencedProject: true);
        var leafId = ProjectId.CreateNewId();
        var leafDocumentId = DocumentId.CreateNewId(leafId);
        const string declaration = "public class LeafContract { public int Value; }";
        solution = solution.AddProject(leafId, "Leaf", "Leaf", LanguageNames.CSharp)
            .WithProjectCompilationOptions(leafId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(leafId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddProjectReference(modelId.ProjectId, new ProjectReference(leafId));
        if (removeDocument)
            solution = solution.AddDocument(leafDocumentId, "LeafContract.cs", SourceText.From(declaration));
        await solution.GetDocument(consumerId)!.Project.GetCompilationAsync();
        var before = await solution.GetProject(leafId)!.GetCompilationAsync();
        Assert.Equal(removeDocument, before!.GetTypeByMetadataName("LeafContract") is not null);

        var changed = removeDocument
            ? solution.RemoveDocument(leafDocumentId)
            : solution.AddDocument(leafDocumentId, "LeafContract.cs", SourceText.From(declaration));
        var document = changed.GetDocument(consumerId)!;
        Assert.False(document.Project.TryGetCompilation(out _));
        var selected = await document.FreezeAsync(default);
        Assert.Same(document, selected);
        var after = await selected.Project.Solution.GetProject(leafId)!.GetCompilationAsync();

        Assert.Equal(!removeDocument, after!.GetTypeByMetadataName("LeafContract") is not null);
    }

    private static SourceText GeneratorConfiguration(string member) =>
        SourceText.From($"is_global = true\nbuild_property.GeneratedMember = {member}\n");

    private sealed class ProductGeneratorReference(bool analyzerConfiguration) : AnalyzerReference
    {
        private readonly ImmutableArray<ISourceGenerator> _generators =
            [new ProductGenerator(analyzerConfiguration).AsSourceGenerator()];
        public override string? FullPath => null;
        public override object Id => this;
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];
        public override ImmutableArray<ISourceGenerator> GetGenerators(string language) => _generators;
        public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages() => _generators;
    }

    private sealed class ProductGenerator(bool analyzerConfiguration) : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            if (analyzerConfiguration)
            {
                var member = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
                    options.GlobalOptions.TryGetValue("build_property.GeneratedMember", out var value) ? value : "MissingConfiguration");
                context.RegisterSourceOutput(member, static (output, name) => AddProduct(output, name));
            }
            else
            {
                var member = context.AdditionalTextsProvider.Select(static (text, ct) => text.GetText(ct)!.ToString());
                context.RegisterSourceOutput(member, static (output, name) => AddProduct(output, name));
            }
        }

        private static void AddProduct(SourceProductionContext output, string member) =>
            output.AddSource("Product.g.cs", $"public class Product {{ public int {member} {{ get; set; }} }}");
    }

    private static (Solution Solution, DocumentId Model, DocumentId Consumer) CreateSolution(
        AdhocWorkspace workspace, bool referencedProject)
    {
        var consumerProject = ProjectId.CreateNewId();
        var modelProject = referencedProject ? ProjectId.CreateNewId() : consumerProject;
        var reference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var solution = workspace.CurrentSolution
            .AddProject(consumerProject, "Consumer", "Consumer", LanguageNames.CSharp)
            .WithProjectCompilationOptions(consumerProject, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(consumerProject, reference);
        if (referencedProject)
        {
            solution = solution.AddProject(modelProject, "Contracts", "Contracts", LanguageNames.CSharp)
                .WithProjectCompilationOptions(modelProject, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddMetadataReference(modelProject, reference)
                .AddProjectReference(consumerProject, new ProjectReference(modelProject));
        }

        var modelId = DocumentId.CreateNewId(modelProject);
        var consumerId = DocumentId.CreateNewId(consumerProject);
        solution = solution.AddDocument(modelId, "Product.cs", SourceText.From(Model))
            .AddDocument(consumerId, "Consumer.cs", SourceText.From(Consumer))
            // Keep the existing never-built, single-tree fallback from deciding these tests.
            .AddDocument(DocumentId.CreateNewId(consumerProject), "Other.cs", SourceText.From("class Other {}"));
        return (solution, modelId, consumerId);
    }
}
