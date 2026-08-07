using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using WebFormsCore;
using WebFormsCore.Models;
using WebFormsCore.Nodes;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>What the caret is sitting on in an ASPX file.</summary>
internal enum AspxHitKind
{
    /// <summary>A tag name — <c>&lt;asp:Button&gt;</c>.</summary>
    ControlType,

    /// <summary>An attribute name that binds to a property — <c>Text="…"</c>.</summary>
    PropertyName,

    /// <summary>An attribute value that binds to an enum member — <c>Mode="Static"</c>.</summary>
    PropertyValue,

    /// <summary>An event attribute name — <c>OnClick="…"</c>.</summary>
    EventName,

    /// <summary>An event attribute value: the handler method, which may not exist yet.</summary>
    EventHandler,

    /// <summary>An <c>ID</c> attribute value, which binds to the code-behind field.</summary>
    ControlId,

    /// <summary>The <c>Inherits</c> directive value.</summary>
    Inherits,

    /// <summary>A directive attribute naming another file (<c>MasterPageFile</c>, <c>Src</c>, …).</summary>
    FileReference,

    /// <summary>Inside a <c>&lt;% %&gt;</c> block, a <c>&lt;%= %&gt;</c> expression, a
    /// <c>&lt;script runat="server"&gt;</c> block or a data-binding attribute value.</summary>
    Code,

    /// <summary>The prefix of an expression builder — the <c>Resources</c> in
    /// <c>&lt;%$ Resources: Strings, Title %&gt;</c>.</summary>
    ExpressionBuilderPrefix,

    /// <summary>An expression builder's argument — the <c>Strings, Title</c>.</summary>
    ExpressionBuilderArgument,

    /// <summary>A <c>meta:resourcekey</c> value, which names a group of keys rather than one.</summary>
    ResourceKeyAttribute,
}

/// <summary>One resolved caret position in an ASPX file.</summary>
/// <param name="Kind">What kind of thing the caret is on.</param>
/// <param name="Span">The source span of the token under the caret.</param>
/// <param name="Symbol">The symbol it binds to, when there is one.</param>
/// <param name="Element">The element that owns it.</param>
/// <param name="Event">The event, when the hit is an event name or handler.</param>
/// <param name="Name">The token's own text.</param>
/// <param name="TargetPath">The referenced file, for <see cref="AspxHitKind.FileReference"/>.</param>
/// <param name="BuilderPrefix">The builder's prefix, for the two expression-builder kinds. It is
/// carried on the argument hit as well as on the prefix hit, because what a key is looked up in
/// depends on it — <c>Resources</c> and <c>dnnLoc</c> read the same argument differently.</param>
internal sealed record AspxHit(
    AspxHitKind Kind,
    TextSpan Span,
    ISymbol? Symbol = null,
    ElementNode? Element = null,
    IEventSymbol? Event = null,
    string? Name = null,
    string? TargetPath = null,
    string? BuilderPrefix = null);

/// <summary>
/// Maps a caret offset in ASPX markup to the symbol behind it — the markup counterpart of
/// <c>SymbolFinder.FindSymbolAtPositionAsync</c>, and the basis of every navigation feature
/// in a <c>.aspx</c> file.
/// </summary>
internal static class AspxSymbolResolver
{
    /// <summary>Directive attributes whose value is a path to another file.</summary>
    private static readonly string[] s_pathAttributes =
        ["MasterPageFile", "Src", "CodeBehind", "CodeFile", "VirtualPath", "TagName"];

    public static AspxHit? ResolveAt(AspxDocument document, int offset)
    {
        var root = document.Tree;
        if (root is null)
            return null;

        if (ResolveDirective(document, root, offset) is { } directiveHit)
            return directiveHit;

        // Ahead of ResolveCode, not after it. Both are `<% %>` regions that beat the element
        // around them, but a builder is not C#: it never reaches the projection, so letting the
        // code pass claim one would send the caret to a document that does not contain it.
        if (ResolveExpressionBuilder(root, offset) is { } builderHit)
            return builderHit;

        // Code regions win over the elements around them: a `<% %>` block inside a control's
        // body is code, not markup.
        if (ResolveCode(root, offset) is { } codeHit)
            return codeHit;

        foreach (var element in EnumerateElements(root))
        {
            if (ResolveElement(document, element, offset) is { } hit)
                return hit;
        }

        return null;
    }

    // ---- Elements --------------------------------------------------------------------------

