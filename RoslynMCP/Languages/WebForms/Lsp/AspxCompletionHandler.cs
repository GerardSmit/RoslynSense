using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Languages.Formatting;
using RoslynMCP.Languages.WebForms.Core;
using WebFormsCore;
using WebFormsCore.Nodes;
using CompletionItem = RoslynMCP.Lsp.Protocol.CompletionItem;
using CompletionList = RoslynMCP.Lsp.Protocol.CompletionList;
using RoslynMCP.Lsp;
using Protocol = RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Lsp.Handlers;

namespace RoslynMCP.Languages.WebForms.Lsp;

/// <summary>
/// textDocument/completion inside markup. Tag names, attribute names and attribute values come
/// from the control's own type; a caret inside <c>&lt;% %&gt;</c> is handed to Roslyn through
/// the C# projection so inline code completes like any other C#.
/// </summary>
internal static class AspxCompletionHandler
{
    private static readonly CompletionList Empty = new(false, []);

    public static async Task<CompletionList> CompletionAsync(
        CompletionParams p, LspResolveCache cache, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);
        var document = await AspxDocumentService.GetAsync(path, ct);
        if (document is null)
            return Empty;

        return await CompleteAsync(
            document, LspConverters.ToOffset(document.SourceText, p.Position), p.Context, cache, ct);
    }

    /// <summary>
    /// The list for a caret in a document that is already resolved, split out the way
    /// <see cref="CompletionHandler.CompleteAsync"/> is: everything below decides what to offer
    /// from the markup alone, and nothing here needs a URI.
    /// </summary>
    public static async Task<CompletionList> CompleteAsync(
        AspxDocument document, int offset, LspCompletionContext? trigger,
        LspResolveCache cache, CancellationToken ct)
    {
        var context = AspxCompletionContextScanner.Classify(document.Text, offset);

        return context.Kind switch
        {
            // `<%$ … %>` lexes as a code region but is not code — the projection never sees one —
            // so a caret inside it completes resource keys rather than C# symbols.
            AspxContextKind.Code when AspxResourceHandler.IsExpressionBuilder(document.Text, context.TagStart) =>
                await AspxResourceHandler.BuilderKeysAsync(document, context, offset, ct),
            AspxContextKind.Code => await CodeAsync(document, offset, trigger, cache, ct),
            AspxContextKind.TagName => TagNames(document, context),
            AspxContextKind.AttributeName => AttributeNames(document, context),
            AspxContextKind.AttributeValue => await AttributeValuesAsync(document, context, offset, ct),
            AspxContextKind.Directive => DirectiveAttributes(document, context),
            _ => Empty,
        };
    }

    // ---- Inline C# -------------------------------------------------------------------------

    private static async Task<CompletionList> CodeAsync(
        AspxDocument document, int offset, LspCompletionContext? trigger,
        LspResolveCache cache, CancellationToken ct)
    {
        // `Eval("…")` names a field of the bound item, not a C# symbol, so Roslyn has nothing to
        // say about it and the answer has to come from the container's item type instead.
        if (await DataBoundFieldsAsync(document, offset, ct) is { } fields)
            return fields;

        if (AspxProjectionService.Get(document) is not { } projection)
            return Empty;
        if (projection.ToProjected(offset) is not { } projected)
            return Empty;

        // A caret inside a string literal that holds another language — a resource key, a
        // configuration name — belongs to that language rather than to C#, exactly as it does in a
        // code-behind file. `CompletionHandler` makes this check in the overload that resolves a
        // URI, which markup never reaches: its C# lives in a projection, so the check has to be
        // made here and the spans carried back to the file the caret is really in.
        if (await EmbeddedAsync(document, offset, projection, projected, trigger, ct) is { } keys)
            return keys;

        return await CompletionHandler.CompleteAsync(
            projection.Document, projection.Text, projected, trigger, cache,
            span => projection.ToAspx(span) is { } mapped
                ? AspxLanguageHandler.ToRange(document, mapped)
                : null,
            ct);
    }

    /// <summary>
    /// The embedded language's list for a literal in the projection, with every edit mapped back
    /// into the markup. Null when the caret is in no literal a pack owns, or when an edit lands on
    /// generated text that the page has no place for.
    /// </summary>
    private static async Task<CompletionList?> EmbeddedAsync(
        AspxDocument document, int offset, AspxProjection projection, int projected,
        LspCompletionContext? trigger, CancellationToken ct)
    {
        if (await RoslynEmbeddedLanguages.Current.DetectForCompletionAsync(
                projection.Document, projected, ct) is not
            { Language: IEmbeddedCompletionProvider embedded } context)
        {
            return null;
        }

        var parameters = new CompletionParams(
            new TextDocumentIdentifier(LspConverters.PathToUri(document.FilePath)),
            LspConverters.ToPosition(document.SourceText.Lines.GetLinePosition(offset)),
            trigger);

        var list = await embedded.CompletionAsync(context, parameters, ct);
        if (list.Items.Length == 0)
            return list;

        var items = new List<CompletionItem>(list.Items.Length);
        foreach (var item in list.Items)
        {
            if (item.TextEdit is not { } edit)
            {
                items.Add(item);
                continue;
            }

            var span = LspConverters.ToTextSpan(projection.Text, edit.Range);
            if (projection.ToAspx(span) is not { } mapped)
                continue;

            items.Add(item with { TextEdit = edit with { Range = AspxLanguageHandler.ToRange(document, mapped) } });
        }

        return new CompletionList(list.IsIncomplete, [.. items]);
    }

    // ---- Tag names -------------------------------------------------------------------------

    private static CompletionList TagNames(AspxDocument document, AspxCompletionContext context)
    {
        var range = AspxLanguageHandler.ToRange(document, context.ReplaceSpan);

        // A caret inside a ParseChildren control completes that control's own nested elements —
        // its templates and sub-object properties, or the items of its collection — because that
        // is all ASP.NET accepts there. Everywhere else (top level, inside a template) the tag
        // is a control.
        if (NestedTagNames(document, context, range) is { } nested)
            return nested;

        // A committed control tag gets `runat="server"` written with it: without the attribute
        // the runtime treats the tag as literal text, so there is no tag one would complete and
        // then not want it on. Skipped when the tag already carries one — retyping a name must
        // not duplicate it.
        string suffix = HasRunat(document.Text, context.ReplaceSpan.End) ? "" : " runat=\"server\"";

        var items = AspxCatalog.Controls(document)
            .Select(entry =>
            {
                string label = $"{entry.Prefix}:{entry.TagName}";
                return new CompletionItem(
                    label,
                    LspCompletionItemKind.Class,
                    entry.SourcePath is null
                        ? entry.Type.ContainingNamespace?.ToDisplayString()
                        : Path.GetFileName(entry.SourcePath),
                    // Prefixes the file registered itself sort above the framework's.
                    (entry.SourcePath is null ? "1" : "0") + label,
                    label,
                    new TextEdit(range, label + suffix));
            })
            .ToArray();

        return items.Length == 0 ? Empty : new CompletionList(false, items);
    }

    /// <summary>
    /// The list for a tag that names a member of its container rather than a control, or
    /// <c>null</c> when the container takes ordinary controls.
    /// </summary>
    private static CompletionList? NestedTagNames(
        AspxDocument document, AspxCompletionContext context, Protocol.Range range)
    {
        switch (FindContainer(document, context.TagStart))
        {
            // Inside `<Columns>`: only what the collection's Add accepts fits.
            case CollectionNode collection:
                return ItemTags(document, collection.PropertyType, range) ?? Empty;

            case ControlNode { ParseChildren: true } control:
            {
                var items = new List<CompletionItem>();
                var type = control.ControlType;

                foreach (var property in AspxCatalog.ElementProperties(type))
                {
                    bool template = property.Type.IsTemplate();
                    items.Add(new CompletionItem(
                        property.Name,
                        LspCompletionItemKind.Property,
                        property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        // Templates first: they are where the markup goes.
                        (template ? "0" : "1") + property.Name,
                        property.Name,
                        new TextEdit(range, property.Name)));
                }

                // `[ParseChildren(true, "Items")]`: the items sit directly inside the control
                // tag with no property wrapper, so they complete beside its properties.
                if (type.DefaultCollectionProperty() is { } name
                    && type.GetMemberDeep(name)?.Type is INamedTypeSymbol collectionType
                    && ItemTags(document, collectionType, range) is { } defaultItems)
                {
                    items.AddRange(defaultItems.Items);
                }

                return items.Count == 0 ? Empty : new CompletionList(false, [.. items]);
            }

            default:
                return null;
        }
    }

    /// <summary>The types a collection property accepts as child tags, or <c>null</c> when its
    /// item type cannot be read off an <c>Add</c> method.</summary>
    private static CompletionList? ItemTags(
        AspxDocument document, INamedTypeSymbol collectionType, Protocol.Range range)
    {
        if (AspxCatalog.CollectionItemType(collectionType) is not { } itemType)
            return null;

        // No runat: a collection item (`<asp:BoundField>`) is a plain object, and ASP.NET
        // rejects the attribute on one.
        var items = AspxCatalog.CollectionItems(document, itemType)
            .Select(entry =>
            {
                string label = $"{entry.Prefix}:{entry.TagName}";
                return new CompletionItem(
                    label,
                    LspCompletionItemKind.Class,
                    entry.Type.ContainingNamespace?.ToDisplayString(),
                    "2" + label,
                    label,
                    new TextEdit(range, label));
            })
            .ToArray();

        return items.Length == 0 ? null : new CompletionList(false, items);
    }

    /// <summary>
    /// The element the tag being typed sits inside, or <c>null</c> at the top level. Found by
    /// range rather than by parent link: the half-typed tag itself has no node to ask.
    /// </summary>
    private static ElementNode? FindContainer(AspxDocument document, int tagStart)
    {
        if (document.Tree is not { } root || tagStart < 0)
            return null;

        ElementNode? found = null;

        foreach (var element in AspxSymbolResolver.EnumerateElements(root))
        {
            var start = element.StartTag.Range;

            if (start.Start.Offset >= tagStart || start.End.Offset > tagStart)
                continue;
            if (element.EndTag is { } end && end.Range.Start.Offset <= tagStart)
                continue;
            // An element with no end tag read to the end of the file — unless its start tag
            // closed itself, which the parser does not record and the source still shows.
            if (element.EndTag is null && IsSelfClosing(document.Text, start))
                continue;

            if (found is null || start.Start.Offset > found.StartTag.Range.Start.Offset)
                found = element;
        }

        return found;
    }

    private static bool IsSelfClosing(string text, WebFormsCore.Models.TokenRange startTag)
    {
        int end = Math.Min(startTag.End.Offset, text.Length);
        return end >= 2 && text[end - 2] == '/' && text[end - 1] == '>';
    }

    /// <summary>Whether the tag already carries a <c>runat</c> attribute past the caret's
    /// replace span.</summary>
    private static bool HasRunat(string text, int offset)
    {
        int i = offset;
        while (i < text.Length)
        {
            char c = text[i];

            if (c is '>' or '<')
                return false;

            if (c is '"' or '\'')
            {
                int end = text.IndexOf(c, i + 1);
                if (end < 0)
                    return false;
                i = end + 1;
                continue;
            }

            if (char.IsLetter(c))
            {
                int start = i;
                while (i < text.Length && IsAttributeChar(text[i]))
                    i++;
                if (text[start..i].Equals("runat", StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            i++;
        }

        return false;
    }

    // ---- Attribute names -------------------------------------------------------------------

    private static CompletionList AttributeNames(AspxDocument document, AspxCompletionContext context)
    {
        if (ResolveTagType(document, context) is not { } type)
            return Empty;

        var range = AspxLanguageHandler.ToRange(document, context.ReplaceSpan);
        bool hasValue = FollowedByValue(document.Text, context.ReplaceSpan.End);
        var written = WrittenAttributes(document.Text, context);

        var items = new List<CompletionItem>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // `ID` is offered up front because it is what a control tag almost always needs next,
        // and `Control` also declares it as an ordinary property — hence the dedupe.
        void Add(string name, int kind, string? detail, string sort, string? insert = null)
        {
            if (written.Contains(name) || !added.Add(name))
                return;
            items.Add(new CompletionItem(
                name, kind, detail, sort + name, name,
                new TextEdit(range, insert ?? Insertion(name, hasValue)))
            {
                InsertTextFormat = insert is not null || hasValue || !LspClientState.SnippetSupport
                    ? LspInsertTextFormat.PlainText
                    : LspInsertTextFormat.Snippet,
            });
        }

        // Only a real control carries these; a collection item like a grid column is a plain
        // object with neither.
        if (type.IsAssignableTo("Control"))
        {
            Add("ID", LspCompletionItemKind.Property, "control identifier", "0");
            // Value and all: `server` is the only value the attribute takes, so there is nothing
            // for a caret between the quotes to decide.
            Add("runat", LspCompletionItemKind.Property, "server", "0",
                insert: hasValue ? null : "runat=\"server\"");
        }

        // Events first: wiring one up is the reason to open completion inside a control tag,
        // and there are far fewer of them than there are properties.
        foreach (var @event in AspxCatalog.Events(type))
            Add("On" + @event.Name, LspCompletionItemKind.Event, @event.Type.ToDisplayString(), "1");

        foreach (var property in AspxCatalog.WritableProperties(type))
            Add(property.Name, LspCompletionItemKind.Property, property.Type.ToDisplayString(), "2");

        return items.Count == 0 ? Empty : new CompletionList(false, items.ToArray());
    }

    private static string Insertion(string name, bool hasValue) =>
        hasValue ? name
        : LspClientState.SnippetSupport ? $"{name}=\"$0\""
        : $"{name}=\"\"";

    /// <summary>True when an <c>=</c> already follows, so the item must insert the name alone
    /// rather than a second <c>=""</c>.</summary>
    private static bool FollowedByValue(string text, int offset)
    {
        for (int i = offset; i < text.Length; i++)
        {
            if (text[i] == '=') return true;
            if (!char.IsWhiteSpace(text[i])) return false;
        }
        return false;
    }

    /// <summary>Attributes already on the tag, so completion does not offer them twice.</summary>
    private static HashSet<string> WrittenAttributes(string text, AspxCompletionContext context)
    {
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (context.TagStart < 0)
            return written;

        int i = context.TagStart + 1;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '>')
            i++;

        while (i < text.Length && text[i] != '>')
        {
            char c = text[i];

            if (c is '"' or '\'')
            {
                int end = text.IndexOf(c, i + 1);
                if (end < 0) break;
                i = end + 1;
                continue;
            }

            if (!char.IsLetter(c) && c != '_')
            {
                i++;
                continue;
            }

            int start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '-' or ':'))
                i++;

            // The attribute being typed is not "already written" — excluding it would empty the
            // list the moment the user typed a character that matches one.
            if (!(start <= context.ReplaceSpan.Start && i >= context.ReplaceSpan.End))
                written.Add(text[start..i]);
        }

        return written;
    }

    // ---- Attribute values ------------------------------------------------------------------

    /// <summary>
    /// An implicit-localization key names a group of resx entries rather than a value of the
    /// attribute it is written beside, so it is answered from the resource catalog before the
    /// control's own type is consulted — nothing on the control binds it.
    /// </summary>
    private static async Task<CompletionList> AttributeValuesAsync(
        AspxDocument document, AspxCompletionContext context, int offset, CancellationToken ct)
    {
        // Ahead of everything else: a configured format attribute is an ordinary string property
        // on the control, so the branch below would resolve it, find no enum behind it and offer
        // nothing — which is exactly the caret where the components are worth having.
        if (await FormatComponentsAsync(document, offset, ct) is { } components)
            return components;

        // And for the same reason one line up: a configured member attribute — a grid's
        // `SortExpression` — is an ordinary string property on the control, so nothing below has
        // anything to offer at a caret where the bound item's fields are the whole answer. The
        // `Eval("…")` list, at the one position the code branch never reaches.
        //
        // Matched against the scanned context rather than against the parse tree: an attribute
        // whose value is still empty is the caret completion exists for, and the parser gives that
        // value no range at all.
        if (context is { TagName: { } tagName, AttributeName: { Length: > 0 } attribute }
            && MarkupBindingSettings.Current.For(context.TagPrefix, tagName, attribute)
                is { Kind: MarkupBindingKind.Member }
            && await DataBoundFieldsAsync(document, context.ReplaceSpan, offset, ct) is { } fields)
        {
            return fields;
        }

        if (context.AttributeName is { Length: > 0 } name
            && AspxSymbolResolver.IsImplicitKeyAttribute(name))
        {
            return await AspxResourceHandler.ImplicitKeysAsync(
                document, context, ResolveTagType(document, context), ct);
        }

        return AttributeValues(document, context);
    }

    private static CompletionList AttributeValues(AspxDocument document, AspxCompletionContext context)
    {
        if (context.AttributeName is not { Length: > 0 } attributeName)
            return Empty;

        var range = AspxLanguageHandler.ToRange(document, context.ReplaceSpan);

        if (attributeName.Equals("runat", StringComparison.OrdinalIgnoreCase))
        {
            return new CompletionList(false,
            [
                new CompletionItem("server", LspCompletionItemKind.Value, null, "0server", "server",
                    new TextEdit(range, "server")),
            ]);
        }

        // Answered before the tag type is resolved: a page whose master lives in another project
        // still knows which placeholders that master declares.
        if (attributeName.Equals("ContentPlaceHolderID", StringComparison.OrdinalIgnoreCase))
            return ContentPlaceHolders(document, range);

        if (ResolveTagType(document, context) is not { } type)
            return Empty;

        if (AspxSymbolResolver.TryGetEvent(type, attributeName) is { } @event)
            return HandlerNames(document, context, @event, range);

        var property = ResolveProperty(type, attributeName);
        if (property is null)
            return Empty;

        var valueType = UnwrapNullable(property.Type);

        if (valueType.SpecialType == SpecialType.System_Boolean)
        {
            return new CompletionList(false,
            [
                Value("True", range),
                Value("False", range),
            ]);
        }

        if (valueType.TypeKind != TypeKind.Enum)
            return Empty;

        var items = valueType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.HasConstantValue)
            .Select(f => Value(f.Name, range))
            .ToArray();

        return items.Length == 0 ? Empty : new CompletionList(false, items);
    }

    /// <summary>
    /// The specifier components, for a caret inside a configured format attribute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null rather than empty when the caret is somewhere else, so the ordinary attribute-value
    /// list still answers. Inside the specifier and nowhere else: a caret on the index of
    /// <c>{0:…}</c> is choosing which value to print, and the components would be the wrong list.
    /// </para>
    /// <para>
    /// The replace span is the component run under the caret rather than the whole value, which is
    /// what makes retyping half of one work — <c>dd-M|M</c> replaces the <c>MM</c> and leaves the
    /// rest of the date alone. The attribute-wide span the scanner computes would delete it.
    /// </para>
    /// </remarks>
    private static async Task<CompletionList?> FormatComponentsAsync(
        AspxDocument document, int offset, CancellationToken ct)
    {
        if (await MarkupFormatSites.AtAsync(document, offset, ct) is not { } format)
            return null;

        int inside = offset - format.Value.Start;

        if (FormatString.HoleAt(FormatString.Holes(format.Text), inside) is not { } hole
            || inside < hole.Specifier.Start || inside > hole.Specifier.End)
        {
            return null;
        }

        string specifier = format.Text[hole.Specifier.Start..hole.Specifier.End];
        var parts = FormatString.Parts(specifier, format.Family);

        var replaced = FormatString.PartAt(parts, inside - hole.Specifier.Start) is
            { Kind: not (FormatPartKind.Literal or FormatPartKind.Escape) } run
            ? new TextSpan(
                format.Value.Start + hole.Specifier.Start + run.Span.Start, run.Span.Length)
            : new TextSpan(offset, 0);

        var range = AspxLanguageHandler.ToRange(document, replaced);
        var items = new List<CompletionItem>();
        int order = 0;

        foreach (var component in FormatString.Components(format.Family))
        {
            string detail = FormatString.Example(component.Text, format.Family) is { } example
                ? $"{component.Description} — {example}"
                : component.Description;

            items.Add(new CompletionItem(
                component.Text, LspCompletionItemKind.EnumMember, detail,
                order++.ToString("D2"), component.Text,
                new TextEdit(range, component.Text)));
        }

        return new CompletionList(false, [.. items]);
    }

    private static CompletionItem Value(string text, Protocol.Range range) =>
        new(text, LspCompletionItemKind.EnumMember, null, "0" + text, text, new TextEdit(range, text));

    /// <summary>
    /// Handler names for an event attribute: the methods already on the code-behind that fit
    /// the delegate, plus the one the designer would have created. Committing the generated one
    /// writes the method into the code-behind through
    /// <see cref="ExecuteCommandHandler.GenerateEventHandlerCommand"/>.
    /// </summary>
    private static CompletionList HandlerNames(
        AspxDocument document, AspxCompletionContext context, IEventSymbol @event, Protocol.Range range)
    {
        var items = new List<CompletionItem>();

        if (FindControl(document, context) is { } control)
        {
            string generated = AspxEventHandlerService.SuggestName(control, @event, document.CodeBehind);
            items.Add(new CompletionItem(
                generated,
                LspCompletionItemKind.Method,
                $"generate handler in {Path.GetFileName(document.FilePath)}.cs",
                "0" + generated,
                generated,
                new TextEdit(range, generated))
            {
                Preselect = true,
                Command = new Command(
                    "Generate event handler",
                    ExecuteCommandHandler.GenerateEventHandlerCommand,
                    [
                        LspConverters.PathToUri(document.FilePath),
                        control.StartTag.Range.Start.Offset,
                        "On" + @event.Name,
                        generated,
                    ]),
            });
        }

        foreach (var method in AspxEventHandlerService.CompatibleHandlers(document.CodeBehind, @event))
        {
            items.Add(new CompletionItem(
                method.Name,
                LspCompletionItemKind.Method,
                method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                "1" + method.Name,
                method.Name,
                new TextEdit(range, method.Name)));
        }

        return items.Count == 0 ? Empty : new CompletionList(false, items.ToArray());
    }

    /// <summary>
    /// The placeholders the page's master page declares. Read out of the master itself rather
    /// than offered as free text: an <c>&lt;asp:Content&gt;</c> naming a placeholder the master
    /// does not have throws at runtime, and the name is the only thing tying the two files
    /// together.
    /// </summary>
    private static CompletionList ContentPlaceHolders(AspxDocument document, Protocol.Range range)
    {
        if (MasterPage(document) is not { } master)
            return Empty;

        string detail = Path.GetFileName(master.Path);

        var items = AspxSymbolResolver.EnumerateElements(master.Root)
            .Where(e => e.Name.Value.Equals("ContentPlaceHolder", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.RawAttributes.TryGetValue("ID", out var id) ? id.Value : string.Empty)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => new CompletionItem(
                id, LspCompletionItemKind.Value, detail, "0" + id, id,
                new TextEdit(range, id)))
            .ToArray();

        return items.Length == 0 ? Empty : new CompletionList(false, items);
    }

    /// <summary>
    /// The master page the <c>MasterPageFile</c> attribute names, parsed. Parsed here rather than
    /// loaded through <see cref="AspxDocumentService"/> because a master is a file on disk before
    /// it is a project item, and this has to work in a project that never lists it.
    /// </summary>
    private static (string Path, RootNode Root)? MasterPage(AspxDocument document)
    {
        if (document.Tree is not { } root)
            return null;

        foreach (var directive in root.Directives)
        {
            foreach (var (key, value) in directive.Attributes)
            {
                if (!key.Value.Equals("MasterPageFile", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (AspxSymbolResolver.ResolvePath(document, value.Value) is not { } path)
                    return null;

                string? text = ReadFile(path);
                if (text is null)
                    return null;

                var parse = AspxSourceMappingService.Parse(
                    path, text, document.Compilation,
                    rootDirectory: Path.GetDirectoryName(document.Project.FilePath));

                return parse.ParseTree is { } tree ? (path, tree) : null;
            }
        }

        return null;
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- Directives ------------------------------------------------------------------------

    /// <summary>
    /// What <c>&lt;%@ Register %&gt;</c> accepts. Two registrations share the directive — a user
    /// control (<c>TagPrefix</c> + <c>TagName</c> + <c>Src</c>) and a namespace
    /// (<c>TagPrefix</c> + <c>Namespace</c> + <c>Assembly</c>) — so all five are offered and the
    /// detail says which shape each belongs to.
    /// </summary>
    private static readonly (string Name, string Detail)[] s_registerAttributes =
    [
        ("TagPrefix", "prefix the controls are written under"),
        ("TagName", "tag name for a single user control"),
        ("Src", "path to the .ascx being registered"),
        ("Namespace", "namespace whose controls are registered"),
        ("Assembly", "assembly that namespace lives in"),
    ];

    private static CompletionList DirectiveAttributes(
        AspxDocument document, AspxCompletionContext context)
    {
        if (context.TagStart < 0)
            return Empty;

        if (ScanDirective(document.Text, context.TagStart, context.ReplaceSpan.Start)
            is not { InValue: false } directive)
            return Empty;

        if (!directive.Name.Equals("Register", StringComparison.OrdinalIgnoreCase))
            return Empty;

        var range = AspxLanguageHandler.ToRange(document, directive.ReplaceSpan);
        bool hasValue = FollowedByValue(document.Text, directive.ReplaceSpan.End);

        var items = s_registerAttributes
            .Where(attribute => !directive.Written.Contains(attribute.Name))
            .Select((attribute, index) => new CompletionItem(
                attribute.Name,
                LspCompletionItemKind.Property,
                attribute.Detail,
                index.ToString("D2"),
                attribute.Name,
                new TextEdit(range, Insertion(attribute.Name, hasValue)))
            {
                InsertTextFormat = hasValue || !LspClientState.SnippetSupport
                    ? LspInsertTextFormat.PlainText
                    : LspInsertTextFormat.Snippet,
            })
            .ToArray();

        return items.Length == 0 ? Empty : new CompletionList(false, items);
    }

    /// <summary>Where the caret sits inside a <c>&lt;%@ … %&gt;</c> directive.</summary>
    /// <param name="Name">The directive's own name — <c>Page</c>, <c>Register</c>, …</param>
    /// <param name="ReplaceSpan">The attribute name a committed item replaces.</param>
    /// <param name="InValue">Whether the caret is inside a quoted value instead.</param>
    /// <param name="Written">The attributes already on the directive.</param>
    private sealed record AspxDirectiveContext(
        string Name, TextSpan ReplaceSpan, bool InValue, HashSet<string> Written);

    /// <summary>
    /// Scans a directive the way <see cref="AspxCompletionContextScanner"/> scans a tag. A
    /// directive being typed does not parse, so the tree cannot answer this either.
    /// </summary>
    private static AspxDirectiveContext? ScanDirective(string text, int start, int offset)
    {
        int i = start + "<%@".Length;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;

        int nameStart = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            i++;

        // Still typing the directive's own name, which is not what this offers.
        if (i == nameStart || i >= offset)
            return null;

        string name = text[nameStart..i];
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (i < offset && i < text.Length)
        {
            char c = text[i];

            if (c == '%' && i + 1 < text.Length && text[i + 1] == '>')
                return null;

            if (char.IsWhiteSpace(c) || c == '=')
            {
                i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                int valueEnd = text.IndexOf(c, i + 1);
                if (valueEnd < 0 || valueEnd >= offset)
                    return new AspxDirectiveContext(name, default, InValue: true, written);

                i = valueEnd + 1;
                continue;
            }

            int attributeStart = i;
            while (i < text.Length && IsAttributeChar(text[i]))
                i++;

            if (i == attributeStart)
            {
                i++;
                continue;
            }

            // The attribute being typed is not "already written" — excluding it would empty the
            // list the moment the user typed a character that matches one.
            if (i >= offset)
            {
                int attributeEnd = i;
                while (attributeEnd < text.Length && IsAttributeChar(text[attributeEnd]))
                    attributeEnd++;

                return new AspxDirectiveContext(
                    name, TextSpan.FromBounds(attributeStart, attributeEnd), InValue: false, written);
            }

            written.Add(text[attributeStart..i]);
        }

        return new AspxDirectiveContext(name, new TextSpan(offset, 0), InValue: false, written);
    }

    private static bool IsAttributeChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';

    // ---- Data binding ----------------------------------------------------------------------

    /// <summary>
    /// Field names inside <c>Eval("…")</c> or <c>Bind("…")</c>, or <c>null</c> when the caret is
    /// not in one. An empty list rather than <c>null</c> when the container's item type is not
    /// statically known: the alternative is guessing from names seen elsewhere in the page, and a
    /// wrong field here fails at runtime rather than at build.
    /// </summary>
    private static async Task<CompletionList?> DataBoundFieldsAsync(
        AspxDocument document, int offset, CancellationToken ct)
    {
        if (DataBindingService.ArgumentAt(document.Text, offset) is not { } span)
            return null;

        return await DataBoundFieldsAsync(document, span, offset, ct);
    }

    /// <summary>
    /// The same list for a path written somewhere else — the value of an attribute the
    /// configuration reads as a member path, whose span the caller found for itself.
    /// </summary>
    private static async Task<CompletionList?> DataBoundFieldsAsync(
        AspxDocument document, TextSpan span, int offset, CancellationToken ct)
    {
        // The path's own dots are walked too, so `Eval("Buyer.` offers the members of Buyer
        // rather than the members of the item all over again.
        var itemType = await DataBindingService.ItemTypeAsync(document, offset, ct);
        var segments = DataBindingService.Segments(document.Text, span, itemType);

        if (DataBindingService.SegmentAt(segments, offset) is not { } segment)
            return Empty;

        int index = segments.IndexOf(segment);

        // What the segment before it resolved to, or the item itself at the head of the path. A
        // segment after one that resolved to nothing has no scope to offer, which is not the same
        // as an empty one.
        var scope = index == 0
            ? itemType
            : segments[index - 1].Symbol is { } previous
                ? DataBindingService.MemberType(previous)
                : null;

        if (scope is null)
            return Empty;

        var range = AspxLanguageHandler.ToRange(document, segment.Span);

        var items = DataBindingService.Fields(scope)
            .Select(member => new CompletionItem(
                member.Name,
                member is IPropertySymbol ? LspCompletionItemKind.Property : LspCompletionItemKind.Field,
                DataBindingService.MemberType(member)?
                    .ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? string.Empty,
                "0" + member.Name,
                member.Name,
                new TextEdit(range, member.Name)))
            .ToArray();

        return items.Length == 0 ? Empty : new CompletionList(false, items);
    }


    // ---- Shared ----------------------------------------------------------------------------

    /// <summary>
    /// The control type for the tag the caret is in. The parse tree wins when it has a node for
    /// the tag; a tag still being typed has none, and is resolved from its written prefix.
    /// </summary>
    private static INamedTypeSymbol? ResolveTagType(AspxDocument document, AspxCompletionContext context)
    {
        if (FindControl(document, context) is { } control)
            return control.ControlType;

        return context.TagName is { Length: > 0 } name
            ? AspxCatalog.ResolveTag(document, context.TagPrefix, name)
            : null;
    }

    private static ControlNode? FindControl(AspxDocument document, AspxCompletionContext context)
    {
        if (document.Tree is not { } root || context.TagStart < 0)
            return null;

        return AspxSymbolResolver.EnumerateControls(root)
            .FirstOrDefault(c => c.StartTag.Range.Start.Offset == context.TagStart);
    }

    private static IPropertySymbol? ResolveProperty(INamedTypeSymbol type, string attributeName)
    {
        ITypeSymbol current = type;
        IPropertySymbol? resolved = null;

        foreach (string segment in attributeName.Split('-'))
        {
            resolved = AspxCatalog.WritableProperties((INamedTypeSymbol)current)
                .Concat(AspxCatalog.ComplexProperties((INamedTypeSymbol)current))
                .FirstOrDefault(p => p.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));

            if (resolved is null || resolved.Type is not INamedTypeSymbol next)
                return resolved;

            current = next;
        }

        return resolved;
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } named
            ? named.TypeArguments[0]
            : type;
}
