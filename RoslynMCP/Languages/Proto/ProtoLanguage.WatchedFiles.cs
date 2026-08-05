using RoslynMCP.Languages.Proto.Core;

namespace RoslynMCP.Languages.Proto;

internal sealed partial class ProtoLanguage : ILanguageWatchedFileHandler
{
    /// <summary>
    /// A <c>.proto</c> changed under the editor — a branch switch, a scaffold, another agent's edit.
    /// The parse cache keys each entry on the buffer's checksum and re-reads the file on every
    /// lookup, so an edited file self-corrects; a deleted or renamed one does not, and its entry
    /// would otherwise outlive it for the rest of the process.
    /// </summary>
    /// <remarks>
    /// No Roslyn document is evicted, because there is none: a <c>.proto</c> is not a document the
    /// workspace holds. Grpc.Tools writes real <c>.cs</c> into <c>obj</c> and MSBuild hands those to
    /// Roslyn as ordinary <c>Compile</c> items, so the only documents behind this file are outputs
    /// of a build. They are stale the moment this edit lands — the contract moved and nothing
    /// regenerated them — and that is the build's business rather than this handler's. It is what
    /// the "no generated C# was found" diagnostic exists to say out loud, and what keying the binder
    /// on fully-qualified proto names is for: a declaration whose generated C# is a build behind
    /// still resolves, as long as its name did not move.
    /// </remarks>
    public bool Invalidate(string path, WatchedFileChange change)
    {
        if (!ProtoDocumentService.IsProtoFile(path))
            return false;

        ProtoDocumentService.Invalidate(path);
        return true;
    }
}
