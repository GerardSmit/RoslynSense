using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Lsp.Handlers;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Attribute arguments in the hover, written as values. <see cref="Microsoft.CodeAnalysis.TypedConstant"/>
/// does not override <c>ToString()</c>, so a member annotated <c>[XmlElement("tabid")]</c> hovered as
/// <c>[XmlElement(Microsoft.CodeAnalysis.TypedConstant)]</c> — the name of the struct, on every argument.
/// </summary>
public class HoverAttributeRenderingTests
{
    [Fact]
    public void ConstructorAndNamedArgumentsShowTheirValues()
    {
        var property = PropertyOf(
            """
            using System;

            public class ColumnAttribute : Attribute
            {
                public ColumnAttribute(string name) { }
                public bool CanBeNull { get; set; }
            }

            public class ContentItem
            {
                [Column("tabid", CanBeNull = true)]
                public int TabID { get; set; }
            }
            """);

        string hover = HoverHandler.Describe(property, default);

        Assert.Contains("[Column(\"tabid\", CanBeNull = true)]", hover);
        Assert.DoesNotContain("TypedConstant", hover);
    }

    [Fact]
    public void EnumAndTypeArgumentsAreWrittenAsCSharp()
    {
        var property = PropertyOf(
            """
            using System;

            public class ShapedAttribute : Attribute
            {
                public ShapedAttribute(StringComparison comparison, Type type) { }
            }

            public class ContentItem
            {
                [Shaped(StringComparison.Ordinal, typeof(string))]
                public int TabID { get; set; }
            }
            """);

        string hover = HoverHandler.Describe(property, default);

        Assert.Contains("StringComparison.Ordinal", hover);
        Assert.Contains("typeof(string)", hover);
    }

    private static IPropertySymbol PropertyOf(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "AttributeFixture",
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return (IPropertySymbol)compilation
            .GetTypeByMetadataName("ContentItem")!
            .GetMembers("TabID")
            .Single();
    }

    private static ImmutableArray<MetadataReference> References =>
        [.. AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))];
}
