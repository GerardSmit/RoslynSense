using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Languages.Resources;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// RSX0002 lives in the neutral file, at the key: the missing entries have no position anywhere
/// else, so that declaration is the only place a squiggle can mean anything.
/// </summary>
public class ResourceDiagnosticsTests : IDisposable
{
    private const string Neutral =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <root>
          <data name="btnSave.Text" xml:space="preserve"><value>Save</value></data>
          <data name="btnCancel.Text" xml:space="preserve"><value>Cancel</value></data>
        </root>
        """;

    private const string Translated =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <root>
          <data name="btnSave.Text" xml:space="preserve"><value>Opslaan</value></data>
        </root>
        """;

    private readonly string _root;
    private readonly ResourcesLanguage _pack = new(EffectiveSettings.Resolve([], null, out _));

    public ResourceDiagnosticsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "roslynsense-resx-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task AnUntranslatedKeyIsReportedAtItsDeclarationInTheNeutralFile()
    {
        string neutral = Write("View.ascx.resx", Neutral);
        Write("View.ascx.nl.resx", Translated);
        Write("View.ascx.de.resx", Translated);

        var diagnostics = await _pack.DiagnosticsAsync(neutral, default);

        var missing = Assert.Single(diagnostics, d => d.Code == "RSX0002");
        Assert.Equal("'btnCancel.Text' has no translation in de, nl.", missing.Message);

        // The range is the key's name= value, not the first line of the file.
        var text = SourceText.From(Neutral);
        var line = text.Lines[missing.Range.Start.Line];
        Assert.Equal(
            "btnCancel.Text",
            line.ToString().Substring(
                missing.Range.Start.Character,
                missing.Range.End.Character - missing.Range.Start.Character));
    }

    [Fact]
    public async Task TheTranslationItselfIsNotWhereTheReportGoes()
    {
        Write("View.ascx.resx", Neutral);
        string translation = Write("View.ascx.nl.resx", Translated);

        var diagnostics = await _pack.DiagnosticsAsync(translation, default);

        Assert.DoesNotContain(diagnostics, d => d.Code == "RSX0002");
    }

    [Fact]
    public async Task ANeutralFileWithNoTranslationsHasNothingToReport()
    {
        string neutral = Write("View.ascx.resx", Neutral);

        var diagnostics = await _pack.DiagnosticsAsync(neutral, default);

        Assert.DoesNotContain(diagnostics, d => d.Code == "RSX0002");
    }

    [Fact]
    public async Task ACustomizationIsNotMeasured()
    {
        // A Host override is meant to carry only the keys it overrides; its gaps are not work.
        string neutral = Write("View.ascx.resx", Neutral);
        Write("View.ascx.Host.resx", Translated);

        var diagnostics = await _pack.DiagnosticsAsync(neutral, default);

        Assert.DoesNotContain(diagnostics, d => d.Code == "RSX0002");
    }

    private string Write(string name, string contents)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
