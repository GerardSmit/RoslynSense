using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMCP.Languages.Dbml.Core;

/// <summary>The <c>.dbml</c> declaration a generated C# symbol came from.</summary>
/// <param name="Key">The declaration's <see cref="IDbmlDeclaration.Key"/>, which the caller turns
/// back into a declaration with a span once it has parsed the model itself.</param>
/// <remarks>
/// A path and a key rather than a declaration, for the reason <c>ProtoDeclarationRef</c> gives: the
/// index reads the file the compilation was built from, and the caller is usually looking at an
/// editor buffer. Handing back the index's own objects would give the caller spans measured against
/// text it is not showing.
/// </remarks>
internal readonly record struct DbmlDeclarationRef(
    string DbmlPath, string Key, DbmlDeclarationKind Kind);

/// <summary>
/// The bridge between one <c>.dbml</c> and the C# SqlMetal generated from it: which
/// <see cref="ISymbol"/> stands for each declaration in the model, and the way back.
/// </summary>
/// <remarks>
/// <para>
/// There is no projection here, the same as the protobuf pack. SqlMetal writes a real
/// <c>.designer.cs</c> that MSBuild compiles as an ordinary <c>Compile</c> item, so Roslyn has
/// already bound it and every navigation feature reduces to plain <c>SymbolFinder</c> once a model
/// declaration has been turned into a symbol. Turning it into a symbol is the only hard part, and it
/// is what this type does.
/// </para>
/// <para>
/// Nothing here predicts a name. Every binding reads an anchor SqlMetal left in its own output — the
/// <c>[Table(Name=…)]</c> on an entity class, the <c>[Column(Name=…)]</c> on a property, the
/// <c>[Association(Name=…)]</c> on a relationship, the <c>[Function(Name=…)]</c> on a context method
/// — so the C# name is discovered rather than guessed. Two of those anchors have edges worth knowing:
/// </para>
/// <list type="bullet">
/// <item>
/// SqlMetal <b>omits</b> <c>Name</c> from <c>[Column]</c> when the column name already equals the
/// member name, so an attribute with no name is not an unbound column — it is a column named after
/// its property.
/// </item>
/// <item>
/// An <c>[Association]</c>'s <c>Name</c> is <b>the same on both ends</b> of a relationship, since it
/// is the foreign key's name. The property name is what tells the two ends apart, which is why
/// associations are keyed on their member and not on their name.
/// </item>
/// </list>
/// <para>
/// The generated file is found by path derivation rather than by scanning the project, because
/// <c>DbmlDesignerGenerator</c> already fixes the relationship in the forward direction. That
/// derivation is ambiguous going backwards — see <see cref="DbmlSourceMappingService"/> — so the
/// binding is what confirms the file really is this model's output, and
/// <see cref="DbmlSourceMappingService.NoteBound"/> is called only once something bound.
/// </para>
/// <para>
/// A built index is memoized against the project's dependent semantic version and the model's own
/// checksum, the way <c>ProtoGeneratedIndex</c> keys itself: every binding here is made from
/// declarations and attributes, which a method-body edit does not move, while the model's text
/// decides the keys a caller will ask about. The cost is the same too — <see cref="Compilation"/> can
/// be a snapshot the solution has since retired, so a caller about to hand one of these symbols to
/// <c>SymbolFinder</c> re-anchors it first; see <see cref="DbmlReferenceService"/>.
/// </para>
/// </remarks>
internal sealed class DbmlGeneratedIndex
{
    /// <summary>The index of a model whose designer is missing, unbuilt, or not SqlMetal's.</summary>
    public static readonly DbmlGeneratedIndex Empty = new();

    private readonly Dictionary<string, ISymbol> _byKey = new(StringComparer.Ordinal);

    private readonly Dictionary<ISymbol, DbmlDeclarationRef> _reverse =
        new(SymbolEqualityComparer.Default);

    private DbmlGeneratedIndex()
    {
    }

    /// <summary>The model this index was built for, absolute and normalised.</summary>
    public string DbmlPath { get; private init; } = string.Empty;

    /// <summary>SqlMetal's output for that model, or empty when none was found.</summary>
    public string DesignerPath { get; private init; } = string.Empty;

