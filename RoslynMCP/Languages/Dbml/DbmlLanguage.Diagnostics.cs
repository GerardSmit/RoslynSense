using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Dbml;

internal sealed partial class DbmlLanguage : ILanguageDiagnosticProvider
{
    private const string Source = "dbml";

    private const int Error = 1;
    private const int Warning = 2;
    private const int Information = 3;

    /// <summary>
    /// The few things about a model that can be said with certainty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Narrow on purpose. A <c>.dbml</c> is a mapping onto a database this process usually cannot see,
    /// so almost everything that looks wrong here — a column type that does not match, a table that is
    /// not there — is a question only the database can answer, and answering it by guessing would put
    /// a red squiggle on a model that is correct. What is left is what the file contradicts about
    /// itself, plus the one thing that explains a missing feature rather than reporting a fault.
    /// </para>
    /// <para>
    /// The unbound reports are informational rather than warnings for that reason: a model whose
    /// project has never been built is not broken, it is unbuilt, and the message exists so the
    /// missing reference counts read as "not yet" rather than as "none".
    /// </para>
    /// <para>
    /// The one exception to the narrowness is a <c>Type=</c> that names nothing. That is not a
    /// question about the database — the compilation answers it outright — and it is the failure
    /// that ends in a build error naming a type nobody typed, in a generated file nobody edits. It
    /// is reported as an error because that is what it becomes; the rest of the pack stays at
    /// warning because the rest of the pack is describing a model, not a compilation.
    /// </para>
    /// </remarks>
    public async Task<Diagnostic[]> DiagnosticsAsync(string filePath, CancellationToken ct)
    {
        if (await DbmlWorkspace.GetAsync(filePath, ct) is not { } view || view.Database.IsEmpty)
            return [];

        var lines = view.Text.Lines;
        var diagnostics = new List<Diagnostic>();

        AddBindingDiagnostics(view, lines, diagnostics);
        AddModelDiagnostics(view, lines, diagnostics, ct);
        AddClrTypeDiagnostics(view, lines, diagnostics, ct);

        return [.. diagnostics];
    }

    /// <summary>
    /// Whether the designer exists and whether it bound, reported once on the root.
    /// </summary>
    /// <remarks>
    /// Once, not per element. A project that has not been built binds nothing at all, and one report
    /// per column would bury the file in a hundred copies of the same sentence.
    /// </remarks>
    private static void AddBindingDiagnostics(
        DbmlView view, TextLineCollection lines, List<Diagnostic> diagnostics)
    {
        var range = LspConverters.ToRange(lines, view.Database.SelectionSpan);
        string designer = Path.GetFileName(DbmlSourceMappingService.DesignerPathFor(view.FilePath));

        if (view.Project is null)
        {
            diagnostics.Add(new Diagnostic(
                range, Information, "DBML0001", Source,
                "No project claims this model, so reference counts and navigation into the "
                + "generated code are unavailable."));
            return;
        }

        if (!File.Exists(DbmlSourceMappingService.DesignerPathFor(view.FilePath)))
        {
            diagnostics.Add(new Diagnostic(
                range, Information, "DBML0002", Source,
                $"There is no {designer}. Build the project, or regenerate the designer, for "
                + "reference counts and navigation into the generated code."));
            return;
        }

        if (view.Index.IsEmpty)
        {
            diagnostics.Add(new Diagnostic(
                range, Information, "DBML0003", Source,
                $"{designer} is not part of the project's compilation, or was generated from "
                + "something other than this model, so nothing here binds to it."));
        }
    }

    /// <summary>A <c>Type=</c> that names no type the project can see.</summary>
    /// <remarks>
    /// <para>
    /// Gated on the same compilation <see cref="ClrTokenType"/> is, so the colour and the squiggle
    /// are driven by one predicate and cannot disagree: a model whose project has never been built
    /// has no compilation, and reporting against one that does not exist would paint every column
    /// red on a checkout. The <c>DataContext</c> probe is the second half of that — a project whose
    /// references failed to resolve can produce a compilation in which nothing resolves, and this
    /// pass has to stay quiet there rather than report the whole file.
    /// </para>
    /// <para>
    /// Driven from <see cref="DbmlReferences.All"/> rather than from the model, because the model
    /// records a column's type and not a function's: <c>DbmlReader</c> never descends into
    /// <c>&lt;Parameter&gt;</c> or <c>&lt;Return&gt;</c>, so a model-driven pass would skip every
    /// stored-procedure signature — which is where a hand-edited type name is likeliest to be.
    /// </para>
    /// </remarks>
    internal static void AddClrTypeDiagnostics(
        DbmlView view, TextLineCollection lines, List<Diagnostic> diagnostics, CancellationToken ct)
    {
        if (view.Index.Compilation is not { } compilation
            || compilation.GetTypeByMetadataName("System.Data.Linq.DataContext") is null)
        {
            return;
        }

        // Per call, not per reference: a model repeats a handful of type names across hundreds of
        // columns, and a miss costs one metadata lookup per dot in the name.
        var resolved = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var reference in DbmlReferences.All(view.Document))
        {
            ct.ThrowIfCancellationRequested();

            if (reference.Kind != DbmlReferenceKind.ClrType)
                continue;

            if (!resolved.TryGetValue(reference.Name, out bool exists))
            {
                exists = ResolveClrType(compilation, reference.Name) is not null;
                resolved[reference.Name] = exists;
            }

            if (exists)
                continue;

            diagnostics.Add(new Diagnostic(
                LspConverters.ToRange(lines, reference.Span), Error, "DBML0006", Source,
                $"'{reference.Name}' names no type this project can see. The generated designer "
                + "will not compile."));
        }
    }

    /// <summary>What the file says about itself that cannot all be true.</summary>
    private static void AddModelDiagnostics(
        DbmlView view, TextLineCollection lines, List<Diagnostic> diagnostics, CancellationToken ct)
    {
        var declaredTypes = new HashSet<string>(
            view.Database.AllTypes().Select(type => type.Name), StringComparer.Ordinal);

        foreach (var type in view.Database.AllTypes())
        {
            ct.ThrowIfCancellationRequested();

            // Members of the same class, so a column and an association collide with each other just
            // as two columns do — which is the case a reader is least likely to spot.
            var seen = new Dictionary<string, IDbmlDeclaration>(StringComparer.Ordinal);

            foreach (var member in type.Columns.Cast<IDbmlDeclaration>().Concat(type.Associations))
            {
                if (member.Member.Length == 0)
                    continue;

                if (!seen.TryAdd(member.Member, member))
                {
                    diagnostics.Add(new Diagnostic(
                        LspConverters.ToRange(lines, member.SelectionSpan), Warning, "DBML0004", Source,
                        $"'{type.Name}' already has a member named '{member.Member}'. "
                        + "The generated class will not compile."));
                }
            }

            foreach (var association in type.Associations)
            {
                if (association.TargetTypeName.Length == 0
                    || declaredTypes.Contains(association.TargetTypeName))
                {
                    continue;
                }

                diagnostics.Add(new Diagnostic(
                    LspConverters.ToRange(lines, association.SelectionSpan), Warning, "DBML0005", Source,
                    $"'{association.Member}' targets '{association.TargetTypeName}', which this "
                    + "model does not declare. The generated class will not compile."));
            }
        }
    }
}
