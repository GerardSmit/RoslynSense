using WebFormsCore.Models;

namespace WebFormsCore.Nodes;

public class ExpressionBuilderNode : Node
{
    public ExpressionBuilderNode()
        : base(NodeType.ExpressionBuilder)
    {
    }

    public TokenString Prefix { get; set; }

    public TokenString Argument { get; set; }
}
