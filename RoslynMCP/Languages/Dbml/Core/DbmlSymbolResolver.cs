using Microsoft.CodeAnalysis;

namespace RoslynMCP.Languages.Dbml.Core;

/// <summary>What a caret in a <c>.dbml</c> is on, and the C# SqlMetal generated for it.</summary>
/// <param name="Symbol">The bound member, or <c>null</c> when the designer is missing or out of step
/// with the model. Navigation and counts decline on a null; the outline and hover do not, because
/// what the model says is worth showing whether or not the C# has caught up.</param>
internal readonly record struct DbmlHit(IDbmlDeclaration Declaration, ISymbol? Symbol);

/// <summary>Maps an offset in a <c>.dbml</c> to the declaration it falls in.</summary>
internal static class DbmlSymbolResolver
{
    /// <summary>
    /// The innermost declaration containing <paramref name="offset"/>, with its symbol attached.
    /// </summary>
    /// <remarks>
    /// Innermost wins, which falls out of the model's nesting: a caret on a <c>&lt;Column&gt;</c> is
    /// inside its <c>&lt;Type&gt;</c>, its <c>&lt;Table&gt;</c> and the <c>&lt;Database&gt;</c> too,
    /// and only the column is what the user is pointing at. A caret in the whitespace between two
    /// tables lands on the database, which is the truthful answer rather than nothing.
    /// </remarks>
    public static DbmlHit? ResolveAt(DbmlView view, int offset)
    {
        IDbmlDeclaration? best = null;

        foreach (var declaration in view.Database.AllDeclarations())
        {
            if (!declaration.Span.Contains(offset) && declaration.Span.End != offset)
                continue;

            // Ties go to the shorter span, which is the more deeply nested one.
            if (best is null || declaration.Span.Length <= best.Span.Length)
                best = declaration;
        }

        return best is null ? null : new DbmlHit(best, view.Index.SymbolFor(best));
    }
}
