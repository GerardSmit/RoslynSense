using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.Dbml;

internal sealed partial class DbmlLanguage :
    ILanguageDefinitionProvider,
    ILanguageReferencesProvider
{
    /// <summary>
    /// Where the C# behind the caret is: the property, the entity class or the context method
    /// SqlMetal generated for the element.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one navigation in the pack that goes <em>into</em> the designer on purpose. The
    /// contributors work hard to keep F12 in C# out of that file, and both are right: from C# the
    /// designer is a restatement of the model and the model is the answer, while from the model the
    /// designer is the only thing there is to point at — it is where the member the rest of the
    /// solution calls actually lives.
    /// </para>
    /// <para>
    /// <paramref name="typeDefinition"/> is not distinguished. A column's type is
    /// <c>System.Int32</c>, which is metadata rather than anything the model declares, so answering
    /// the same location for both is better than answering nothing for one.
    /// </para>
    /// <para>
    /// Empty rather than approximate when nothing bound. A project that has never been built has no
    /// designer to open, and the file SqlMetal would have written is not a file that exists.
    /// </para>
    /// </remarks>
    public async Task<LspLocation[]> DefinitionAsync(
        TextDocumentPositionParams p, bool typeDefinition, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (view, offset))
            return [];

        // A reference first, because a declaration always encloses one: the caret in the value of
        // `ThisKey="CustomerId"` is inside the <Association> as well, and answering with the
        // association would be answering with the element the reader is already looking at.
        if (DbmlReferences.At(view.Document, offset) is { } reference)
            return await ReferenceTargetAsync(view, reference, ct);

        if (DbmlSymbolResolver.ResolveAt(view, offset) is not { Symbol: { } symbol }
            || view.Project is not { } project)
        {
            return [];
        }

        return await NavigationHandlers.DefinitionLocationsAsync(symbol, project, typeDefinition: false, ct);
    }

    /// <summary>
    /// Where a name written inside an attribute is declared.
    /// </summary>
    /// <remarks>
    /// Three of the four kinds are resolved inside this file and the fourth in C#, which is the split
    /// the model itself draws: an association's <c>Type</c> and its two key lists name things the
    /// <c>.dbml</c> declares further down, while a column's <c>Type</c> names a CLR type that has
    /// nothing to do with the model and everything to do with the compilation.
    /// </remarks>
    private static async Task<LspLocation[]> ReferenceTargetAsync(
        DbmlView view, DbmlReference reference, CancellationToken ct)
    {
        if (reference.Kind is DbmlReferenceKind.ClrType)
        {
            return view.Project is { } owner && view.Index.Compilation is { } compilation
                   && ResolveClrType(compilation, reference.Name) is { } type
                ? await NavigationHandlers.DefinitionLocationsAsync(
                    type, owner, typeDefinition: false, ct)
                : [];
        }

        var types = view.Database.AllTypes();

        IDbmlDeclaration? target = reference.Kind switch
        {
            DbmlReferenceKind.ModelType => TypeNamed(types, reference.Name),

            DbmlReferenceKind.ThisKeyColumn =>
                Column(TypeNamed(types, reference.OwnerTypeName), reference.Name),

            DbmlReferenceKind.OtherKeyColumn =>
                Column(TypeNamed(types, reference.TargetTypeName), reference.Name),

            _ => null,
        };

        return target is null
            ? []
            : [new LspLocation(
                LspConverters.PathToUri(view.FilePath), ToRange(view.Text, target.SelectionSpan))];
    }

    private static DbmlType? TypeNamed(IEnumerable<DbmlType> types, string name) =>
        name.Length == 0
            ? null
            : types.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <remarks>
    /// By the column's <c>Name</c> and not its <c>Member</c>, because a key list is written in the
    /// database's vocabulary — it is the constraint's columns, which is why it survives a model that
    /// renamed the property.
    /// </remarks>
    private static DbmlColumn? Column(DbmlType? type, string name) => type?.ColumnNamed(name);

    /// <summary>
    /// The CLR type a <c>Type=</c> attribute names, resolved against the project's compilation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>global::</c> is stripped because a model routinely writes it — SqlMetal emits the prefix for
    /// any type outside <c>System</c> — and it is a C# spelling that metadata names never carry.
    /// </para>
    /// <para>
    /// The <c>+</c> retry is for nested types, which the model writes with a dot the way source does
    /// and metadata spells with a plus. Trying the rightmost dot first and walking left resolves
    /// <c>Ns.Outer.Inner</c> without knowing where the namespace stops.
    /// </para>
    /// <para>
    /// The rest is about spellings a person writes and a generator does not. SqlMetal emits
    /// <c>System.Int32</c>, but a hand-edited model reasonably says <c>int</c>, <c>int?</c>,
    /// <c>System.Byte[]</c> or an assembly-qualified name, and none of those is a metadata name.
    /// Every one of them is a type that exists, so failing to resolve it is a wrong answer — which
    /// used to cost only a missing colour and now costs a red squiggle.
    /// </para>
    /// <para>
    /// The plural lookup at the end is for a name two referenced assemblies both define: the
    /// singular overload answers <c>null</c> for an ambiguity, which reads as "no such type" and is
    /// the one false negative here that has nothing to do with how the name was written.
    /// </para>
    /// </remarks>
    internal static INamedTypeSymbol? ResolveClrType(Compilation compilation, string typeName)
    {
        string name = typeName.Trim();

        if (name.StartsWith("global::", StringComparison.Ordinal))
            name = name["global::".Length..];

        // "Ns.T, MyAssembly, Version=..." — the assembly is the runtime's business, not the
        // compilation's, and the name in front of the comma is the whole question.
        int qualifier = name.IndexOf(',');
        if (qualifier >= 0)
            name = name[..qualifier].TrimEnd();

        // A .dbml carries nullability in CanBeNull, so a "?" here is someone writing C#.
        name = name.TrimEnd('?').TrimEnd();

        // An array of a type that exists is a type that exists, and the element is what every
        // caller wants anyway: the TypeKind to colour by, and the declaration to open.
        while (name.EndsWith("[]", StringComparison.Ordinal))
            name = name[..^2].TrimEnd();

        if (name.Length == 0)
            return null;

        if (s_keywordTypes.TryGetValue(name, out var special))
            return compilation.GetSpecialType(special);

        if (compilation.GetTypeByMetadataName(name) is { } direct)
            return direct;

        var candidate = name.ToCharArray();

        for (int index = name.LastIndexOf('.'); index > 0; index = name.LastIndexOf('.', index - 1))
        {
            candidate[index] = '+';

            if (compilation.GetTypeByMetadataName(new string(candidate)) is { } nested)
                return nested;
        }

        return compilation.GetTypesByMetadataName(name) is [var ambiguous, ..] ? ambiguous : null;
    }

    /// <summary>The C# names for the special types, which are not metadata names.</summary>
    private static readonly Dictionary<string, SpecialType> s_keywordTypes = new(StringComparer.Ordinal)
    {
        ["bool"] = SpecialType.System_Boolean,
        ["byte"] = SpecialType.System_Byte,
        ["sbyte"] = SpecialType.System_SByte,
        ["char"] = SpecialType.System_Char,
        ["decimal"] = SpecialType.System_Decimal,
        ["double"] = SpecialType.System_Double,
        ["float"] = SpecialType.System_Single,
        ["int"] = SpecialType.System_Int32,
        ["uint"] = SpecialType.System_UInt32,
        ["long"] = SpecialType.System_Int64,
        ["ulong"] = SpecialType.System_UInt64,
        ["short"] = SpecialType.System_Int16,
        ["ushort"] = SpecialType.System_UInt16,
        ["object"] = SpecialType.System_Object,
        ["string"] = SpecialType.System_String,
    };

    /// <summary>
    /// Every use of the element's generated member across the solution, with the designer's own
    /// mentions of it kept out.
    /// </summary>
    /// <remarks>
    /// Solution-wide by construction rather than by choice: a <c>DataContext</c> lives in a
    /// data-access project and is queried from everywhere else, so a project-scoped search would find
    /// the designer and nothing more — which is exactly what the filter in
    /// <see cref="DbmlReferenceService"/> removes.
    /// </remarks>
    public async Task<LspLocation[]> ReferencesAsync(ReferenceParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (view, offset)
            || DbmlSymbolResolver.ResolveAt(view, offset) is not { } hit
            || view.Project is not { } project)
        {
            return [];
        }

        return await UsageLocationsAsync(
            await DbmlReferenceService.FindUsagesAsync(
                hit, view, ct, DbmlReferenceService.ExplicitSearchBudget),
            project, p.Context.IncludeDeclaration, ct);
    }

    /// <summary>
    /// Usages as locations the editor can open.
    /// </summary>
    /// <remarks>
    /// Through the usage's syntax tree rather than its file path, and then through
    /// <see cref="HandlerHelpers.ToLocationsAsync"/>: a result that landed in a source-generated
    /// document has no file to open, and that helper is what registers it under a URI scheme the
    /// client can fetch. A row standing in for the model's own line has no document and needs
    /// neither, being a file on disk already. The code lens uses this too, so a peek from the gutter
    /// and a Shift+F12 cannot disagree about where a result is.
    /// </remarks>
    internal static async Task<LspLocation[]> UsageLocationsAsync(
        IEnumerable<DbmlUsage> usages, Project project, bool includeDefinitions, CancellationToken ct)
    {
        var locations = new List<Microsoft.CodeAnalysis.Location>();
        var declarations = new List<LspLocation>();

        foreach (var usage in usages)
        {
            if (usage.IsDefinition && !includeDefinitions)
                continue;

            if (usage.Document is { } document)
            {
                if (await document.GetSyntaxTreeAsync(ct) is { } tree)
                    locations.Add(Microsoft.CodeAnalysis.Location.Create(tree, usage.Span));
            }
            else if (usage.Text is { } text)
            {
                declarations.Add(new LspLocation(
                    LspConverters.PathToUri(usage.FilePath), ToRange(text, usage.Span)));
            }
        }

        return [.. await HandlerHelpers.ToLocationsAsync(locations, project, ct), .. declarations];
    }

    private static async Task<(DbmlView View, int Offset)?> ResolveAsync(
        TextDocumentIdentifier textDocument, Position position, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(textDocument.Uri);

        if (await DbmlWorkspace.GetAsync(path, ct) is not { } view)
            return null;

        return (view, LspConverters.ToOffset(view.Text, position));
    }

    private static LspRange ToRange(SourceText text, TextSpan span) =>
        LspConverters.ToRange(text.Lines, Clamp(text, span));

    /// <summary>
    /// A span measured against text that has since been edited still has to produce a range inside
    /// the buffer, or the client drops the whole response.
    /// </summary>
    private static TextSpan Clamp(SourceText text, TextSpan span)
    {
        int start = Math.Clamp(span.Start, 0, text.Length);
        int end = Math.Clamp(span.End, start, text.Length);
        return TextSpan.FromBounds(start, end);
    }
}
