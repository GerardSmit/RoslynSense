using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Services;
using RoslynMCP.Tools;

namespace RoslynMCP.Languages.WebForms.Tools;

/// <summary>
/// Validates ASPX/ASCX files. The parse is the validation — unresolved controls, unknown
/// properties and unbalanced tags all surface as parse diagnostics — so the report is the file's
/// outline with its <c>Parse Errors</c> section, which also tells the caller what the parser
/// understood and therefore why a diagnostic was or was not raised.
/// </summary>
internal class AspxDiagnostics : IDiagnosticsHandler
{
    public bool CanHandle(string filePath) => AspxDocumentService.IsAspxFile(filePath);

    public Task<string> ValidateAsync(
        string filePath, IOutputFormatter fmt, CancellationToken cancellationToken) =>
        AspxOutline.FormatAsync(filePath, cancellationToken);
}
