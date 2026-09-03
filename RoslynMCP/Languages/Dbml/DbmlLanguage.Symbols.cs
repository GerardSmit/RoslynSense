using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Dbml;

internal sealed partial class DbmlLanguage : ILanguageDocumentSymbolProvider
{
    /// <summary>
    /// The model as a tree: the context, its tables, each table's row type, and that type's columns
    /// and associations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>&lt;Table&gt;</c> and its <c>&lt;Type&gt;</c> are collapsed into one row. They are two
    /// elements because the mapping is genuinely two-sided — a table name on one and a class name on
    /// the other — but they are one thing to a reader, and nesting a single child under every table
    /// would put a twisty in front of every column in the file for no gain. The row is named for the
    /// class and detailed with the table, which is the pairing a caret in C# arrives looking for.
    /// </para>
    /// <para>
    /// The invariants a <c>documentSymbol</c> tree has to keep, from the resources pack: siblings may
    /// not overlap, a parent's range must contain its children's, and an element that could not be
    /// spanned is left out rather than given a range it does not have.
    /// </para>
    /// </remarks>
    public async Task<DocumentSymbol[]> DocumentSymbolAsync(
        DocumentSymbolParams p, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);

        if (DbmlDocumentCache.Get(path) is not { } document || document.Database.IsEmpty)
            return [];

        var lines = document.Text.Lines;
        var database = document.Database;
        var tables = new List<DocumentSymbol>();

        foreach (var table in database.Tables)
        {
            ct.ThrowIfCancellationRequested();
            tables.Add(TableSymbol(lines, table));
        }

        foreach (var function in database.Functions)
        {
            tables.Add(Leaf(
                lines, function.Member, function.Name,
                function.IsComposable ? LspSymbolKind.Function : LspSymbolKind.Method, function));
        }

        // The root is a symbol of its own rather than the outline's implicit top, so the context
        // class is nameable in the breadcrumb and the whole model folds to one line.
        return
        [
            new DocumentSymbol(
                database.Class,
                database.Name.Length > 0 ? database.Name : null,
                LspSymbolKind.Class,
                LspConverters.ToRange(lines, database.Span),
                LspConverters.ToRange(lines, database.SelectionSpan),
                [.. tables]),
        ];
    }

    private static DocumentSymbol TableSymbol(TextLineCollection lines, DbmlTable table)
    {
        var members = new List<DocumentSymbol>();

        foreach (var type in table.AllTypes())
        {
            foreach (var column in type.Columns)
            {
                members.Add(Leaf(
                    lines, column.Member, ColumnDetail(column),
                    column.IsPrimaryKey ? LspSymbolKind.Key : LspSymbolKind.Field, column));
            }

            foreach (var association in type.Associations)
            {
                members.Add(Leaf(
                    lines,
                    association.Member,
                    $"{association.TargetTypeName} · {association.ThisKey} → {association.OtherKey}",
                    LspSymbolKind.Property,
                    association));
            }
        }

        // Ordered by position, so a derived type's columns fall in with the file rather than after
        // every base column — the tree has to follow the buffer or the ranges stop nesting.
        members.Sort((left, right) => left.Range.Start.Line.CompareTo(right.Range.Start.Line));

        return new DocumentSymbol(
            table.RowType?.Name ?? table.Member,
            table.Name,
            LspSymbolKind.Struct,
            LspConverters.ToRange(lines, table.Span),
            LspConverters.ToRange(lines, table.SelectionSpan),
            [.. members]);
    }

    /// <summary>
    /// What the column is in the database, which is the half the generated property does not say.
    /// </summary>
    private static string ColumnDetail(DbmlColumn column)
    {
        string type = column.DbType is { Length: > 0 } dbType
            ? dbType
            : column.ClrType ?? string.Empty;

        return column.IsDbGenerated ? $"{type} · generated".Trim(' ', '·') : type;
    }

    private static DocumentSymbol Leaf(
        TextLineCollection lines, string name, string? detail, int kind, IDbmlDeclaration declaration) =>
        new(name,
            detail is { Length: > 0 } ? detail : null,
            kind,
            LspConverters.ToRange(lines, declaration.Span),
            LspConverters.ToRange(lines, declaration.SelectionSpan),
            []);
}
