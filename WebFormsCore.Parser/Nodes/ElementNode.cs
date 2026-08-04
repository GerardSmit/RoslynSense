using System.Diagnostics;
using System.Text;
using WebFormsCore.Collections.Comparers;
using WebFormsCore.Models;

namespace WebFormsCore.Nodes;

public class ElementNode : ContainerNode, IAttributeNode
{
    protected ElementNode(NodeType type)
        : base(type)
    {
    }

    public ElementNode()
        : base(NodeType.Element)
    {
    }

    public TokenString Name => StartTag.Name;

    public TokenString? Namespace => StartTag.Namespace;

    public HtmlTagNode StartTag { get; set; } = new();

    public HtmlTagNode? EndTag { get; set; }

    public virtual string? VariableName { get; set; }

    public Dictionary<TokenString, AttributeValue> Attributes { get; set; } = new(AttributeCompare.IgnoreCase);

    /// <summary>
    /// Every attribute as it was written, before the parser sorted it into a property, an event
    /// or a passthrough attribute. Code generation never needs this; editor features do — it is
    /// the only place the source range of a consumed or rejected attribute survives.
    /// </summary>
    public Dictionary<TokenString, AttributeValue> RawAttributes { get; set; } = new(AttributeCompare.IgnoreCase);
}
