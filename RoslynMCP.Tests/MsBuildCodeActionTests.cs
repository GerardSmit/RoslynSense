using System.Collections.Immutable;
using NuGet.Versioning;
using RoslynMCP.Languages.MsBuild.Core;
using RoslynMCP.Languages.MsBuild.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using Xunit;
using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Tests;

/// <summary>
/// The fixes on an outdated reference, and — the reason the parser was chosen — that applying one
/// changes the version and nothing else.
/// </summary>
[Collection(SharedState.Name)]
public class MsBuildCodeActionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "roslynsense-msbfix-" + Guid.NewGuid().ToString("N")[..8]);

    public MsBuildCodeActionTests()
    {
        PackageStatusCache.Clear();
        MsBuildDocumentCache.Clear();
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        PackageStatusCache.Clear();

        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private static PackageStatus Status(params string[] versions) =>
        new([.. versions.Select(NuGetVersion.Parse)], true, [], null, true, DateTime.UtcNow);

    private string Write(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        MsBuildDocumentCache.Invalidate(path);
        return path;
    }

    /// <summary>Asks for actions over the whole file.</summary>
    private static CodeAction[] Actions(string path)
    {
        string text = File.ReadAllText(path);
        var lines = Microsoft.CodeAnalysis.Text.SourceText.From(text).Lines;
        var whole = LspConverters.ToRange(
            lines, new Microsoft.CodeAnalysis.Text.TextSpan(0, text.Length));

        return MsBuildCodeActionHandler.Compute(new CodeActionParams(
            new TextDocumentIdentifier(LspConverters.PathToUri(path)),
            whole,
            new CodeActionContext([])));
    }

    /// <summary>Applies a workspace edit to the text it addresses.</summary>
    private static string Apply(string text, WorkspaceEdit edit)
    {
        var source = Microsoft.CodeAnalysis.Text.SourceText.From(text);
        var edits = edit.Changes.Values.Single()
            .OrderByDescending(e => e.Range.Start.Line)
            .ThenByDescending(e => e.Range.Start.Character);

        foreach (var one in edits)
        {
            int start = source.Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(
                one.Range.Start.Line, one.Range.Start.Character));
            int end = source.Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(
                one.Range.End.Line, one.Range.End.Character));

            text = text.Remove(start, end - start).Insert(start, one.NewText);
            source = Microsoft.CodeAnalysis.Text.SourceText.From(text);
        }

        return text;
    }

    [Fact]
    public void AReferenceBehindOnEveryAxisOffersThreeDistinctUpgrades()
    {
        string path = Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        PackageStatusCache.Seed("Serilog", "1.0.0", Status("1.0.0", "1.0.5", "1.4.0", "3.1.1"));

        var titles = Actions(path).Select(a => a.Title).ToList();

        Assert.Contains("Update Serilog to 1.0.5 (patch)", titles);
        Assert.Contains("Update Serilog to 1.4.0 (minor)", titles);
        Assert.Contains("Update Serilog to 3.1.1 (major)", titles);
    }

    /// <summary>
    /// One patch behind, the three answers coincide — and offering the same version three times
    /// under three labels is three ways to spell one fix.
    /// </summary>
    [Fact]
    public void CoincidingUpgradesAreOfferedOnce()
    {
        string path = Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        PackageStatusCache.Seed("Serilog", "1.0.0", Status("1.0.0", "1.0.1"));

        var upgrades = Actions(path).Where(a => a.Title.StartsWith("Update Serilog", StringComparison.Ordinal));

        Assert.Equal("Update Serilog to 1.0.1 (patch)", Assert.Single(upgrades).Title);
    }

    /// <summary>
    /// The whole reason for a full-fidelity parse: the edit replaces the version and leaves the
    /// alignment, the attribute order and the comment exactly as they were.
    /// </summary>
    [Fact]
    public void ApplyingAFixChangesTheVersionAndNothingElse()
    {
        const string original = """
            <Project Sdk="Microsoft.NET.Sdk">
              <!-- pinned deliberately, see #142 -->
              <ItemGroup>
                <PackageReference Include="Serilog"   Version="1.0.0"  PrivateAssets="all" />
                <PackageReference Include="Other" Version="9.9.9" />
              </ItemGroup>
            </Project>
            """;

        string path = Write("App.csproj", original);
        PackageStatusCache.Seed("Serilog", "1.0.0", Status("1.0.0", "1.0.1"));
        PackageStatusCache.Seed("Other", "9.9.9", Status("9.9.9"));

        var fix = Assert.Single(
            Actions(path),
            a => a.Title == "Update Serilog to 1.0.1 (patch)");

        Assert.Equal(original.Replace("1.0.0", "1.0.1"), Apply(original, fix.Edit!));
    }

    [Fact]
    public void FixAllRewritesEveryOutdatedReferenceAndTheResultStillParses()
    {
        const string original = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
                <PackageReference Include="Newtonsoft.Json" Version="12.0.1" />
              </ItemGroup>
            </Project>
            """;

        string path = Write("App.csproj", original);
        PackageStatusCache.Seed("Serilog", "1.0.0", Status("1.0.0", "3.1.1"));
        PackageStatusCache.Seed("Newtonsoft.Json", "12.0.1", Status("12.0.1", "13.0.3"));

        var all = Assert.Single(Actions(path), a => a.Title.StartsWith("Update all", StringComparison.Ordinal));
        Assert.Contains("2 outdated", all.Title, StringComparison.Ordinal);

        string updated = Apply(original, all.Edit!);

        Assert.Contains("Version=\"3.1.1\"", updated, StringComparison.Ordinal);
        Assert.Contains("Version=\"13.0.3\"", updated, StringComparison.Ordinal);

        // Still a project file afterwards.
        var reparsed = MsBuildDocumentCache.For(path, Microsoft.CodeAnalysis.Text.SourceText.From(updated));
        Assert.Equal(2, MsBuildPackageReader.Read(reparsed).Length);
    }

    [Fact]
    public void FixAllIsNotOfferedForASingleOutdatedReference()
    {
        string path = Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        PackageStatusCache.Seed("Serilog", "1.0.0", Status("1.0.0", "1.0.1"));

        Assert.DoesNotContain(Actions(path), a => a.Title.StartsWith("Update all", StringComparison.Ordinal));
    }

    /// <summary>
    /// The literal shape of a contract with TypeScript that no compiler checks: the extension
    /// registers this command taking a node whose id is <c>project:&lt;path&gt;</c> and a package to
    /// select.
    /// </summary>
    [Fact]
    public void TheNuGetPanelActionCarriesTheCommandTheExtensionRegisters()
    {
        string path = Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        PackageStatusCache.Seed("Serilog", "1.0.0", Status("1.0.0", "1.0.1"));

        var action = Assert.Single(Actions(path), a => a.Title.Contains("NuGet panel", StringComparison.Ordinal));
        var command = action.Command!;

        Assert.Equal("roslynSense.manageNuGetForProject", command.Name);
        Assert.Equal(2, command.Arguments!.Length);
        Assert.Equal("Serilog", command.Arguments[1]);

        string id = command.Arguments[0]!.GetType().GetProperty("id")!.GetValue(command.Arguments[0])!.ToString()!;
        Assert.StartsWith("project:", id, StringComparison.Ordinal);
        Assert.EndsWith("App.csproj", id, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsOfferedWhenTheFeedsDidNotAnswer()
    {
        string path = Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        PackageStatusCache.Seed("Serilog", "1.0.0", new PackageStatus(
            [NuGetVersion.Parse("3.0.0")], true, [], null, FeedsHealthy: false, DateTime.UtcNow));

        Assert.DoesNotContain(Actions(path), a => a.Title.StartsWith("Update Serilog", StringComparison.Ordinal));
    }
}
