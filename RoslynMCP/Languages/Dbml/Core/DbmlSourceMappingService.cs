using System.Collections.Concurrent;

namespace RoslynMCP.Languages.Dbml.Core;

/// <summary>
/// The path relationship between a LINQ to SQL model and the C# SqlMetal writes from it, in both
/// directions.
/// </summary>
/// <remarks>
/// <para>
/// The forward rule is <c>DbmlDesignerGenerator.GetDesignerPath</c>, and this mirrors it in reverse
/// the way <c>AspxSourceMappingService.MarkupPathFor</c> mirrors <c>AspxDesignerGenerator</c>. The
/// difference from WebForms is the one that matters here: LINQ to SQL <em>replaces</em> the extension
/// rather than appending to it, so <c>Northwind.dbml</c> produces <c>Northwind.designer.cs</c> — and
/// so does <c>Northwind.resx</c>, and <c>Northwind.settings</c>.
/// </para>
/// <para>
/// That makes the reverse derivation a <em>candidate</em> and never a conclusion.
/// <see cref="ModelPathFor"/> names the file that would be the model if there were one; only the
/// binder, having matched SqlMetal's attributes against the parsed model, can say that there is. This
/// is why <see cref="IsBoundDesignerPath"/> reads what the binder recorded instead of testing the
/// path: a pack that withdrew <c>Settings.Designer.cs</c> from an unrelated F12 would be worse than
/// one that never contributed at all.
/// </para>
/// </remarks>
internal static class DbmlSourceMappingService
{
    private const string DesignerSuffix = ".designer.cs";

    /// <summary>The C# SqlMetal writes for a model.</summary>
    public static string DesignerPathFor(string dbmlPath) =>
        Path.ChangeExtension(dbmlPath, ".designer.cs");

    /// <summary>
    /// The model a designer file <em>would</em> have come from, or <c>null</c> when the path is not a
    /// designer file at all. Existence is not checked and binding is not implied.
    /// </summary>
    public static string? ModelPathFor(string declaringPath)
    {
        if (declaringPath.Length <= DesignerSuffix.Length
            || !declaringPath.EndsWith(DesignerSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return declaringPath[..^DesignerSuffix.Length] + ".dbml";
    }

    private static readonly ConcurrentDictionary<string, byte> s_bound =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records that a designer file was proved to be SqlMetal's output for a model.</summary>
    /// <remarks>Called by the binder, which is the only thing that can know it.</remarks>
    public static void NoteBound(string designerPath) =>
        s_bound[DbmlDocumentCache.Normalize(designerPath)] = 0;

    /// <summary>
    /// Whether any binding already built calls this path a LINQ to SQL designer.
    /// </summary>
    /// <remarks>
    /// A dictionary read and nothing else, following <c>ProtoGeneratedIndex.IsKnownGenerated</c>: it
    /// runs once per candidate location on every go-to-definition in the solution, so building an
    /// index here would put a parse and a compilation behind a question mostly asked about files with
    /// no connection to LINQ to SQL. The answer is therefore scoped to what has been looked at, which
    /// is the correct scope rather than a limitation — the caller is asking because a contribution was
    /// just made, and making it is what recorded the binding.
    /// </remarks>
    public static bool IsBoundDesignerPath(string filePath) =>
        !s_bound.IsEmpty && s_bound.ContainsKey(DbmlDocumentCache.Normalize(filePath));

    /// <summary>
    /// Drops the record that a designer file is a model's output, returning whether there was one.
    /// </summary>
    /// <remarks>
    /// False is the answer for every <c>.designer.cs</c> in the solution that a <c>.resx</c> or a
    /// <c>.settings</c> produced, which is what lets the watched-file handler tell "not mine" from
    /// "cleared".
    /// </remarks>
    public static bool Forget(string designerPath) =>
        s_bound.TryRemove(DbmlDocumentCache.Normalize(designerPath), out _);

    internal static void Clear() => s_bound.Clear();
}