    /// <summary>
    /// Every element in the tree, including the ones inside templates —
    /// <see cref="ControlNode.Templates"/> sits outside <see cref="ContainerNode.Children"/>,
    /// so a plain walk of the child hierarchy misses everything in an
    /// <c>&lt;ItemTemplate&gt;</c>.
    /// </summary>
    public static IEnumerable<ElementNode> EnumerateElements(RootNode root)
    {
        foreach (var node in root.AllChildren)
            if (node is ElementNode e) yield return e;

        foreach (var template in root.Templates)
        {
            yield return template;
            foreach (var node in template.AllChildren)
                if (node is ElementNode e) yield return e;
        }
    }

    /// <summary>Every server control, templates included.</summary>
    public static IEnumerable<ControlNode> EnumerateControls(RootNode root) =>
        EnumerateElements(root).OfType<ControlNode>();

    private static AspxHit? ResolveElement(AspxDocument document, ElementNode element, int offset)
    {
        var type = ElementType(element);

        if (Contains(element.StartTag.ElementRange, offset)
            || (element.EndTag is not null && Contains(element.EndTag.ElementRange, offset)))
        {
            // `<HeaderTemplate>` is `Repeater.HeaderTemplate`: a member reference like an attribute
            // name, not a control.
            if (element is TemplateNode { Member: { } member })
            {
                return new AspxHit(AspxHitKind.PropertyName, Span(element.StartTag.Name.Range),
                    Symbol: member.Symbol, Element: element, Name: element.Name.Value);
            }

            return type is null
                ? null
                : new AspxHit(AspxHitKind.ControlType, Span(element.StartTag.ElementRange),
                    Symbol: type, Element: element, Name: element.Name.Value);
        }

        foreach (var (key, value) in element.RawAttributes)
        {
            if (Contains(key.Range, offset))
                return ResolveAttributeName(element, type, key);

            if (Contains(value.Range, offset))
                return ResolveAttributeValue(document, element, type, key, value);
        }

        return null;
    }

    private static AspxHit? ResolveAttributeName(ElementNode element, INamedTypeSymbol? type, TokenString key)
    {
        string name = key.Value;

        // A prefixed name — `meta:resourcekey` — belongs to the page framework rather than to the
        // control, and no CLR member name may contain a colon, so walking the type for one can
        // only fail. Answering nothing is the honest result.
        if (type is null
            || name.Equals("runat", StringComparison.OrdinalIgnoreCase)
            || name.IndexOf(':') >= 0)
        {
            return null;
        }

        if (TryGetEvent(type, name) is { } @event)
            return new AspxHit(AspxHitKind.EventName, Span(key.Range),
                Symbol: @event, Element: element, Event: @event, Name: name);

        // `Font-Bold` walks Font then Bold; the caret picks which segment it lands on. The whole
        // attribute is one token, so the segment is derived from its offset within the name.
        var (segment, _, segmentSpan) = WalkSegments(type, key);
        if (segment is null)
            return null;

        return new AspxHit(AspxHitKind.PropertyName, segmentSpan,
            Symbol: segment.Symbol, Element: element, Name: segment.Name);
    }

    private static AspxHit? ResolveAttributeValue(
        AspxDocument document, ElementNode element, INamedTypeSymbol? type,
        TokenString key, AttributeValue value)
    {
        string name = key.Value;

        // Before the ID check and before the event lookup: an implicit-localization key names a
        // whole group of properties — `btnSave.Text`, `btnSave.ToolTip` — so it is not a value of
        // the attribute it is written beside, and nothing on the control binds it.
        if (IsImplicitKeyAttribute(name))
        {
            return new AspxHit(AspxHitKind.ResourceKeyAttribute, Span(value.Range),
                Element: element, Name: value.Value);
        }

        if (name.Equals("ID", StringComparison.OrdinalIgnoreCase))
        {
            // Controls inside a template have no code-behind field — they are reached through
            // FindControl — so the ID resolves to nothing rather than to the wrong symbol.
            var field = document.CodeBehind?.GetMemberDeep(value.Value);
            return new AspxHit(AspxHitKind.ControlId, Span(value.Range),
                Symbol: field?.Symbol, Element: element, Name: value.Value);
        }

        if (type is not null && TryGetEvent(type, name) is { } @event)
        {
            var handler = document.CodeBehind?.GetDeep<IMethodSymbol>(value.Value);
            return new AspxHit(AspxHitKind.EventHandler, Span(value.Range),
                Symbol: handler, Element: element, Event: @event, Name: value.Value);
        }

        if (type is null)
            return null;

        var (segment, _, _) = WalkSegments(type, key);
        if (segment is null)
            return null;

        // An enum-valued attribute names a member of that enum: `Mode="Static"` is a symbol
        // reference, not a string.
        var enumType = UnwrapNullable(segment.Type);
        if (enumType.TypeKind != TypeKind.Enum)
            return null;

        var member = enumType.GetMembers()
            .OfType<IFieldSymbol>()
            .FirstOrDefault(f => f.Name.Equals(value.Value, StringComparison.OrdinalIgnoreCase));

        return member is null
            ? null
            : new AspxHit(AspxHitKind.PropertyValue, Span(value.Range),
                Symbol: member, Element: element, Name: value.Value);
    }

