using System.Text;
using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Dbml;

internal sealed partial class DbmlLanguage : ILanguageHoverProvider
{
    /// <summary>
    /// The mapping under the pointer: what the database calls it, and what SqlMetal generated for it.
    /// </summary>
    /// <remarks>
    /// A <c>.dbml</c> is a mapping, so a hover over one is worth having for exactly the halves the
    /// reader cannot see at once. The element on screen holds the database half; the C# half — the
    /// property's declared type, its accessibility, the attributes on it — is in a file the reader is
    /// specifically being kept out of by the rest of this pack, which makes putting its signature here
    /// the point rather than a nicety.
    /// </remarks>
    public async Task<Hover?> HoverAsync(TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (view, offset)
            || DbmlSymbolResolver.ResolveAt(view, offset) is not { } hit)
        {
            return null;
        }

        return Markdown(view, hit, ct) is { Length: > 0 } markdown
            ? new Hover(new MarkupContent("markdown", markdown), ToRange(view.Text, hit.Declaration.Span))
            : null;
    }

    private static string Markdown(DbmlView view, DbmlHit hit, CancellationToken ct)
    {
        var builder = new StringBuilder(Fence("xml", Signature(hit.Declaration)));

        if (Note(view, hit.Declaration) is { Length: > 0 } note)
            builder.Append("\n\n").Append(note);

        if (hit.Symbol is { } symbol)
            builder.Append("\n\n").Append(HoverHandler.Describe(symbol, ct));

        return builder.ToString();
    }

    /// <summary>
    /// The declaration restated as the one line of the element that matters.
    /// </summary>
    /// <remarks>
    /// Rewritten rather than sliced out of the buffer. An element is commonly wrapped across lines and
    /// carries attributes — <c>Storage</c>, <c>UpdateCheck</c>, <c>AccessModifier</c> — that say
    /// nothing to a reader asking what a column is, and a hover repeating what is already on screen
    /// underneath it is worse than a short one.
    /// </remarks>
    private static string Signature(IDbmlDeclaration declaration) => declaration switch
    {
        DbmlDatabase database => $"<Database Name=\"{database.Name}\" Class=\"{database.Class}\" />",

        DbmlTable table => $"<Table Name=\"{table.Name}\" Member=\"{table.Member}\" />",

        DbmlType type => $"<Type Name=\"{type.Name}\" />",

        DbmlColumn column =>
            $"<Column Name=\"{column.Name}\" Type=\"{column.ClrType}\" DbType=\"{column.DbType}\" />",

        DbmlAssociation association =>
            $"<Association Name=\"{association.Name}\" Member=\"{association.Member}\" "
            + $"ThisKey=\"{association.ThisKey}\" OtherKey=\"{association.OtherKey}\" "
            + $"Type=\"{association.TargetTypeName}\" />",

        DbmlFunction function => $"<Function Name=\"{function.Name}\" Method=\"{function.Member}\" />",

        _ => $"<{declaration.Kind} Name=\"{declaration.Name}\" />",
    };

    /// <summary>
    /// The sentence about the declaration that is not written anywhere in the element.
    /// </summary>
    /// <remarks>
    /// Only where there is something to say. A column's flags are worth spelling out because
    /// <c>IsPrimaryKey="true"</c> and <c>IsDbGenerated="true"</c> are the two that change what the
    /// generated property does; an association's is worth saying because which end of the pair an
    /// element is decides whether the property is a single entity or a collection, and the attribute
    /// that says so is a <c>true</c>/absent pair rather than a word.
    /// </remarks>
    private static string Note(DbmlView view, IDbmlDeclaration declaration)
    {
        switch (declaration)
        {
            case DbmlColumn column:
                var flags = new List<string>();

                if (column.IsPrimaryKey)
                    flags.Add("primary key");
                if (column.IsDbGenerated)
                    flags.Add("database generated");
                if (column.IsVersion)
                    flags.Add("row version");

                flags.Add(column.CanBeNull ? "nullable" : "not null");

                return string.Join(" · ", flags);

            case DbmlAssociation association:
                // Which end this is, in the words the generated property is in: the child end holds
                // the key and gets one entity, the parent end is pointed at and gets many.
                string end = association.IsForeignKey
                    ? $"One `{association.TargetTypeName}`."
                    : $"Many `{association.TargetTypeName}`.";

                return $"{end} `{association.ThisKey}` → `{association.OtherKey}`";

            case DbmlTable table:
                int columns = table.AllTypes().Sum(type => type.Columns.Length);
                return $"{columns} column{(columns == 1 ? "" : "s")}";

            case DbmlDatabase database:
                // The namespaces are the one thing about the root that decides where a reader will
                // find the generated code, and they are attributes a reader would have to scroll to.
                var parts = new List<string>();

                if (database.ContextNamespace is { Length: > 0 } context)
                    parts.Add($"Context in `{context}`.");
                if (database.EntityNamespace is { Length: > 0 } entity)
                    parts.Add($"Entities in `{entity}`.");

                parts.Add($"{view.Database.Tables.Length} tables.");

                return string.Join(" ", parts);

            default:
                return string.Empty;
        }
    }

    private static string Fence(string language, string code) => $"```{language}\n{code}\n```";
}
