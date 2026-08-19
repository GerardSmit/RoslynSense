using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using WebFormsCore.Nodes;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>One dotted segment of a binding path, and the member it named.</summary>
/// <param name="Symbol">Null where the walk gave up — an unresolved item type, or a segment no
/// member of the type before it declares.</param>
internal readonly record struct DataBindingSegment(TextSpan Span, string Name, ISymbol? Symbol);

/// <summary>
/// The item behind <c>Eval("Entity.Images")</c>: which type a data-binding expression reads from,
/// and which member each segment of its path names.
/// </summary>
/// <remarks>
/// The literal is the same shape of problem as a resource key or a <c>FindControl</c> ID — a name
/// Roslyn cannot see, because <c>Eval</c> takes a <c>string</c> and does its own reflection at
/// render time. What makes this one different is that the type to resolve against is not written
/// at the call: it comes from the container's <c>ItemType</c>, or, when nothing declares one, from
/// what the code-behind assigns to the control's <c>DataSource</c>.
/// <para>
/// Text-scanning rather than the projection, deliberately. A binding expression appears in an
/// attribute value as often as in element content, the parser keeps it as an unstructured value
/// either way, and the completion side has always found it by walking back from the caret — one
/// way of locating these literals, used by completion, navigation and colouring alike.
/// </para>
/// </remarks>
internal static class DataBindingService
{
    /// <summary>The page methods whose first argument is a data field path.</summary>
    private static readonly string[] s_bindingMethods = ["Eval", "Bind", "XPath"];

    /// <summary>
    /// The <c>DataBinder</c> methods whose <em>second</em> argument is a data field path, the
    /// first being the item to read it from — <c>DataBinder.Eval(Container.DataItem, "Name")</c>.
    /// </summary>
    /// <remarks>
    /// The long-hand of <c>Eval("Name")</c>, and the only form available outside a data-binding
    /// context, which is why generated and hand-written markup alike is full of it.
    /// </remarks>
    private static readonly string[] s_binderMethods = ["Eval", "GetPropertyValue"];

    /// <summary>
    /// The span of the binding literal's content when <paramref name="offset"/> is inside one.
    /// </summary>
    /// <remarks>
    /// Walks back from the caret rather than forward from a match, so a literal still being typed
    /// — no closing quote yet — is found on the keystroke that needs it.
    /// </remarks>
    public static TextSpan? ArgumentAt(string text, int offset)
    {
        int quote = -1;

        for (int i = Math.Min(offset, text.Length) - 1; i >= 0; i--)
        {
            if (text[i] is '\n' or '\r')
                return null;
            if (text[i] is '"' or '\'')
            {
                quote = i;
                break;
            }
        }

        if (quote < 0 || !IsBindingCallBefore(text, quote))
            return null;

        int close = text.IndexOf(text[quote], quote + 1);
        int end = close < 0 || close < offset ? offset : close;

        return TextSpan.FromBounds(quote + 1, Math.Max(quote + 1, end));
    }

    /// <summary>Every binding literal in the file, for the passes that colour or check all of
    /// them rather than the one under a caret.</summary>
    public static IEnumerable<TextSpan> AllArguments(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('"' or '\'') || !IsBindingCallBefore(text, i))
                continue;

            int close = text.IndexOf(text[i], i + 1);
            if (close < 0)
                yield break;

