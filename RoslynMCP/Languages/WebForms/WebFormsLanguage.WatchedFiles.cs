using RoslynMCP.Languages.WebForms.Core;

namespace RoslynMCP.Languages.WebForms;

internal sealed partial class WebFormsLanguage : ILanguageWatchedFileHandler
{
    /// <summary>
    /// A markup file changed under the editor. The parse cache keys on the file's own text, so it
    /// self-corrects on the next read; what does not self-correct is the editor, still showing
    /// diagnostics computed from the old markup. A <c>web.config</c> is the exception that has to
    /// clear the cache outright — every parsed document in the project inherited its
    /// <c>&lt;pages&gt;</c> namespaces.
    /// </summary>
    public bool Invalidate(string path, WatchedFileChange change)
    {
        if (Path.GetFileName(path).Equals("web.config", StringComparison.OrdinalIgnoreCase))
        {
            // The tree the config governs, not every parse in the process. A web.config applies to
            // the site it sits in, and dropping everything re-parsed every page of every other site
            // loaded — including solutions open in other windows, which this handler is offered
            // events for regardless of whether they enabled the pack.
            AspxDocumentService.InvalidateUnder(Path.GetDirectoryName(path) ?? "");
            return true;
        }

        if (AspxDocumentService.IsAspxFile(path))
        {
            AspxDocumentService.Invalidate(path);
            return true;
        }

        return false;
    }
}
