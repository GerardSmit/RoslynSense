using System.Text.Json;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The lens above a config file, and what clicking it does.
/// </summary>
/// <remarks>
/// The number on the lens and the number the fix reaches are the same number, and these are what
/// keep them that way: the count is taken from the findings a rewrite can resolve, not from every
/// finding reported, because the file also carries hints for redirects nothing repairs.
/// </remarks>
public class BindingRedirectLensTests
{
    private const string Config = """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <runtime>
            <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
              <dependentAssembly>
                <assemblyIdentity name="Newtonsoft.Json" publicKeyToken="30ad4fe6b2a6aeed" culture="neutral" />
                <bindingRedirect oldVersion="0.0.0.0-12.0.0.0" newVersion="12.0.0.0" />
              </dependentAssembly>
            </assemblyBinding>
          </runtime>
        </configuration>
        """;

    [Fact]
    public void ThereIsNoLensWhenEveryRedirectIsRight() =>
        Assert.Empty(BindingRedirectHandler.Lenses("web.config", []));

    /// <summary>
    /// Above line one, so it reads as a statement about the file rather than about whatever
    /// element happens to be first.
    /// </summary>
    [Fact]
    public void TheLensSitsAtTheTopOfTheFileAndCarriesItsPath()
    {
        var lens = Assert.Single(BindingRedirectHandler.Lenses("web.config", [Stale("Newtonsoft.Json")]));

        Assert.Equal(0, lens.Range.Start.Line);
        Assert.Equal(0, lens.Range.Start.Character);
        Assert.Equal(0, lens.Range.End.Line);

        Assert.Equal("1 binding redirect out of date — fix it", lens.Command!.Title);
        Assert.Equal(ExecuteCommandHandler.FixBindingRedirectsCommand, lens.Command.Name);
        Assert.Equal("web.config", Assert.Single(lens.Command.Arguments!));
    }

    /// <summary>
    /// One lens whatever the count — the per-redirect story is the squiggle's, and this is the
    /// only place the total is said.
    /// </summary>
    [Fact]
    public void ManyStaleRedirectsAreStillOneLens()
    {
        var lens = Assert.Single(BindingRedirectHandler.Lenses(
            "web.config", [Stale("Newtonsoft.Json"), Stale("System.Buffers"), Stale("System.Memory")]));

        Assert.Equal("3 binding redirects out of date — fix them all", lens.Command!.Title);
    }

    /// <summary>
    /// An orphan is reported and repaired by nothing, so it must not raise the count — a lens
    /// offering to fix one redirect that then fixes none is worse than no lens.
    /// </summary>
    [Fact]
    public void FindingsNothingRepairsDoNotCount()
    {
        var orphan = new BindingRedirectFinding(
            BindingRedirectProblem.Orphan, "Gone", "30ad4fe6b2a6aeed", "neutral", "1.0.0.0", "", "", 5);

        var noOp = new BindingRedirectFinding(
            BindingRedirectProblem.NoOp, "Unsigned", null, "neutral", "1.0.0.0", "", "", 6);

        Assert.Empty(BindingRedirectHandler.Lenses("web.config", [orphan, noOp]));

        var lens = Assert.Single(BindingRedirectHandler.Lenses(
            "web.config", [orphan, noOp, Stale("Newtonsoft.Json")]));

        Assert.Equal("1 binding redirect out of date — fix it", lens.Command!.Title);
    }

    [Fact]
    public async Task ClickingTheLensRewritesTheFileAndSaysWhatItDid()
    {
        string path = Write(Config);

        string message = await BindingRedirectHandler.ApplyAsync(
            path, [Stale("Newtonsoft.Json")], CancellationToken.None);

        Assert.Equal("Redirected Newtonsoft.Json to 13.0.0.0.", message);

        string rewritten = await File.ReadAllTextAsync(path, CancellationToken.None);
        Assert.Contains("newVersion=\"13.0.0.0\"", rewritten);
        Assert.Contains("oldVersion=\"0.0.0.0-13.0.0.0\"", rewritten);
    }

