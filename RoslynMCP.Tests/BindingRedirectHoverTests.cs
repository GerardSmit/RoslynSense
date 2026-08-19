using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Which position in a config file is an assembly name worth answering for.
/// </summary>
/// <remarks>
/// The hover's own body is a metadata read of a built project, so what is pinned here is the part
/// that decides whether to do it at all: a text scan that has to survive a document mid-edit, and
/// has to stay off every other <c>name=</c> in a file full of them.
/// </remarks>
public class BindingRedirectHoverTests
{
    private const string Identity =
        """          <assemblyIdentity name="Newtonsoft.Json" publicKeyToken="30ad4fe6b2a6aeed" />""";

    private static readonly int s_nameStart = Identity.IndexOf("Newtonsoft.Json", StringComparison.Ordinal);

    [Fact]
    public void TheNameOfAnAssemblyIdentityIsAnswered()
    {
        var hit = BindingRedirectHandler.IdentityNameAt(Identity, new Position(0, s_nameStart + 3));

        Assert.NotNull(hit);
        Assert.Equal("Newtonsoft.Json", hit!.Value.Name);
        Assert.Equal(s_nameStart, hit.Value.Range.Start.Character);
        Assert.Equal(s_nameStart + "Newtonsoft.Json".Length, hit.Value.Range.End.Character);
    }

    /// <summary>
    /// The cursor on the attribute's name, or on the token beside it, is not the cursor on the
    /// assembly — a hover there would cover text it has nothing to say about.
    /// </summary>
    [Fact]
    public void APositionOutsideTheNameValueIsNotAnswered()
    {
        Assert.Null(BindingRedirectHandler.IdentityNameAt(Identity, new Position(0, s_nameStart - 8)));
        Assert.Null(BindingRedirectHandler.IdentityNameAt(
            Identity, new Position(0, Identity.IndexOf("30ad4fe6", StringComparison.Ordinal))));
    }

    /// <summary>
    /// A config file is full of <c>name=</c> — every <c>add</c> in <c>appSettings</c>, every
    /// module and handler — and none of those is an assembly.
    /// </summary>
    [Fact]
    public void ANameOnSomethingElseIsNotAnAssembly()
    {
        const string add = """  <add name="Default" connectionString="..." />""";

        Assert.Null(BindingRedirectHandler.IdentityNameAt(
            add, new Position(0, add.IndexOf("Default", StringComparison.Ordinal))));
    }

    /// <summary>
    /// Hand-formatted markup puts the attribute on its own line, and the element name is then one
    /// line up.
    /// </summary>
    [Fact]
    public void AWrappedIdentityIsFoundFromTheLineAbove()
    {
        const string wrapped = "<assemblyIdentity\n    name=\"Newtonsoft.Json\"\n    culture=\"neutral\" />";

        var hit = BindingRedirectHandler.IdentityNameAt(wrapped, new Position(1, 12));

        Assert.NotNull(hit);
        Assert.Equal("Newtonsoft.Json", hit!.Value.Name);
        Assert.Equal(1, hit.Value.Range.Start.Line);
    }

    /// <summary>
    /// The line above opens an element and closes it again, so the next line's <c>name=</c>
    /// belongs to whatever comes after — not to the identity that has already ended.
    /// </summary>
    [Fact]
    public void AnAttributeAfterAClosedIdentityIsNotItsOwn()
    {
        const string after = "<assemblyIdentity name=\"Newtonsoft.Json\" />\n<add name=\"Default\" />";

        Assert.Null(BindingRedirectHandler.IdentityNameAt(after, new Position(1, 12)));
    }

    /// <summary>
    /// A config that binds the assembly namespace to a prefix rather than as the default is the
    /// same markup, and the hover has to read it as such.
    /// </summary>
    [Fact]
    public void APrefixedIdentityIsStillAnIdentity()
    {
        const string prefixed = """<asm:assemblyIdentity name="Newtonsoft.Json" />""";

        var hit = BindingRedirectHandler.IdentityNameAt(
            prefixed, new Position(0, prefixed.IndexOf("Newtonsoft", StringComparison.Ordinal)));

        Assert.NotNull(hit);
        Assert.Equal("Newtonsoft.Json", hit!.Value.Name);
    }

    [Fact]
    public void APositionPastTheEndOfTheDocumentIsNotAnswered() =>
        Assert.Null(BindingRedirectHandler.IdentityNameAt(Identity, new Position(9, 0)));

    /// <summary>
    /// The version that ships, which is the whole reason to hover: it is the one fact in the file
    /// that reading the file cannot establish.
    /// </summary>
    [Fact]
    public void TheInstalledVersionAndWhereItCameFromAreShown()
    {
        string path = Path.Combine("bin", "Newtonsoft.Json.dll");

        string markdown = BindingRedirectHandler.HoverMarkdown(
            "Newtonsoft.Json", new Version(13, 0, 0, 0), path, new Version(13, 0, 0, 0));

        Assert.Contains("**Newtonsoft.Json**", markdown);
        Assert.Contains("Installed: `13.0.0.0`", markdown);
        Assert.Contains(path, markdown);

        // Nothing about the redirect, because it names what ships. Saying so anyway would make
        // every correct redirect look like it had something to answer for.
        Assert.DoesNotContain("redirect", markdown);
    }

    [Fact]
    public void ARedirectNamingSomethingElseIsCalledOut()
    {
        string markdown = BindingRedirectHandler.HoverMarkdown(
            "Newtonsoft.Json", new Version(13, 0, 0, 0), null, new Version(12, 0, 0, 0));

        Assert.Contains("Installed: `13.0.0.0`", markdown);
        Assert.Contains("The redirect names `12.0.0.0`", markdown);
    }

    /// <summary>
    /// A redirect for something the project does not ship. "Installed: nothing" would be the
    /// literal answer and the useless one; what the reader needs is that the element is inert.
    /// </summary>
    [Fact]
    public void AnAssemblyThatShipsNowhereSaysTheRedirectDoesNothing()
    {
        string markdown = BindingRedirectHandler.HoverMarkdown("Gone", null, null, new Version(1, 0, 0, 0));

        Assert.Contains("**Gone**", markdown);
        Assert.Contains("no effect", markdown);
        Assert.DoesNotContain("Installed:", markdown);
    }

    /// <summary>
    /// An assembly read out of the packages folder, before any build has produced output to name
    /// it from, still reports its version.
    /// </summary>
    [Fact]
    public void AVersionWithNoPathStillReports()
    {
        string markdown = BindingRedirectHandler.HoverMarkdown(
            "System.Buffers", new Version(4, 0, 3, 0), null, null);

        Assert.Contains("Installed: `4.0.3.0`", markdown);
    }
}
