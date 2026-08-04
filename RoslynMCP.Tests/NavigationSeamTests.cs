using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using Xunit;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Tests;

/// <summary>
/// The redirect seam itself, with a pack that exists only here.
/// </summary>
/// <remarks>
/// <para>
/// A fake rather than a real pack on purpose. What is under test is the contract — replace when a
/// pack answers, fall through when it does not, and do not re-map what it named — and none of that
/// should be reachable only through one pack's idea of what a dispatch looks like. It also means
/// this file passes before any engine exists, so a failure later is attributable to the matching or
/// to the plumbing but not to both at once.
/// </para>
/// <para>
/// <c>Calculator.Add</c> and <c>Calculator.Subtract</c> stand in for a dispatcher and its handler:
/// the caret is on a call to one, and the answer has to be the other.
/// </para>
/// </remarks>
[Collection(SharedState.Name)]
public class NavigationSeamTests
{
    /// <summary>1-based line 5 of Calculator.cs, `public int Add`.</summary>
    private const int AddDeclarationLine = 4;

    /// <summary>1-based line 7 of Calculator.cs, `public int Subtract`.</summary>
    private const int SubtractDeclarationLine = 6;

    /// <summary>
    /// A pack owning no files, which is the shape every C#-to-C# pack has: it redirects a caret on
    /// one method to another, and separately records which symbol the append contributors were
    /// asked about.
    /// </summary>
    private sealed class FakePack(string from, string? to) :
        ILanguagePack, ILanguageDefinitionRedirector, ILanguageDefinitionContributor
    {
        /// <summary>The symbols the append pass was asked about, in order.</summary>
        public List<string> AskedAbout { get; } = [];

        public string Id => "fake-redirect";

        public string DisplayName => "Fake";

        public ImmutableArray<string> FileExtensions { get; } = [];

        public LanguageCapabilities Capabilities => LanguageCapabilities.None;

        public ImmutableArray<string> WellKnownTypeNames { get; } = [];

        public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } = [];

        public bool IsProjectionPath(string? filePath) => false;

        public async Task<IReadOnlyList<ISymbol>> RedirectAsync(
            NavigationContext context, CancellationToken ct)
        {
            if (to is null || context.Symbol.Name != from)
                return [];

            var compilation = await context.Document.Project.GetCompilationAsync(ct);
            var calculator = compilation?.GetTypeByMetadataName("SampleProject.Calculator");
            return calculator is null ? [] : [.. calculator.GetMembers(to)];
        }

        public Task<IReadOnlyList<LspLocation>> DefinitionsAsync(
            ISymbol symbol, Project project, CancellationToken ct)
        {
            AskedAbout.Add(symbol.Name);
            return Task.FromResult<IReadOnlyList<LspLocation>>([]);
        }
    }

    private static TextDocumentPositionParams AtTheAddCallSite()
    {
        string path = FixturePaths.CalculatorFile;
        string text = File.ReadAllText(path);

        int index = text.IndexOf("Add(a, b), Subtract", StringComparison.Ordinal);
        Assert.True(index >= 0, "the Add call site is not in Calculator.cs");

        var position = SourceText.From(text).Lines.GetLinePosition(index);
        return new TextDocumentPositionParams(
            new TextDocumentIdentifier(LspConverters.PathToUri(path)),
            new Position(position.Line, position.Character));
    }

    private static int SoleCalculatorLine(LspLocation[] locations)
    {
        var location = Assert.Single(locations);
        Assert.EndsWith(
            "Calculator.cs", LspConverters.UriToPath(location.Uri), StringComparison.OrdinalIgnoreCase);
        return location.Range.Start.Line;
    }

    [Fact]
    public async Task ARedirectReplacesRoslynsAnswer()
    {
        var locations = await NavigationHandlers.DefinitionAsync(
            AtTheAddCallSite(), typeDefinition: false, default,
            new LanguageSession([new FakePack("Add", "Subtract")]));

        // Replaced, not appended: the method the caret is literally on must not be offered, or the
        // editor shows the pick-one-of-two list the redirect exists to remove.
        Assert.Equal(SubtractDeclarationLine, SoleCalculatorLine(locations));
    }

    [Fact]
    public async Task AnEmptyRedirectFallsThroughToRoslyn()
    {
        var locations = await NavigationHandlers.DefinitionAsync(
            AtTheAddCallSite(), typeDefinition: false, default,
            new LanguageSession([new FakePack("Add", to: null)]));

        Assert.Equal(AddDeclarationLine, SoleCalculatorLine(locations));
    }

    [Fact]
    public async Task APackOutsideTheSessionDoesNotRedirect()
    {
        var locations = await NavigationHandlers.DefinitionAsync(
            AtTheAddCallSite(), typeDefinition: false, default, LanguageSession.Empty);

        Assert.Equal(AddDeclarationLine, SoleCalculatorLine(locations));
    }

    [Fact]
    public async Task ARedirectedMethodIsNotMappedToItsReturnType()
    {
        var locations = await NavigationHandlers.DefinitionAsync(
            AtTheAddCallSite(), typeDefinition: true, default,
            new LanguageSession([new FakePack("Add", "Subtract")]));

        // The redirect has already chosen between the member and the type declaring it. Passing
        // typeDefinition on to DefinitionLocationsAsync would map the method it named to `int` and
        // land the caret in the framework instead.
        Assert.Equal(SubtractDeclarationLine, SoleCalculatorLine(locations));
    }

    [Fact]
    public async Task ImplementationUsesTheSameSeamAsDefinition()
    {
        var caret = AtTheAddCallSite();
        var session = new LanguageSession([new FakePack("Add", "Subtract")]);

        var definition = await NavigationHandlers.DefinitionAsync(
            caret, typeDefinition: false, default, session);
        var implementation = await NavigationHandlers.ImplementationAsync(caret, default, session);

        // Two verbs disagreeing about one caret is worse than either answer alone, and without the
        // seam Ctrl+F12 here lands back on the caret it started from.
        Assert.Equal(definition, implementation);
    }

    [Fact]
    public async Task TheAppendPassIsAskedAboutTheRedirectedSymbol()
    {
        var pack = new FakePack("Add", "Subtract");

        await NavigationHandlers.DefinitionAsync(
            AtTheAddCallSite(), typeDefinition: false, default, new LanguageSession([pack]));

        // What makes the two seams compose: a handler a generator produced still reaches the
        // declaration behind it, because the append pass runs on what the redirect named.
        Assert.Equal(["Subtract"], pack.AskedAbout);
    }
}
