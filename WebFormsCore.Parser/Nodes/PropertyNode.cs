using Microsoft.CodeAnalysis;
using WebFormsCore.Models;

namespace WebFormsCore.Nodes;

public class PropertyNode : Node
{
    public PropertyNode(MemberResult member, AttributeValue value, INamedTypeSymbol? converter)
        : base(NodeType.Property)
    {
        Member = member;
        Value = value;
        Converter = converter;
    }

    public MemberResult Member { get; set; }

    public AttributeValue Value { get; set; }

    /// <summary>
    /// Where the attribute's name was written, as opposed to <see cref="Node.Range"/>, which is
    /// its value. Code generation only ever needs the value; renaming the property needs this, and
    /// without it a rename rewrites <c>Title="Welcome"</c> into <c>Title="NewName"</c>.
    /// Null for a property the parser synthesised rather than read from source.
    /// </summary>
    public TokenRange? NameRange { get; set; }

    public INamedTypeSymbol? Converter { get; set; }

    public string DisplayType => Member.DisplayType;

    public string? DisplayConverter => Converter?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}
