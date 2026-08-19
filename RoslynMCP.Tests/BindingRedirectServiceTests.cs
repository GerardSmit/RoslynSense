using System.Reflection;
using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Binding redirects against what a project actually ships.
/// </summary>
/// <remarks>
/// The comparison is metadata-driven, so the two halves worth pinning down separately are reading
/// the config (which has three ways to write a version range and one way to write "no key") and the
/// rewrite (which has to leave a document it did not need to touch alone).
/// </remarks>
public class BindingRedirectServiceTests
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
    public void ARedirectIsReadWithItsIdentityRangeAndLine()
    {
        string path = Write(Config);

        var redirects = BindingRedirectService.Read(path);

        var redirect = Assert.Single(redirects);
        Assert.Equal("Newtonsoft.Json", redirect.Name);
        Assert.Equal("30ad4fe6b2a6aeed", redirect.PublicKeyToken);
        Assert.Equal("neutral", redirect.Culture);
        Assert.Equal(new Version(0, 0, 0, 0), redirect.OldLow);
        Assert.Equal(new Version(12, 0, 0, 0), redirect.OldHigh);
        Assert.Equal(new Version(12, 0, 0, 0), redirect.NewVersion);

        // The line recorded is the <dependentAssembly> — the element a rewrite replaces — and not
        // the <assemblyIdentity> inside it that the assertions above read. 0-based, so the fifth
        // line of the document is 4.
        Assert.Equal(4, redirect.Line);
    }

    [Fact]
    public void AnOldVersionWithNoRangeIsASingleVersion()
    {
        string path = Write(Config.Replace(
            """oldVersion="0.0.0.0-12.0.0.0" """, """oldVersion="11.0.0.0" """));

        var redirect = Assert.Single(BindingRedirectService.Read(path));

        Assert.Equal(new Version(11, 0, 0, 0), redirect.OldLow);
        Assert.Equal(new Version(11, 0, 0, 0), redirect.OldHigh);
    }

    /// <summary>
    /// Reading from text is what the hover does, because the buffer it is answering about may
    /// never have been saved. It has to see exactly what reading the file sees.
    /// </summary>
    [Fact]
    public void TextAndFileAreReadTheSameWay()
    {
        var fromFile = Assert.Single(BindingRedirectService.Read(Write(Config)));
        var fromText = Assert.Single(BindingRedirectService.ReadText(Config));

        Assert.Equal(fromFile, fromText);
    }

    /// <summary>
    /// A config being typed into does not parse for as long as the tag is half-written, and a
    /// hover there answers nothing rather than throwing on the way past.
    /// </summary>
    [Fact]
    public void TextThatDoesNotParseReadsAsNoRedirects() =>
        Assert.Empty(BindingRedirectService.ReadText("<configuration><runtime>"));

    /// <summary>
    /// A config writes an unsigned assembly's token as the literal <c>null</c>, which has to read
    /// back as "no token" rather than as a token spelled n-u-l-l.
    /// </summary>
    [Fact]
    public void ANullPublicKeyTokenIsNoToken()
    {
        string path = Write(Config.Replace("30ad4fe6b2a6aeed", "null"));

        Assert.Null(Assert.Single(BindingRedirectService.Read(path)).PublicKeyToken);
    }

    [Fact]
    public void RewritingAStaleRedirectMovesBothVersions()
    {
        var finding = Stale("Newtonsoft.Json", "30ad4fe6b2a6aeed", "12.0.0.0", "13.0.0.0");

        var (text, applied) = BindingRedirectService.Rewrite(Config, [finding]);

        Assert.NotNull(text);
        Assert.Single(applied);
        Assert.Contains("""newVersion="13.0.0.0" """.TrimEnd(), text);

        // The range moves with it: a redirect that only names the new version stops catching the
        // versions it was written for.
        Assert.Contains("""oldVersion="0.0.0.0-13.0.0.0" """.TrimEnd(), text);
        Assert.DoesNotContain("12.0.0.0", text);
    }

    [Fact]
    public void AMissingRedirectIsAddedWithItsIdentity()
    {
        var finding = new BindingRedirectFinding(
            BindingRedirectProblem.Missing, "Contoso.Core", "abcdef0123456789", "neutral",
            null, "2.0.0.0", "", -1);

        var (text, applied) = BindingRedirectService.Rewrite(Config, [finding]);

        Assert.NotNull(text);
        Assert.Single(applied);
        Assert.Contains("""name="Contoso.Core" """.TrimEnd(), text);
        Assert.Contains("""publicKeyToken="abcdef0123456789" """.TrimEnd(), text);
        Assert.Contains("""newVersion="2.0.0.0" """.TrimEnd(), text);

        // The redirect that was already there is untouched.
        Assert.Contains("Newtonsoft.Json", text);
    }

    /// <summary>
    /// A redirect can only bind a strong name, so proposing one for an unsigned assembly would
    /// write a section the runtime ignores.
    /// </summary>
    [Fact]
    public void AnUnsignedAssemblyIsNeverRewritten()
    {
        var finding = new BindingRedirectFinding(
            BindingRedirectProblem.Missing, "Contoso.Loose", null, "neutral",
            null, "2.0.0.0", "", -1);

        var (text, applied) = BindingRedirectService.Rewrite(Config, [finding]);

        Assert.Null(text);
        Assert.Empty(applied);
    }

    /// <summary>
    /// An orphan is reported and not repaired: nothing is broken by a redirect for an assembly that
    /// is no longer shipped, and removing one is a guess about intent.
    /// </summary>
    [Fact]
    public void AnOrphanIsLeftAlone()
    {
        var finding = new BindingRedirectFinding(
            BindingRedirectProblem.Orphan, "Newtonsoft.Json", "30ad4fe6b2a6aeed", "neutral",
            "12.0.0.0", "", "", 5);

        var (text, _) = BindingRedirectService.Rewrite(Config, [finding]);

        Assert.Null(text);
    }

    [Fact]
    public void AConfigWithNoRuntimeSectionGrowsOne()
    {
        string bare = """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <appSettings />
            </configuration>
            """;

        var (text, _) = BindingRedirectService.Rewrite(
            bare, [Stale("Contoso.Core", "abcdef0123456789", null, "2.0.0.0")]);

        Assert.NotNull(text);
        Assert.Contains("<runtime>", text);
        Assert.Contains("urn:schemas-microsoft-com:asm.v1", text);
        Assert.Contains("<appSettings />", text);
    }

    [Fact]
    public void MalformedXmlIsReportedAsNothingToDo()
    {
        var (text, applied) = BindingRedirectService.Rewrite(
            "<configuration>", [Stale("Contoso.Core", "abcdef0123456789", null, "2.0.0.0")]);

        Assert.Null(text);
        Assert.Empty(applied);
    }

    /// <summary>
    /// The token a config names is the last eight bytes of the key's SHA-1, reversed — never the
    /// key itself. Read from a real assembly, because a hand-rolled expectation would only test the
    /// arithmetic against itself.
    /// </summary>
    [Fact]
    public void AStrongNamedAssemblyReportsItsPublicKeyToken()
    {
        string path = typeof(object).Assembly.Location;

        var info = AssemblyIdentityReader.Read(path);

        Assert.NotNull(info);
        Assert.Equal("System.Private.CoreLib", info!.Identity.Name);
        Assert.Equal("7cec85d7bea7798e", info.Identity.PublicKeyToken);
        Assert.Equal("neutral", info.Identity.Culture);
    }

    /// <summary>
    /// Identities are memoized per file so a <c>packages</c> folder is not re-parsed on every
    /// request. The one way that can go wrong is an assembly that changed and was not re-read —
    /// which is the case right after a build, and the case this whole feature exists to catch.
    /// </summary>
    [Fact]
    public void AnAssemblyRebuiltUnderTheSamePathIsReadAgain()
    {
        string path = Path.Combine(Path.GetTempPath(), $"binding-{Guid.NewGuid():N}.dll");

        File.Copy(typeof(BindingRedirectServiceTests).Assembly.Location, path);
        string? first = BindingRedirectService.Identity(path)?.Identity.Name;

        // A different assembly at the same path, which is what a rebuild looks like from here.
        File.Copy(typeof(BindingRedirectService).Assembly.Location, path, overwrite: true);
        string? second = BindingRedirectService.Identity(path)?.Identity.Name;

        Assert.Equal("RoslynMCP.Tests", first);
        Assert.Equal("RoslynMCP", second);
    }

    /// <summary>The same file, unchanged, reads back the same identity rather than a stale
    /// null.</summary>
    [Fact]
    public void AnUnchangedAssemblyReadsTheSameIdentityTwice()
    {
        string path = typeof(BindingRedirectService).Assembly.Location;

        Assert.Equal(
            BindingRedirectService.Identity(path)?.Identity,
            BindingRedirectService.Identity(path)?.Identity);
    }

    [Fact]
    public void AFileThatIsNotAnAssemblyIsSkipped()
    {
        string path = Write("not a PE file");

        Assert.Null(AssemblyIdentityReader.Read(path));
    }

    [Fact]
    public void AnAssemblysReferencesCarryTheVersionItWasBuiltAgainst()
    {
        var info = AssemblyIdentityReader.Read(typeof(BindingRedirectServiceTests).Assembly.Location);

        Assert.NotNull(info);
        Assert.NotEmpty(info!.References);

        // That a version was read at all, rather than that it is 1.0 or later. A project reference
        // is a reference like any other and this assembly has one — RoslynMCP itself, at 0.1.0.0 —
        // so asserting a non-zero major says the product is broken whenever a solution has not
        // reached 1.0, which is most of them.
        Assert.All(info.References, reference => Assert.NotEqual(new Version(0, 0, 0, 0), reference.Version));
    }

    private static BindingRedirectFinding Stale(
        string name, string token, string? configured, string required) =>
        new(BindingRedirectProblem.Stale, name, token, "neutral", configured, required, "", 5);

    private static string Write(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"binding-{Guid.NewGuid():N}.config");
        File.WriteAllText(path, content);
        return path;
    }
}
