using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Dbml;

internal sealed partial class DbmlLanguage : ILanguageDiagnosticProvider
{
    private const string Source = "dbml";

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
    /// </remarks>
    public async Task<Diagnostic[]> DiagnosticsAsync(string filePath, CancellationToken ct)
    {
        if (await DbmlWorkspace.GetAsync(filePath, ct) is not { } view || view.Database.IsEmpty)
            return [];

        var lines = view.Text.Lines;
        var diagnostics = new List<Diagnostic>();

        AddBindingDiagnostics(view, lines, diagnostics);
        AddModelDiagnostics(view, lines, diagnostics, ct);

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
