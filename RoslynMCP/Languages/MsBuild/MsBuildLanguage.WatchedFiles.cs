using RoslynMCP.Languages.MsBuild.Core;

namespace RoslynMCP.Languages.MsBuild;

/// <summary>Dropping what a project file changing on disk makes wrong.</summary>
internal sealed partial class MsBuildLanguage : ILanguageWatchedFileHandler
{
    public bool Invalidate(string path, WatchedFileChange change)
    {
        if (MsBuildFile.KindOf(path) is MsBuildFileKind.None)
            return false;

        // The parse, always: a branch switch or a `dotnet add package` rewrote the file under us,
        // and the tree cached for it describes text that is no longer there.
        MsBuildDocumentCache.Invalidate(path);

        // The assembly list is per project and enumerated from disk once, so a project appearing or
        // disappearing is the only thing that can move it. Its target framework can change without
        // the file being created or deleted, but that is an edit to a buffer we will re-read anyway.
        if (change is not WatchedFileChange.Changed)
            FrameworkReferenceCatalog.Clear();

        // What the feeds say about a version is not a function of this file, so the status cache
        // survives. The references it holds answers for are mostly still here — a version was
        // edited, not the world.
        return true;
    }
}
