using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Resources.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The spans <c>.resx</c> features are built on: a rename replaces <c>KeySpan</c>, hover and
/// definition point at it, and a diagnostic is drawn on it.
/// </summary>
/// <remarks>
/// Written against the full-fidelity parser the reader moved onto. The cases that matter are the
/// ones where the older <see cref="System.Xml.XmlReader"/> implementation had to decline: a name
/// carrying an entity reference, and a buffer that stopped being XML halfway down.
/// </remarks>
public class ResxReaderTests
{
    private static string Wrap(string body) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <root>
        {body}
        </root>
        """;

    private static (ResxContents Contents, string Text) Read(string body, string newLine = "\n")
    {
        string text = Wrap(body).ReplaceLineEndings(newLine);
        return (ResxReader.Read(SourceText.From(text)), text);
    }

    [Fact]
    public void TheKeySpanIsTheNameValueWithoutItsQuotes()
    {
        var (contents, text) = Read("""
              <data name="Greeting" xml:space="preserve">
                <value>Hello</value>
              </data>
            """);

        var entry = contents.Entries["Greeting"];

        // The exact characters a rename replaces. Quotes excluded, or the rename eats them.
        Assert.Equal("Greeting", text.Substring(entry.KeySpan.Start, entry.KeySpan.Length));
        Assert.Equal("Hello", text.Substring(entry.ValueSpan.Start, entry.ValueSpan.Length));
        Assert.Equal("Hello", entry.Value);
    }

    /// <summary>
    /// The behaviour that changed with the parser. The old reader returned <c>default</c> here,
    /// which is how it declined to rename a key it could not span; the tree keeps the raw source, so
    /// the span is real — but it is five characters longer than the decoded key, and that mismatch
    /// is what callers now check before rewriting in place.
    /// </summary>
    [Fact]
    public void AnEntityInTheNameSpansTheSourceNotTheDecodedKey()
    {
        var (contents, text) = Read("""
              <data name="A&amp;B" xml:space="preserve">
                <value>ampersand</value>
              </data>
            """);

        var entry = Assert.Single(contents.Entries).Value;

        Assert.Equal("A&B", entry.Key);
        Assert.Equal("A&amp;B", text.Substring(entry.KeySpan.Start, entry.KeySpan.Length));

        // The guard every in-place rewrite depends on: the span is longer than the key it decodes
        // to, so a caller replacing `KeySpan.Length` characters with a new key of `Key.Length` would
        // be editing the wrong range.
        Assert.NotEqual(entry.Key.Length, entry.KeySpan.Length);
    }

    [Fact]
    public void EntriesSurviveABufferThatStopsBeingXml()
    {
        // A file being typed into: the last entry is half-written and the root never closes.
        string text = """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <data name="First" xml:space="preserve">
                <value>one</value>
              </data>
              <data name="Second" xml:space="preserve">
                <value>two</value>
              </data>
              <data name="Half
            """;

        var contents = ResxReader.Read(SourceText.From(text));

        Assert.Equal("one", contents.Entries["First"].Value);
        Assert.Equal("two", contents.Entries["Second"].Value);

        // The half-written entry comes through as well, and its key spans exactly what has been
        // typed so far. That is the tolerant parser doing its job: the old reader stopped at the
        // first malformation and lost every entry from there down, whereas here the only entry
        // affected is the one under the caret — which is the one the user is looking at.
        var half = contents.Entries["Half"];
        Assert.Equal("Half", text.Substring(half.KeySpan.Start, half.KeySpan.Length));
    }

    [Fact]
    public void SpansAreCorrectUnderBothLineEndings()
    {
        foreach (string newLine in new[] { "\n", "\r\n" })
        {
            var (contents, text) = Read("""
                  <data name="Greeting" xml:space="preserve">
                    <value>Hello</value>
                  </data>
                """, newLine);

            var entry = contents.Entries["Greeting"];

            Assert.Equal("Greeting", text.Substring(entry.KeySpan.Start, entry.KeySpan.Length));
            Assert.Equal("Hello", text.Substring(entry.ValueSpan.Start, entry.ValueSpan.Length));
        }
    }

    [Fact]
    public void ADuplicateKeyIsReportedAndTheFirstOneWins()
    {
        var (contents, _) = Read("""
              <data name="Same" xml:space="preserve">
                <value>first</value>
              </data>
              <data name="Same" xml:space="preserve">
                <value>second</value>
              </data>
            """);

        Assert.Equal("first", contents.Entries["Same"].Value);
        Assert.Equal("Same", Assert.Single(contents.DuplicateKeys));
    }

    /// <summary>
    /// A <c>ResXFileRef</c> or a serialized object. The key still counts — a rename has to move it
    /// and a missing-key diagnostic must not fire on it — but there is no string to show.
    /// </summary>
    [Fact]
    public void ATypedEntryKeepsItsKeyAndDropsItsValue()
    {
        var (contents, _) = Read("""
              <data name="Logo" type="System.Resources.ResXFileRef, System.Windows.Forms">
                <value>logo.png;System.Drawing.Bitmap</value>
              </data>
            """);

        var entry = contents.Entries["Logo"];

        Assert.Null(entry.Value);
        Assert.NotEqual(0, entry.KeySpan.Length);
    }

    [Fact]
    public void AnEmptyValueElementSpansThePositionWhereContentWouldGo()
    {
        var (contents, text) = Read("""
              <data name="Blank" xml:space="preserve">
                <value></value>
              </data>
            """);

        var entry = contents.Entries["Blank"];

        Assert.Equal(string.Empty, entry.Value);
        Assert.Equal(0, entry.ValueSpan.Length);
        Assert.Equal("</value>", text.Substring(entry.ValueSpan.Start, "</value>".Length));
    }
}
