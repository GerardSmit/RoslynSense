using Microsoft.CodeAnalysis;
using WebFormsCore.Models;

namespace WebFormsCore.Nodes;

public class TemplateNode : ElementNode
{
    public string ClassName { get; set; } = default!;

    public Token Property { get; set; }

    /// <summary>
    /// The property the tag names — <c>Repeater.HeaderTemplate</c> for a
    /// <c>&lt;HeaderTemplate&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Code generation works from <see cref="ClassName"/> and never needs this; the editor does.
    /// A template tag is a member reference exactly the way an attribute name is, and without the
    /// symbol behind it go-to-definition and hover on the tag have nothing to answer with — which
    /// is a hole in the middle of a control, since a template is where most of the markup lives.
    /// </remarks>
    public MemberResult? Member { get; set; }

    public string? ControlsType { get; set; }

    /// <summary>
    /// Whether the template property is marked <c>[TemplateInstance(TemplateInstance.Single)]</c>,
    /// like <c>UpdatePanel.ContentTemplate</c>. Controls inside a single-instance template are
    /// instantiated exactly once, so Visual Studio gives them designer fields as if the template
    /// wrapper were not there.
    /// </summary>
    public bool IsSingleInstance { get; set; }

    /// <summary>
    /// The type <c>[TemplateContainer(typeof(X))]</c> on the template property declares for
    /// <c>Container</c> in data-binding expressions — <c>RepeaterItem</c> for a Repeater's
    /// ItemTemplate. Without it <c>Container</c> has no type and nothing in
    /// <c>&lt;%# Container.DataItem %&gt;</c> binds.
    /// </summary>
    public INamedTypeSymbol? ContainerType { get; set; }

    public List<ContainerNode> RenderMethods { get; set; } = new();

    public List<ControlId> Ids { get; set; } = new();

    public override string? VariableName
    {
        get => null;
        set
        {
            // ignore.
        }
    }

    public INamedTypeSymbol? ItemType { get; set; }
}