    /// <summary>
    /// Resolves the dashed segments of an attribute name (<c>Font-Bold</c>) against
    /// <paramref name="type"/>, returning the segment the caret is on.
    /// </summary>
    private static (MemberResult? Member, ITypeSymbol? Type, TextSpan Span) WalkSegments(
        INamedTypeSymbol type, TokenString key)
    {
        string name = key.Value;
        int start = key.Range.Start.Offset;

        ITypeSymbol current = type;
        int segmentStart = 0;

        while (true)
        {
            int dash = name.IndexOf('-', segmentStart);
            string segment = dash < 0 ? name[segmentStart..] : name[segmentStart..dash];

            var member = current.GetMemberDeep(segment);
            if (member is null)
                return (null, null, default);

            var span = new TextSpan(start + segmentStart, segment.Length);

            if (dash < 0)
                return (member, current, span);

            current = member.Type;
            segmentStart = dash + 1;
        }
    }

    // ---- Directives ------------------------------------------------------------------------

    private static AspxHit? ResolveDirective(AspxDocument document, RootNode root, int offset)
    {
        foreach (var directive in root.Directives)
        {
            if (!Contains(directive.Range, offset))
                continue;

            foreach (var (key, value) in directive.Attributes)
            {
                if (!Contains(value.Range, offset))
                    continue;

                if (key.Value.Equals("Inherits", StringComparison.OrdinalIgnoreCase))
                {
                    return new AspxHit(AspxHitKind.Inherits, Span(value.Range),
                        Symbol: root.Inherits, Name: value.Value);
                }

                if (s_pathAttributes.Contains(key.Value, StringComparer.OrdinalIgnoreCase))
                {
                    return new AspxHit(AspxHitKind.FileReference, Span(value.Range),
                        Name: value.Value,
                        TargetPath: ResolvePath(document, value.Value));
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>Resolves a directive path — <c>~/</c> against the project, anything else
    /// against the file's own directory.</summary>
    public static string? ResolvePath(AspxDocument document, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string relative = value.Replace('/', Path.DirectorySeparatorChar).TrimStart();
        string baseDir;

        if (relative.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            baseDir = Path.GetDirectoryName(document.Project.FilePath) ?? string.Empty;
            relative = relative[2..];
        }
        else
        {
            baseDir = Path.GetDirectoryName(document.FilePath) ?? string.Empty;
        }

        if (baseDir.Length == 0)
            return null;

        try
        {
            string full = Path.GetFullPath(Path.Combine(baseDir, relative));
            return File.Exists(full) ? full : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // ---- Expression builders ---------------------------------------------------------------

    /// <summary>
    /// A <c>&lt;%$ Prefix: Argument %&gt;</c>, whether it stands in element content or is the
    /// whole of an attribute's value.
    /// </summary>
    /// <remarks>
    /// Both shapes have to be covered here because the parser models them differently: one is a
    /// node in the tree, the other is the attribute's value with no node of its own — the control
    /// it is written on has not been pushed when the value is read.
    /// </remarks>
    private static AspxHit? ResolveExpressionBuilder(RootNode root, int offset)
    {
        foreach (var node in EnumerateNodes(root))
        {
            if (node is ExpressionBuilderNode builder
                && ResolveBuilderHalf(offset, builder.Prefix, builder.Argument) is { } hit)
            {
                return hit;
            }
        }

        foreach (var element in EnumerateElements(root))
        {
            foreach (var (_, value) in element.RawAttributes)
            {
                if (value.Kind is AttributeValueKind.ExpressionBuilder
                    && ResolveBuilderHalf(offset, value.Prefix, value.Token, element) is { } hit)
                {
                    return hit;
                }
            }
        }

        return null;
    }

    /// <summary>The half of a builder the caret is on, or <c>null</c> when it is on neither —
    /// the delimiters and the colon between the two names belong to no name.</summary>
    private static AspxHit? ResolveBuilderHalf(
        int offset, TokenString prefix, TokenString argument, ElementNode? element = null)
    {
        if (Contains(prefix.Range, offset))
            return new AspxHit(AspxHitKind.ExpressionBuilderPrefix, Span(prefix.Range),
                Element: element, Name: prefix.Value, BuilderPrefix: prefix.Value);

        if (Contains(argument.Range, offset))
            return new AspxHit(AspxHitKind.ExpressionBuilderArgument, Span(argument.Range),
                Element: element, Name: argument.Value, BuilderPrefix: prefix.Value);

        return null;
    }

    /// <summary>Every node in the file, templates included — a template's contents hang off
    /// <see cref="RootNode.Templates"/> rather than off the child hierarchy.</summary>
    public static IEnumerable<Node> EnumerateNodes(RootNode root)
    {
        foreach (var node in root.AllChildren)
            yield return node;

        foreach (var template in root.Templates)
        {
            foreach (var node in template.AllChildren)
                yield return node;
        }
    }

    /// <summary>Whether an attribute carries an implicit-localization key. ASP.NET spells it
    /// <c>meta:resourcekey</c>; DNN spells the same idea <c>resourcekey</c>.</summary>
    public static bool IsImplicitKeyAttribute(string name) =>
        name.Equals("meta:resourcekey", StringComparison.OrdinalIgnoreCase)
        || name.Equals("resourcekey", StringComparison.OrdinalIgnoreCase);

    // ---- Code regions ----------------------------------------------------------------------

    private static AspxHit? ResolveCode(RootNode root, int offset)
    {
        foreach (var node in root.AllChildren)
        {
            switch (node)
            {
                case ExpressionNode expr when Contains(expr.Text.Range, offset):
                    return new AspxHit(AspxHitKind.Code, Span(expr.Text.Range), Name: expr.Text.Value);
                case StatementNode stmt when Contains(stmt.Text.Range, offset):
                    return new AspxHit(AspxHitKind.Code, Span(stmt.Text.Range), Name: stmt.Text.Value);
            }
        }

        foreach (var script in root.ScriptBlocks)
        {
            if (Contains(script.Range, offset))
                return new AspxHit(AspxHitKind.Code, Span(script.Range), Name: script.Value);
        }

        // `Text='<%# Eval("Name") %>'`. The range the parser reports is the expression alone, so
        // this only claims the caret once it is past the `<%#` — everywhere else in the attribute
        // it is still an ordinary value.
        foreach (var element in EnumerateElements(root))
        {
            foreach (var (_, value) in element.RawAttributes)
            {
                if (value.Kind is AttributeValueKind.Code && Contains(value.Range, offset))
                    return new AspxHit(AspxHitKind.Code, Span(value.Range),
                        Element: element, Name: value.Value);
            }
        }

        return null;
    }

    // ---- Helpers ---------------------------------------------------------------------------

    public static INamedTypeSymbol? ElementType(ElementNode element) => element switch
    {
        ControlNode c => c.ControlType,
        CollectionNode c => c.PropertyType,
        _ => null,
    };

    /// <summary>The event an <c>On…</c> attribute names, or <c>null</c> when the type has no
    /// such event (which makes the attribute an ordinary property named <c>On…</c>).</summary>
    public static IEventSymbol? TryGetEvent(ITypeSymbol type, string attributeName) =>
        attributeName.Length > 2 && attributeName.StartsWith("On", StringComparison.OrdinalIgnoreCase)
            ? type.GetDeep<IEventSymbol>(attributeName[2..])
            : null;

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } named
            ? named.TypeArguments[0]
            : type;

    /// <summary>
    /// End-inclusive, because the caret sits between characters: with the caret just past the
    /// last character of <c>OnClick</c> the user is still on <c>OnClick</c>.
    /// </summary>
    public static bool Contains(TokenRange range, int offset) =>
        offset >= range.Start.Offset && offset <= range.End.Offset;

    public static TextSpan Span(TokenRange range) =>
        TextSpan.FromBounds(range.Start.Offset, Math.Max(range.Start.Offset, range.End.Offset));
}