    /// <summary>
    /// The compilation every <see cref="ISymbol"/> in here came from, or <c>null</c> for
    /// <see cref="Empty"/>.
    /// </summary>
    /// <remarks>
    /// Exposed so a caller about to search can tell whether the index has drifted from the snapshot
    /// it is searching: the index survives a method-body edit and the compilation does not, so the
    /// two part company routinely. Compilations are snapshots, so reference equality is the test.
    /// </remarks>
    internal Compilation? Compilation { get; private init; }

    /// <summary>Whether nothing bound — the never-generated case, in which every lookup is null.</summary>
    public bool IsEmpty => _byKey.Count == 0;

    /// <summary>The C# member SqlMetal generated for one model declaration.</summary>
    public ISymbol? SymbolFor(IDbmlDeclaration declaration) => SymbolFor(declaration.Key);

    /// <inheritdoc cref="SymbolFor(IDbmlDeclaration)"/>
    public ISymbol? SymbolFor(string key) =>
        _byKey.TryGetValue(key, out var found) ? found : null;

    /// <summary>
    /// The model declaration a generated symbol came from, or <c>null</c> when the symbol is not
    /// SqlMetal's output.
    /// </summary>
    /// <remarks>
    /// Keyed on symbol identity alone, unlike the protobuf index which also keys on the documentation
    /// comment id. It can be: this map is only ever read by the definition contributor, which starts
    /// from <c>DeclaringSyntaxReferences</c> and therefore always holds a source symbol from the
    /// project that declares it. Nothing asks it about the retargeted symbol a consuming project
    /// would see, because every feature that starts in a consumer starts from the model side and
    /// searches outward instead.
    /// </remarks>
    public DbmlDeclarationRef? DeclarationFor(ISymbol symbol)
    {
        var definition = symbol.OriginalDefinition;

        if (_reverse.TryGetValue(definition, out var found))
            return found;

        // `new Product()` binds to the constructor SqlMetal emitted, not to the class, and a
        // constructor stands for no model declaration of its own. Constructors only: every other
        // member of an entity class stands for something, and walking out to the containing type
        // would answer "the entity" for a caret on one of its columns.
        return definition is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } owner }
               && _reverse.TryGetValue(owner.OriginalDefinition, out var declaring)
            ? declaring
            : null;
    }

    /// <summary>Whether a document is the designer this index bound, and therefore a pass-through.</summary>
    public bool IsGenerated(Document document) =>
        document.FilePath is { Length: > 0 } path && IsGenerated(path);

    /// <inheritdoc cref="IsGenerated(Document)"/>
    public bool IsGenerated(string filePath) =>
        DesignerPath.Length > 0
        && string.Equals(DesignerPath, DbmlDocumentCache.Normalize(filePath), StringComparison.OrdinalIgnoreCase);

    // ---- Entry point ----------------------------------------------------------------------------

    private sealed record CacheEntry(VersionStamp SemanticVersion, string Fingerprint, DbmlGeneratedIndex Index);

    private static readonly ConcurrentDictionary<(ProjectId Project, string Dbml), CacheEntry> s_indexes = new();

    /// <summary>
    /// The index for one model, built once per set of declarations and reused after.
    /// </summary>
    /// <remarks>
    /// The version is asked for before the compilation is — it is a cheap traversal that forces no
    /// binding — so a cache hit answers without one. That is the point: an outline or a code lens in
    /// a <c>.dbml</c> does not wait for the C# the user is typing elsewhere to compile, and the
    /// compilation is paid for only by the callers that go on to search, which need it anyway.
    /// </remarks>
    public static async Task<DbmlGeneratedIndex> GetAsync(
        string dbmlPath, Project? project, CancellationToken ct)
    {
        if (project is null || project.Language != LanguageNames.CSharp)
            return Empty;

        string path = DbmlDocumentCache.Normalize(dbmlPath);
        string designerPath = DbmlDocumentCache.Normalize(DbmlSourceMappingService.DesignerPathFor(path));

        // The gate that keeps this cheap: a model whose designer the project does not compile can
        // bind nothing, and finding that out is a scan of a document list rather than a compilation.
        if (project.Documents.FirstOrDefault(d =>
                d.FilePath is { Length: > 0 } file
                && string.Equals(DbmlDocumentCache.Normalize(file), designerPath, StringComparison.OrdinalIgnoreCase))
            is not { } designer)
        {
            return Empty;
        }

        if (DbmlDocumentCache.Get(path) is not { } model || model.Database.IsEmpty)
            return Empty;

        string fingerprint = Convert.ToHexString(model.Text.GetChecksum().AsSpan());
        var semanticVersion = await project.GetDependentSemanticVersionAsync(ct);
        var cacheKey = (project.Id, path);

        if (s_indexes.TryGetValue(cacheKey, out var cached)
            && cached.SemanticVersion.Equals(semanticVersion)
            && string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return cached.Index;
        }

        if (await project.GetCompilationAsync(ct) is not { } compilation)
            return Empty;

        var index = await BuildAsync(model.Database, path, designer, designerPath, compilation, ct);
        s_indexes[cacheKey] = new CacheEntry(semanticVersion, fingerprint, index);

        // Only after something bound. The path derivation alone would claim every `.designer.cs` in
        // the solution, including the ones a `.resx` or a `.settings` produced.
        if (!index.IsEmpty)
            DbmlSourceMappingService.NoteBound(designerPath);

        return index;
    }

    internal static void Clear() => s_indexes.Clear();

    // ---- Binding --------------------------------------------------------------------------------

    private static async Task<DbmlGeneratedIndex> BuildAsync(
        DbmlDatabase database,
        string dbmlPath,
        Document designer,
        string designerPath,
        Compilation compilation,
        CancellationToken ct)
    {
        if (await designer.GetSyntaxTreeAsync(ct) is not { } tree
            || !compilation.ContainsSyntaxTree(tree))
        {
            // A tree the compilation does not own has no semantic model to ask, which happens when
            // the caller handed over a project from a different solution snapshot.
            return Empty;
        }

        var model = compilation.GetSemanticModel(tree);
        var root = await tree.GetRootAsync(ct);

        var entitiesByTableName = new Dictionary<string, INamedTypeSymbol>(StringComparer.OrdinalIgnoreCase);
        var typesByClassName = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        INamedTypeSymbol? context = null;

        foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            ct.ThrowIfCancellationRequested();

            if (model.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol type)
                continue;

            typesByClassName.TryAdd(type.Name, type);

            if (AttributeValue(type, "TableAttribute", "Name") is { Length: > 0 } tableName)
                entitiesByTableName.TryAdd(tableName, type);

            // The context announces itself by deriving from DataContext. Its own [Database] name is
            // not used as an anchor: SqlMetal omits it when the model does not name the database,
            // and there is only ever one context in a designer.
            if (context is null && DerivesFromDataContext(type))
                context = type;
        }

        var index = new DbmlGeneratedIndex
        {
            DbmlPath = dbmlPath,
            DesignerPath = designerPath,
            Compilation = compilation,
        };

        if (context is not null)
            index.Record(context, dbmlPath, database);

        var tablePropertiesByTableName = TablePropertiesByTableName(context);
        var functionsByName = FunctionsByName(context);

        foreach (var table in database.Tables)
        {
            ct.ThrowIfCancellationRequested();

            if (tablePropertiesByTableName.TryGetValue(table.Name, out var tableProperty))
                index.Record(tableProperty, dbmlPath, table);

            foreach (var type in table.AllTypes())
            {
                // The row type is anchored on the table name it carries; a derived type carries no
                // [Table] of its own — LINQ to SQL maps inheritance onto one table — so it falls back
                // to its class name, which is what the model declares it as.
                if (!entitiesByTableName.TryGetValue(table.Name, out var entity)
                    || !string.Equals(entity.Name, type.Name, StringComparison.Ordinal))
                {
                    if (!typesByClassName.TryGetValue(type.Name, out entity))
                        continue;
                }

                index.Record(entity, dbmlPath, type);
                BindMembers(index, dbmlPath, type, entity);
            }
        }

        foreach (var function in database.Functions)
        {
            if (functionsByName.TryGetValue(function.Name, out var method))
                index.Record(method, dbmlPath, function);
        }

        return index;
    }

    private static void BindMembers(
        DbmlGeneratedIndex index, string dbmlPath, DbmlType type, INamedTypeSymbol entity)
    {
        var columns = new Dictionary<string, IPropertySymbol>(StringComparer.OrdinalIgnoreCase);
        var associations = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);

        foreach (var property in entity.GetMembers().OfType<IPropertySymbol>())
        {
            if (Attribute(property, "ColumnAttribute") is { } column)
            {
                // No Name on the attribute means the column is named after the property — SqlMetal
                // omits the redundant half rather than leaving the column unmapped.
                columns.TryAdd(NamedArgument(column, "Name") ?? property.Name, property);
                continue;
            }

            if (Attribute(property, "AssociationAttribute") is not null)
                associations.TryAdd(property.Name, property);
        }

        foreach (var column in type.Columns)
        {
            if (columns.TryGetValue(column.Name, out var property)
                || columns.TryGetValue(column.Member, out property))
            {
                index.Record(property, dbmlPath, column);
            }
        }

        foreach (var association in type.Associations)
        {
            // By member, never by name: both ends of a relationship share one Name, so keying on it
            // would bind the collection on the parent and the reference on the child to whichever
            // was seen first.
            if (associations.TryGetValue(association.Member, out var property))
                index.Record(property, dbmlPath, association);
        }
    }

    /// <summary>
    /// The context's <c>Table&lt;T&gt;</c> properties, keyed by the table each one's entity claims.
    /// </summary>
    /// <remarks>
    /// Through the entity's <c>[Table(Name=…)]</c> rather than through the property's own name,
    /// because the property name is SqlMetal's pluralization of the class and the model's
    /// <c>Member</c> is free to disagree with it. The type argument is the anchor both ends share.
    /// </remarks>
    private static Dictionary<string, IPropertySymbol> TablePropertiesByTableName(INamedTypeSymbol? context)
    {
        var results = new Dictionary<string, IPropertySymbol>(StringComparer.OrdinalIgnoreCase);

        if (context is null)
            return results;

        foreach (var property in context.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.Type is not INamedTypeSymbol { Name: "Table", TypeArguments: [INamedTypeSymbol entity] })
                continue;

            if (AttributeValue(entity, "TableAttribute", "Name") is { Length: > 0 } name)
                results.TryAdd(name, property);
        }

        return results;
    }

    private static Dictionary<string, IMethodSymbol> FunctionsByName(INamedTypeSymbol? context)
    {
        var results = new Dictionary<string, IMethodSymbol>(StringComparer.OrdinalIgnoreCase);

        if (context is null)
            return results;

        foreach (var method in context.GetMembers().OfType<IMethodSymbol>())
        {
            if (AttributeValue(method, "FunctionAttribute", "Name") is { Length: > 0 } name)
                results.TryAdd(name, method);
        }

        return results;
    }

    private void Record(ISymbol symbol, string dbmlPath, IDbmlDeclaration declaration)
    {
        _byKey.TryAdd(declaration.Key, symbol);
        _reverse.TryAdd(symbol.OriginalDefinition, new DbmlDeclarationRef(dbmlPath, declaration.Key, declaration.Kind));
    }

    private static bool DerivesFromDataContext(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current is { Name: "DataContext", ContainingNamespace.Name: "Linq" })
                return true;
        }

        return false;
    }

    /// <summary>
    /// One of SqlMetal's mapping attributes, matched on its simple name.
    /// </summary>
    /// <remarks>
    /// Generated code writes these fully qualified and alias-qualified —
    /// <c>[global::System.Data.Linq.Mapping.ColumnAttribute(…)]</c> — so the simple name is the part
    /// that is stable. The namespace is not checked because no other attribute in a designer file is
    /// called <c>ColumnAttribute</c>, and requiring it would break the moment someone maps a model by
    /// hand against a compatible runtime.
    /// </remarks>
    private static AttributeData? Attribute(ISymbol symbol, string attributeName)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == attributeName)
                return attribute;
        }

        return null;
    }

    private static string? AttributeValue(ISymbol symbol, string attributeName, string argument) =>
        Attribute(symbol, attributeName) is { } attribute ? NamedArgument(attribute, argument) : null;

    private static string? NamedArgument(AttributeData attribute, string name)
    {
        foreach (var (key, value) in attribute.NamedArguments)
        {
            if (key == name)
                return value.Value as string;
        }

        return null;
    }
}