    [Fact]
    public async Task AFileWithNothingToFixIsLeftExactlyAsItWas()
    {
        string path = Write(Config);

        string message = await BindingRedirectHandler.ApplyAsync(
            path, [], CancellationToken.None);

        Assert.Equal("Every binding redirect already names what ships.", message);
        Assert.Equal(Config, await File.ReadAllTextAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task TheCountInTheMessageIsWhatWasApplied()
    {
        string path = Write(Config);

        string message = await BindingRedirectHandler.ApplyAsync(
            path,
            [Stale("Newtonsoft.Json"), Stale("System.Buffers")],
            CancellationToken.None);

        Assert.Equal("Updated 2 binding redirects.", message);
    }

    /// <summary>
    /// The lens carries the path as its one argument, and the command is what turns that back into
    /// a file. Both halves are checked here because nothing else joins them: the lens is built in
    /// one place and dispatched in another, and a mismatch would only ever show up as a click that
    /// does nothing.
    /// </summary>
    [Fact]
    public async Task TheCommandTakesTheConfigPathTheLensPutInIt()
    {
        var lens = Assert.Single(BindingRedirectHandler.Lenses(
            Write(Config), [Stale("Newtonsoft.Json")]));

        var result = await ExecuteCommandHandler.ExecuteAsync(
            new ExecuteCommandParams(lens.Command!.Name, Arguments(lens.Command.Arguments!)),
            CancellationToken.None);

        // Nothing sits in the config file's own directory, which is the point: the path arrived,
        // was recognised as this command's, and got as far as looking for the project that owns it.
        Assert.Equal("No project sits beside this config file.", result);
    }

    [Fact]
    public async Task TheCommandWithNoPathSaysSoRatherThanThrowing()
    {
        var result = await ExecuteCommandHandler.ExecuteAsync(
            new ExecuteCommandParams(ExecuteCommandHandler.FixBindingRedirectsCommand, null),
            CancellationToken.None);

        Assert.Equal("No config file to fix binding redirects in.", result);
    }

    /// <summary>
    /// The squiggle is on the value that is wrong, not at the start of the element two lines
    /// above it.
    /// </summary>
    [Fact]
    public void ADiagnosticSitsOnTheTextTheFindingIsAbout()
    {
        var finding = new BindingRedirectFinding(
            BindingRedirectProblem.Stale, "Newtonsoft.Json", "30ad4fe6b2a6aeed", "neutral",
            "12.0.0.0", "13.0.0.0", "out of date", 4, new ConfigSpan(6, 55, 6, 63));

        var diagnostic = Assert.Single(BindingRedirectHandler.ToDiagnostics(
            new BindingRedirectReport("Contoso.csproj", "web.config", [finding])));

        Assert.Equal(6, diagnostic.Range.Start.Line);
        Assert.Equal(55, diagnostic.Range.Start.Character);
        Assert.Equal(6, diagnostic.Range.End.Line);
        Assert.Equal(63, diagnostic.Range.End.Character);
    }

    /// <summary>
    /// A finding the document could not be read precisely enough to place still lands on its
    /// line rather than on nothing.
    /// </summary>
    [Fact]
    public void ADiagnosticWithNoSpanFallsBackToItsLine()
    {
        var diagnostic = Assert.Single(BindingRedirectHandler.ToDiagnostics(
            new BindingRedirectReport("Contoso.csproj", "web.config", [Stale("Newtonsoft.Json")])));

        Assert.Equal(4, diagnostic.Range.Start.Line);
        Assert.Equal(0, diagnostic.Range.Start.Character);
    }

    private static JsonElement[] Arguments(object[] arguments) =>
        JsonSerializer.Deserialize<JsonElement[]>(JsonSerializer.Serialize(arguments))!;

    private static BindingRedirectFinding Stale(string name) =>
        new(BindingRedirectProblem.Stale, name, "30ad4fe6b2a6aeed", "neutral", "12.0.0.0", "13.0.0.0", "", 4);

    /// <summary>The config file, in a directory of its own.</summary>
    /// <remarks>
    /// Its own directory rather than the temp root, because the command looks for the project
    /// <em>beside</em> the config file. Any test anywhere in the suite that names a project path
    /// directly under the temp root — several do, to check what happens to a project that is not
    /// there — puts one beside this file, and the command then answers about that project instead.
    /// </remarks>
    private static string Write(string content)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "roslyn-sense-tests", $"binding-lens-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, "web.config");
        File.WriteAllText(path, content);
        return path;
    }
}
