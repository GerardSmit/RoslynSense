using RoslynMCP.Lsp.Handlers;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Documents with no file behind them: source-generated output, which exists only inside the
/// compilation, and decompiled metadata.
/// </summary>
public class VirtualDocumentTests
{
    [Fact]
    public void AUriSurvivesTheRoundTripWithAWindowsPathInIt()
    {
        // The owner is a full path with a drive letter and separators; putting it in the URI's
        // path would let the client's parser rewrite it.
        string uri = VirtualDocumentHandler.UriFor(
            VirtualDocumentHandler.GeneratedScheme, @"C:\src\App\App.csproj", "Gen.g.cs");

        Assert.StartsWith("roslynsense-generated:/", uri);
        Assert.DoesNotContain(@"C:\src", uri);
    }

    [Fact]
    public async Task AnUnknownSchemeResolvesToNothing() =>
        Assert.Null(await VirtualDocumentHandler.ResolveAsync(
            new VirtualDocumentParams("file:///C:/src/App/Program.cs"), default));

    [Fact]
    public async Task AMalformedUriResolvesToNothingRatherThanThrowing() =>
        Assert.Null(await VirtualDocumentHandler.ResolveAsync(
            new VirtualDocumentParams("roslynsense-generated:no-owner"), default));

    [Fact]
    public async Task GeneratedFilesAreListedWithTheGeneratorThatMadeThem()
    {
        var files = await VirtualDocumentHandler.ListGeneratedAsync(
            FixturePaths.SourceGenConsumerProjectFile, default);

        Assert.NotEmpty(files);
        Assert.All(files, file =>
        {
            Assert.False(string.IsNullOrWhiteSpace(file.HintName));
            Assert.False(string.IsNullOrWhiteSpace(file.Generator));
            Assert.StartsWith("roslynsense-generated:/", file.Uri);
        });
    }

    [Fact]
    public async Task AGeneratedDocumentResolvesToItsSource()
    {
        var files = await VirtualDocumentHandler.ListGeneratedAsync(
            FixturePaths.SourceGenConsumerProjectFile, default);
        Assert.NotEmpty(files);

        var document = await VirtualDocumentHandler.ResolveAsync(
            new VirtualDocumentParams(files[0].Uri), default);

        Assert.NotNull(document);
        Assert.False(string.IsNullOrWhiteSpace(document!.Text));
        // The reader has to be told why this file has no path.
        Assert.Contains("read-only", document.Description);
        Assert.Equal("csharp", document.LanguageId);
    }

    [Fact]
    public async Task AProjectWithNoGeneratorsListsNothingRatherThanFailing() =>
        Assert.Empty(await VirtualDocumentHandler.ListGeneratedAsync(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.csproj"), default));
}