            yield return TextSpan.FromBounds(i + 1, close);
            i = close;
        }
    }

    /// <summary>
    /// Whether the quote at <paramref name="quote"/> opens a data field path: the first argument
    /// of <c>Eval</c>, <c>Bind</c> or <c>XPath</c>, or the second of <c>DataBinder.Eval</c>.
    /// </summary>
    /// <remarks>
    /// The argument's position is part of the question, not a detail. <c>Eval("Amount", "{0:c}")</c>
    /// and <c>DataBinder.Eval(item, "Amount", "{0:c}")</c> both end in a format string, which is
    /// not a path and must not be offered field completions or coloured as members.
    /// </remarks>
    private static bool IsBindingCallBefore(string text, int quote)
    {
        if (!TryReadCall(text, quote, out string name, out int argument, out bool qualified))
            return false;

        return argument switch
        {
            0 => Array.IndexOf(s_bindingMethods, name) >= 0,
            // Only when written on a receiver: a page's own `Eval(item, "Name")` does not exist,
            // whereas an unqualified two-argument call is somebody else's method.
            1 => qualified && Array.IndexOf(s_binderMethods, name) >= 0,
            _ => false,
        };
    }

    /// <summary>
    /// The call the quote at <paramref name="quote"/> is an argument of: the method's
    /// <paramref name="name"/>, the zero-based <paramref name="argument"/> position the quote sits
    /// at, and whether the name was written on a receiver — <c>DataBinder.Eval</c> rather than
    /// <c>Eval</c>.
    /// </summary>
    /// <remarks>
    /// A backward scan, because that is what the caret cases need: the text to the right of a
    /// literal being typed does not exist yet. Nested brackets are counted so that only the
    /// commas of this argument list are commas, and a string met on the way back is stepped over
    /// whole, so that a comma or a bracket inside one is not read as punctuation.
    /// </remarks>
    /// <returns>False when the quote is not an argument at all — the value of an attribute, a
    /// string in an expression that is not a call.</returns>
    private static bool TryReadCall(
        string text, int quote, out string name, out int argument, out bool qualified)
    {
        name = "";
        argument = 0;
        qualified = false;

        int depth = 0;
        int i = quote - 1;

        for (; i >= 0; i--)
        {
            switch (text[i])
            {
                case ')' or ']':
                    depth++;
                    break;

                case '(' or '[' when depth > 0:
                    depth--;
                    break;

                // The opening bracket of the argument list the quote is in.
                case '(':
                    goto found;

                case '[':
                    return false;

                case ',' when depth == 0:
                    argument++;
                    break;

                case '"' or '\'':
                    int opening = text.LastIndexOf(text[i], i - 1);
                    if (opening < 0)
                        return false;
                    i = opening;
                    break;

                // The line is the bound, the way it is for the caret walk in ArgumentAt: a call
                // spelled across two lines is rare, and without a bound every quote in the file
                // would scan back to its start.
                case '\n' or '\r':
                    return false;
            }
        }

        return false;

    found:
        int j = SkipWhitespaceBack(text, i - 1);

        int nameEnd = j + 1;
        while (j >= 0 && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
            j--;

        name = text[(j + 1)..nameEnd];

        int receiver = SkipWhitespaceBack(text, j);
        qualified = receiver >= 0 && text[receiver] == '.';

        return name.Length > 0;
    }

    private static int SkipWhitespaceBack(string text, int index)
    {
        while (index >= 0 && char.IsWhiteSpace(text[index]))
            index--;

        return index;
    }

    /// <summary>
    /// The path's segments with the member each one named, resolved left to right from the item
    /// type. A segment that resolves to nothing ends the walk: everything after it is a member of
    /// a type nobody knows, so it is reported unresolved rather than guessed at.
    /// </summary>
    public static ImmutableArray<DataBindingSegment> Segments(
        string text, TextSpan argument, INamedTypeSymbol? itemType)
    {
        var segments = ImmutableArray.CreateBuilder<DataBindingSegment>();
        ITypeSymbol? current = itemType;
        int start = argument.Start;

        while (start <= argument.End)
        {
            int end = SegmentEnd(text, start, argument.End);

            // `Rows[0]` and `Item['key']` are one segment each: the name is what F12 and the
            // colouring are about, and the brackets are an operation applied to it.
            int nameEnd = text.IndexOf('[', start);
            if (nameEnd < 0 || nameEnd > end)
                nameEnd = end;

            string name = text[start..nameEnd];
            bool indexed = nameEnd < end;

            var member = current is null || name.Length == 0
                ? null
                : indexed ? Field(current, name) ?? Indexer(current, name) : Field(current, name);

            segments.Add(new DataBindingSegment(TextSpan.FromBounds(start, nameEnd), name, member));

            current = member is null
                ? null
                : indexed ? Indexed(member) : MemberType(member);

            if (end == argument.End)
                break;

            start = end + 1;
        }

        return segments.ToImmutable();
    }

    /// <summary>
    /// Where the segment starting at <paramref name="start"/> ends: the next <c>.</c> that is not
    /// inside brackets, so <c>Item['a.b'].Name</c> splits into two and not three.
    /// </summary>
    private static int SegmentEnd(string text, int start, int limit)
    {
        int depth = 0;

        for (int i = start; i < limit; i++)
        {
            switch (text[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth = Math.Max(0, depth - 1);
                    break;
                case '.' when depth == 0:
                    return i;
            }
        }

        return limit;
    }

    /// <summary>
    /// The indexer a bracketed segment names — <c>Item</c> being the name C# gives one unless
    /// <c>[IndexerName]</c> says otherwise.
    /// </summary>
    private static ISymbol? Indexer(ITypeSymbol type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is IPropertySymbol { IsIndexer: true, DeclaredAccessibility: Accessibility.Public } indexer
                    && (indexer.MetadataName.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Item", StringComparison.OrdinalIgnoreCase)))
                {
                    return indexer;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// What one bracket yields off a member: the indexer's own type when the member is the
    /// indexer, and otherwise what indexing its type gives — <c>Rows[0]</c> off a
    /// <c>List&lt;Row&gt;</c> property.
    /// </summary>
    private static ITypeSymbol? Indexed(ISymbol member)
    {
        if (member is IPropertySymbol { IsIndexer: true } indexer)
            return indexer.Type;

        if (MemberType(member) is not { } type)
            return null;

        if (type is IArrayTypeSymbol array)
            return array.ElementType;

        return Indexer(type, "Item") is IPropertySymbol element ? element.Type : null;
    }

    /// <summary>The segment a caret is in, or null when it is between the quotes of an empty
    /// path.</summary>
    public static DataBindingSegment? SegmentAt(
        ImmutableArray<DataBindingSegment> segments, int offset)
    {
        foreach (var segment in segments)
        {
            if (offset >= segment.Span.Start && offset <= segment.Span.End)
                return segment;
        }

        return null;
    }

    /// <summary>What <c>Eval</c> can read off an item: its public properties and fields.</summary>
    public static IEnumerable<ISymbol> Fields(ITypeSymbol itemType)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var current = itemType; current is not null; current = current.BaseType)
        {
            if (current.SpecialType == SpecialType.System_Object)
                yield break;

            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public)
                    continue;

                bool readable = member switch
                {
                    IPropertySymbol { IsIndexer: false, GetMethod.DeclaredAccessibility: Accessibility.Public } => true,
                    IFieldSymbol { AssociatedSymbol: null } => true,
                    _ => false,
                };

                if (readable && seen.Add(member.Name))
                    yield return member;
            }
        }
    }

    /// <summary>The one field of <paramref name="type"/> a path segment names.</summary>
    /// <remarks>
    /// Case-insensitively, because <c>DataBinder.Eval</c> is: it goes through
    /// <c>TypeDescriptor</c>, whose property lookup ignores case, so <c>Eval("name")</c> reads
    /// <c>Name</c> at runtime and colouring it as unresolved would be wrong.
    /// </remarks>
    public static ISymbol? Field(ITypeSymbol type, string name)
    {
        foreach (var member in Fields(type))
        {
            if (member.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return member;
        }

        return null;
    }

    public static ITypeSymbol? MemberType(ISymbol member) => member switch
    {
        IPropertySymbol property => property.Type,
        IFieldSymbol field => field.Type,
        _ => null,
    };

    // ---- The item type ---------------------------------------------------------------------------

    /// <summary>
    /// The type a binding expression at <paramref name="offset"/> reads from: what the innermost
    /// container declares, and failing that what the code-behind assigns to its
    /// <c>DataSource</c>.
    /// </summary>
    public static async Task<INamedTypeSymbol?> ItemTypeAsync(
        AspxDocument document, int offset, CancellationToken ct)
    {
        if (document.Tree is not { } root)
            return null;

        var innermost = Innermost(root, offset);

        for (var node = innermost; node is not null; node = node.Parent)
        {
            var declared = node switch
            {
                TemplateNode template => template.ItemType,
                ControlNode control => control.ItemType,
                _ => null,
            };

            if (declared is not null)
                return declared;
        }

        // Only now, because ItemType is a statement and this is a deduction. A page that declares
        // one has said what it binds; a page that does not is the common case, and the answer has
        // to be dug out of the code-behind.
        for (var node = innermost; node is not null; node = node.Parent)
        {
            if (node is ControlNode { Id: { Length: > 0 } id }
                && await DataSourceItemTypeAsync(document, id, ct) is { } inferred)
            {
                return inferred;
            }
        }

        return null;
    }

    /// <summary>
    /// The element the offset sits in most deeply.
    /// </summary>
    private static ElementNode? Innermost(RootNode root, int offset)
    {
        ElementNode? innermost = null;

        foreach (var element in AspxSymbolResolver.EnumerateElements(root))
        {
            if (!Spans(element, offset))
                continue;

            if (innermost is null
                || element.StartTag.Range.Start.Offset >= innermost.StartTag.Range.Start.Offset)
            {
                innermost = element;
            }
        }

        return innermost;
    }

    private static bool Spans(ElementNode element, int offset)
    {
        int start = element.StartTag.Range.Start.Offset;
        int end = Math.Max(element.StartTag.Range.End.Offset, element.EndTag?.Range.End.Offset ?? 0);
        return offset >= start && offset <= end;
    }

    /// <summary>
    /// The element type of whatever the code-behind assigns to <c>&lt;id&gt;.DataSource</c>.
    /// </summary>
    /// <remarks>
    /// Every assignment is read and they have to agree: a control assigned one sequence in one
    /// branch and a different one in another has no single item type, and answering with whichever
    /// assignment happened to be found first would colour half the page's fields wrong. Two
    /// assignments of the same type are one answer, which is the common shape — a bind method and
    /// a rebind after a postback.
    /// </remarks>
    private static async Task<INamedTypeSymbol?> DataSourceItemTypeAsync(
        AspxDocument document, string id, CancellationToken ct)
    {
        if (document.CodeBehind is not { } codeBehind)
            return null;

        INamedTypeSymbol? found = null;

        foreach (var reference in codeBehind.DeclaringSyntaxReferences)
        {
            var tree = reference.SyntaxTree;
            var project = document.Project;
            var declaration = project.Solution.GetDocument(tree);

            if (declaration is null || await declaration.GetSemanticModelAsync(ct) is not { } model)
                continue;

            var root = await tree.GetRootAsync(ct);

            foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                ct.ThrowIfCancellationRequested();

                if (!IsDataSourceOf(assignment.Left, id))
                    continue;

                if (model.GetTypeInfo(assignment.Right, ct).Type is not { } assigned
                    || ElementType(assigned) is not { } element)
                {
                    continue;
                }

                if (found is null)
                    found = element;
                else if (!SymbolEqualityComparer.Default.Equals(found, element))
                    return null;
            }
        }

        return found;
    }

    /// <summary>Whether an assignment target is <c>id.DataSource</c>, written bare or through
    /// <c>this</c>.</summary>
    private static bool IsDataSourceOf(ExpressionSyntax left, string id) =>
        left is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "DataSource" } access
        && access.Expression switch
        {
            IdentifierNameSyntax name => name.Identifier.ValueText.Equals(id, StringComparison.Ordinal),
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } through =>
                through.Name.Identifier.ValueText.Equals(id, StringComparison.Ordinal),
            _ => false,
        };

    /// <summary>
    /// What one item of a sequence is: the element of an array, or the <c>T</c> of the
    /// <c>IEnumerable&lt;T&gt;</c> it implements.
    /// </summary>
    /// <remarks>
    /// A non-generic sequence — <c>DataTable</c>, <c>ArrayList</c>, an untyped <c>IEnumerable</c> —
    /// answers null rather than <c>object</c>. Its items have no static members to offer, and
    /// <c>object</c> would turn "we do not know" into a type against which every field in the page
    /// resolves to nothing and would be coloured as an error.
    /// </remarks>
    private static INamedTypeSymbol? ElementType(ITypeSymbol source)
    {
        if (source is IArrayTypeSymbol { ElementType: INamedTypeSymbol element })
            return element;

        if (source is INamedTypeSymbol { IsGenericType: true } named && IsEnumerable(named))
            return named.TypeArguments.FirstOrDefault() as INamedTypeSymbol;

        foreach (var contract in source.AllInterfaces)
        {
            if (IsEnumerable(contract))
                return contract.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
        }

        return null;
    }

    private static bool IsEnumerable(INamedTypeSymbol type) =>
        type is { IsGenericType: true, TypeArguments.Length: 1 }
        && type.ConstructedFrom.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T;
}
