using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services.ExternalSource;
using Xunit;

namespace RoslynMCP.Tests;

public sealed class ConfigurationReadNavigationTests
{
    [Fact]
    public void ConstantInAnotherTypeLandsOnReadingMethod()
    {
        const string source = """
            class Reader
            {
                string Read() => ConfigurationManager.AppSettings[Keys.Name];
            }
            """;
        AssertPosition(source, "Read()", "Setting", "Read");
    }

    [Fact]
    public void ConstantGetterInSameTypeDoesNotStealReadingLocation()
    {
        const string source = """
            class Reader
            {
                static string Key => "Setting";
                string Read() => ConfigurationManager.AppSettings[Key];
            }
            """;
        AssertPosition(source, "Read()", "Setting", "Read");
    }

    [Fact]
    public void DirectReadStillLandsOnLiteral()
    {
        const string source = """
            class Reader
            {
                static string Key => "Setting";
                string Read() => ConfigurationManager.AppSettings["Setting"];
            }
            """;
        int offset = source.LastIndexOf("\"Setting\"", StringComparison.Ordinal);
        Assert.Equal(Position(source, offset), SourceMemberLocator.FindConfigurationRead(source, "Setting", "Read", default));
    }

    [Fact]
    public void ReadingPropertyIsRecognizedByMetadataAccessorName()
    {
        const string source = "class Reader { string Value => ConfigurationManager.AppSettings[Keys.Name]; }";
        AssertPosition(source, "Value", "Setting", "get_Value");
    }

    [Fact]
    public void MissingMemberStillFallsBackToLiteral()
    {
        const string source = "class Reader { string Key => \"Setting\"; }";
        AssertPosition(source, "\"Setting\"", "Setting", "Renamed");
    }

    private static void AssertPosition(string source, string target, string literal, string method)
    {
        int offset = source.IndexOf(target, StringComparison.Ordinal);
        Assert.Equal(Position(source, offset), SourceMemberLocator.FindConfigurationRead(source, literal, method, default));
    }

    private static (int Line, int Character) Position(string source, int offset)
    {
        var position = SourceText.From(source).Lines.GetLinePosition(offset);
        return (position.Line, position.Character);
    }
}
