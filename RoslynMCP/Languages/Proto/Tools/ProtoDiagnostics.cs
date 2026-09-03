using System.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Proto.Tools;

/// <summary>
/// Validates a <c>.proto</c> and reports what the editor would put in its problems list.
/// </summary>
/// <remarks>
/// <para>
/// The analysis is <see cref="ProtoDiagnosticsHandler"/>'s, not this file's. Proto validation is
/// two things the parse alone cannot answer — which names resolve across the import graph, and
/// which of protoc's own rules a syntactically fine file breaks — and both are decisions, which
/// means having two of them is having two answers. Running the LSP pass and formatting its result
/// is what guarantees the squiggle in the editor and the report in the chat say the same thing,
/// down to the severity and the diagnostic id a user might suppress.
/// </para>
/// <para>
/// Nothing here runs protoc, and the pass says so through its severities: a problem decidable from
/// the file alone is an error, and one that depends on finding another file is a warning, because
/// MSBuild's per-item <c>ProtoRoot</c> is invisible from this side and a project that sets one
/// builds cleanly while its imports look missing.
/// </para>
/// </remarks>
internal class ProtoDiagnostics : IDiagnosticsHandler
{
    /// <summary>LSP severities, which arrive as the wire numbers.</summary>
    private const int Error = 1;
    private const int Warning = 2;

    public bool CanHandle(string filePath) => ProtoDocumentService.IsProtoFile(filePath);

    public async Task<string> ValidateAsync(
        string systemPath, IOutputFormatter fmt, CancellationToken cancellationToken)
    {
        var view = await ProtoWorkspace.GetAsync(systemPath, cancellationToken);
        if (view is null)
        {
            return $"Error: Couldn't load '{Path.GetFileName(systemPath)}'. " +
                   "The file must exist and be a readable .proto.";
        }

        var diagnostics = await ProtoDiagnosticsHandler.DiagnosticsAsync(systemPath, cancellationToken);

        var parse = view.Parse;
        var results = new StringBuilder();

        fmt.AppendHeader(results, $"Proto Validation: {Path.GetFileName(parse.FilePath)}");

        fmt.AppendHeader(results, "File Information", level: 2);
        fmt.AppendField(results, "File", parse.FilePath);
        fmt.AppendField(results, "Package", parse.Package.Length > 0 ? parse.Package : "(none)");
        fmt.AppendField(results, "Imports", $"{parse.Imports.Length} statement(s)");
        fmt.AppendField(results, "Declarations", parse.AllDeclarations.Length);
        fmt.AppendField(results, "Project", view.Project is { } project
            ? project.FilePath is { } path ? Path.GetFileName(path) : project.Name
            : "none — no project in the solution compiles this file");
        fmt.AppendSeparator(results);

        int errors = diagnostics.Count(diagnostic => diagnostic.Severity == Error);
        int warnings = diagnostics.Count(diagnostic => diagnostic.Severity == Warning);

        fmt.AppendHeader(results, "Diagnostics", level: 2);

        if (diagnostics.Length == 0)
        {
            fmt.AppendEmpty(results, "None — the file parses and every name in it resolves.");
            fmt.AppendSeparator(results);
        }
        else
        {
            fmt.AppendField(results, "Found", $"{diagnostics.Length} diagnostic(s)");
            fmt.AppendSeparator(results);

            var rows = diagnostics
                .OrderBy(diagnostic => diagnostic.Range.Start.Line)
                .ThenBy(diagnostic => diagnostic.Range.Start.Character)
                .Select(diagnostic => new[]
                {
                    SeverityName(diagnostic.Severity),
                    diagnostic.Code ?? string.Empty,
                    $"{diagnostic.Range.Start.Line + 1}",
                    $"{diagnostic.Range.Start.Character + 1}",
                    diagnostic.Message,
                })
                .ToList();

            fmt.AppendTable(
                results, "Diagnostics", ["Severity", "Code", "Line", "Column", "Message"], rows);
            fmt.AppendSeparator(results);
        }

        fmt.AppendHeader(results, "Summary", level: 2);
        fmt.AppendField(results, "Errors", errors);
        fmt.AppendField(results, "Warnings", warnings);
        fmt.AppendSeparator(results);

        AppendHints(results, view, fmt);

        return results.ToString();
    }

    /// <summary>The LSP severity numbers spelled out. The wire carries 1-4 and a table cell reading
    /// "2" tells a reader nothing.</summary>
    private static string SeverityName(int severity) => severity switch
    {
        1 => "Error",
        2 => "Warning",
        3 => "Information",
        _ => "Hint",
    };

    private static void AppendHints(StringBuilder results, ProtoProjectView view, IOutputFormatter fmt)
    {
        var hints = new List<string>();

        if (view.Index.IsEmpty)
        {
            hints.Add(
                "The project has produced no generated code, so no declaration in this file is bound " +
                "to a symbol yet — build it before relying on go_to_definition or find_usages here.");
        }

        hints.Add("Use get_file_outline on this file to see what the parser understood");
        hints.Add("Nothing here runs protoc — build the project for the compiler's own diagnostics");

        fmt.AppendHints(results, [.. hints]);
    }
}
